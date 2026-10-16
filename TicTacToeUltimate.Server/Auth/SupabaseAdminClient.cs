using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using TicTacToeUltimate.Server.Hosting;

namespace TicTacToeUltimate.Server.Auth;

/// <summary>
/// Supabase Auth admin API (service role). Used only to delete the auth user when a player deletes their account, so the
/// email can never sign in to the anonymised player again. Needs <c>Ttt:Auth:Supabase:ServiceRoleKey</c> (a secret, never in
/// appsettings); without it the player row is still anonymised and a warning is logged.
/// </summary>
public sealed class SupabaseAdminClient(IHttpClientFactory http, IOptions<TttOptions> options, ILogger<SupabaseAdminClient> log)
{
    /// <summary>True when Supabase auth is on and a service-role key is configured.</summary>
    public bool Enabled => options.Value.Auth.Supabase is { Enabled: true, ServiceRoleKey: { Length: > 0 }, Url: { Length: > 0 } };

    /// <summary>Deletes the auth user; true when deleted or already gone.</summary>
    public async Task<bool> DeleteUserAsync(Guid userId, CancellationToken ct)
    {
        var s = options.Value.Auth.Supabase;
        if (!Enabled)
        {
            if (s.Enabled) log.LogWarning("Account {User} deleted but Ttt:Auth:Supabase:ServiceRoleKey is not set; delete the auth user manually", userId);
            return false;
        }
        using var client = http.CreateClient(nameof(SupabaseAdminClient));
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"{s.Url.TrimEnd('/')}/auth/v1/admin/users/{userId}");
        req.Headers.Add("apikey", s.ServiceRoleKey);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.ServiceRoleKey);
        try
        {
            using var res = await client.SendAsync(req, ct).ConfigureAwait(false);
            if (res.IsSuccessStatusCode || res.StatusCode == HttpStatusCode.NotFound) return true;
            log.LogWarning("Supabase auth user {User} delete returned {Status}", userId, (int)res.StatusCode);
            return false;
        }
        catch (HttpRequestException ex)
        {
            log.LogWarning(ex, "Supabase auth user {User} delete failed", userId);
            return false;
        }
    }
}
