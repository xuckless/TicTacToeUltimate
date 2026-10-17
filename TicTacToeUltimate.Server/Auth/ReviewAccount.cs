using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Server.Hosting;

namespace TicTacToeUltimate.Server.Auth;

/// <summary>
/// The App Store / Play reviewer account (<c>Ttt:Auth:Review</c>). Reviewers can't read our sign-in emails, so this one address
/// signs in with a fixed secret code: the server checks it and signs in to Supabase with the account's secret password. At
/// startup the Supabase user is created (email confirmed) when missing. Every other email still needs an emailed code.
/// </summary>
public sealed class ReviewAccount(IHttpClientFactory http, IOptions<TttOptions> options, SupabaseAdminClient admin, ILogger<ReviewAccount> log)
    : IHostedService
{
    private ReviewAuthOptions Review => options.Value.Auth.Review;
    private SupabaseAuthOptions Supabase => options.Value.Auth.Supabase;
    private bool Enabled => Review.Enabled && Supabase.Enabled && Supabase.Url.Length > 0;

    /// <summary>The session for the reviewer, or null when the email/code don't match (or review sign-in is off).</summary>
    public async Task<ReviewSessionDto?> SignInAsync(ReviewSignInDto req, CancellationToken ct)
    {
        if (!Enabled) return null;
        var emailOk = string.Equals(req.Email?.Trim(), Review.Email.Trim(), StringComparison.OrdinalIgnoreCase);
        var codeOk = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(req.Code?.Trim() ?? ""), Encoding.UTF8.GetBytes(Review.Code.Trim()));
        if (!emailOk || !codeOk) return null;
        return await PasswordGrantAsync(ct).ConfigureAwait(false);
    }

    private async Task<ReviewSessionDto?> PasswordGrantAsync(CancellationToken ct)
    {
        using var client = http.CreateClient(nameof(SupabaseAdminClient));
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Supabase.Url.TrimEnd('/')}/auth/v1/token?grant_type=password")
        {
            Content = JsonContent.Create(new { email = Review.Email.Trim().ToLowerInvariant(), password = Review.Password }),
        };
        req.Headers.Add("apikey", Supabase.AnonKey);
        try
        {
            using var res = await client.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) { log.LogWarning("Review sign-in: Supabase password grant returned {Status}", (int)res.StatusCode); return null; }
            var t = await res.Content.ReadFromJsonAsync<GrantResponse>(ct).ConfigureAwait(false);
            return t is { AccessToken: { Length: > 0 } at, RefreshToken: { Length: > 0 } rt } ? new ReviewSessionDto(at, rt, t.ExpiresIn) : null;
        }
        catch (HttpRequestException ex) { log.LogWarning(ex, "Review sign-in failed"); return null; }
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!Enabled) return;
        // Off the startup path: Supabase may be slow; the account only needs to exist before a reviewer arrives.
        _ = Task.Run(async () =>
        {
            try
            {
                if (await PasswordGrantAsync(CancellationToken.None).ConfigureAwait(false) is not null) return;
                if (await admin.CreateConfirmedUserAsync(Review.Email.Trim().ToLowerInvariant(), Review.Password, CancellationToken.None).ConfigureAwait(false))
                    log.LogInformation("Review account {Email} created", Review.Email);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { log.LogWarning(ex, "Review account setup failed"); }
        }, CancellationToken.None);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private sealed record GrantResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
