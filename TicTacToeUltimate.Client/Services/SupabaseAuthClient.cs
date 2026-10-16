using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TicTacToeUltimate.Client.Services;

/// <summary>Supabase session (GoTrue).</summary>
public sealed record SupabaseSession(string AccessToken, string RefreshToken, long ExpiresAtMs);

/// <summary>Result of an email sign-up/sign-in.</summary>
public sealed record EmailAuthResult(SupabaseSession? Session, bool NeedsConfirmation, string? Error);

/// <summary>
/// Minimal GoTrue REST client: email one-time codes (the product sign-in), refresh, and the legacy password endpoints.
/// URL and publishable key come from <c>/api/config</c>; nothing here is secret.
/// </summary>
public sealed class SupabaseAuthClient(HttpClient http)
{
    private sealed record TokenBody(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);

    /// <summary>POST /auth/v1/signup. No session comes back while the email is unconfirmed.</summary>
    public Task<EmailAuthResult> SignUpAsync(string url, string key, string email, string password, string? redirectTo)
        => SendAsync(url, key, "signup" + (redirectTo is null ? "" : "?redirect_to=" + Uri.EscapeDataString(redirectTo)),
            new { email, password }, signUp: true);

    /// <summary>POST /auth/v1/token?grant_type=password.</summary>
    public Task<EmailAuthResult> SignInAsync(string url, string key, string email, string password)
        => SendAsync(url, key, "token?grant_type=password", new { email, password }, signUp: false);

    /// <summary>POST /auth/v1/otp: emails a one-time code (creates the user on first use). Null = sent.</summary>
    public async Task<string?> SendCodeAsync(string url, string key, string email)
    {
        HttpResponseMessage r;
        try { r = await SendRawAsync(url, key, "otp", new { email, create_user = true }); }
        catch (HttpRequestException) { return "auth.service_unreachable"; }
        return r.IsSuccessStatusCode ? null : await ErrorOf(r);
    }

    /// <summary>POST /auth/v1/verify (type=email): exchanges the 6-digit code for a session.</summary>
    public Task<EmailAuthResult> VerifyCodeAsync(string url, string key, string email, string code)
        => SendAsync(url, key, "verify", new { type = "email", email, token = code }, signUp: false);

    /// <summary>POST /auth/v1/token?grant_type=refresh_token.</summary>
    public Task<EmailAuthResult> RefreshAsync(string url, string key, string refreshToken)
        => SendAsync(url, key, "token?grant_type=refresh_token", new { refresh_token = refreshToken }, signUp: false);

    /// <summary>POST /auth/v1/recover: sends a password reset email.</summary>
    public async Task<string?> RecoverAsync(string url, string key, string email, string? redirectTo)
    {
        var r = await SendRawAsync(url, key, "recover" + (redirectTo is null ? "" : "?redirect_to=" + Uri.EscapeDataString(redirectTo)), new { email });
        return r.IsSuccessStatusCode ? null : await ErrorOf(r);
    }

    /// <summary>PUT /auth/v1/user: sets a new password (after a recovery link signed the user in).</summary>
    public async Task<string?> UpdatePasswordAsync(string url, string key, string accessToken, string password)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, Endpoint(url, "user")) { Content = JsonContent.Create(new { password }) };
        req.Headers.Add("apikey", key);
        req.Headers.Authorization = new("Bearer", accessToken);
        var r = await http.SendAsync(req);
        return r.IsSuccessStatusCode ? null : await ErrorOf(r);
    }

    /// <summary>Reads a session from a confirmation/recovery redirect fragment (#access_token=…&amp;refresh_token=…).</summary>
    public static SupabaseSession? FromFragment(string? fragment, long nowMs)
    {
        if (string.IsNullOrEmpty(fragment)) return null;
        var q = System.Web.HttpUtility.ParseQueryString(fragment.TrimStart('#'));
        var at = q["access_token"];
        var rt = q["refresh_token"];
        if (string.IsNullOrEmpty(at) || string.IsNullOrEmpty(rt)) return null;
        _ = int.TryParse(q["expires_in"], out var exp);
        return new SupabaseSession(at, rt, nowMs + Math.Max(60, exp) * 1000L);
    }

    private async Task<EmailAuthResult> SendAsync(string url, string key, string path, object body, bool signUp)
    {
        HttpResponseMessage r;
        try { r = await SendRawAsync(url, key, path, body); }
        catch (HttpRequestException) { return new(null, false, "auth.service_unreachable"); }
        if (!r.IsSuccessStatusCode) return new(null, false, await ErrorOf(r));
        var t = await r.Content.ReadFromJsonAsync<TokenBody>();
        if (t?.AccessToken is { Length: > 0 } at && t.RefreshToken is { Length: > 0 } rt)
            return new(new SupabaseSession(at, rt, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + Math.Max(60, t.ExpiresIn) * 1000L), false, null);
        return signUp ? new(null, true, null) : new(null, false, "auth.sign_in_failed");
    }

    private async Task<HttpResponseMessage> SendRawAsync(string url, string key, string path, object body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint(url, path)) { Content = JsonContent.Create(body) };
        req.Headers.Add("apikey", key);
        return await http.SendAsync(req);
    }

    private static Uri Endpoint(string url, string path) => new(url.TrimEnd('/') + "/auth/v1/" + path);

    private static async Task<string> ErrorOf(HttpResponseMessage r)
    {
        try
        {
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            var code = root.TryGetProperty("error_code", out var c) ? c.GetString() : root.TryGetProperty("error", out var e) ? e.GetString() : null;
            var msg = root.TryGetProperty("msg", out var m) ? m.GetString() : root.TryGetProperty("error_description", out var d) ? d.GetString() : null;
            return code switch
            {
                "invalid_credentials" or "invalid_grant" => "auth.wrong_email_or_password",
                "email_not_confirmed" => "auth.confirm_email_first",
                "user_already_exists" or "email_exists" => "auth.email_exists",
                "weak_password" => "auth.weak_password",
                "over_email_send_rate_limit" or "over_request_rate_limit" => "auth.too_many_attempts",
                "validation_failed" or "email_address_invalid" => "auth.email_invalid",
                _ => msg ?? "auth.sign_in_failed",
            };
        }
        catch (JsonException) { return "auth.sign_in_failed"; }
    }
}
