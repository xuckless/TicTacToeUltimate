using TicTacToeUltimate.Client.Abstractions;

namespace TicTacToeUltimate.Client.Services;

/// <summary>Holds the access token in memory and persists it in <see cref="ISecureStore"/> under <c>ttt.token</c>.</summary>
public sealed class TokenStore(ISecureStore secure)
{
    /// <summary>Storage key.</summary>
    public const string Key = "ttt.token";

    /// <summary>Current token or null.</summary>
    public string? Token { get; private set; }

    /// <summary>This install's device id (sent as X-Ttt-Device so bans cover devices; set at startup).</summary>
    public string? DeviceId { get; set; }

    /// <summary>Restores the persisted token.</summary>
    public async Task<string?> LoadAsync()
    {
        try { Token = await secure.GetAsync(Key); }
        catch (Exception) { Token = null; }
        return Token;
    }

    /// <summary>Sets the token; persists it unless <paramref name="persist"/> is false (tab-only dev impersonation).</summary>
    public async Task SetAsync(string token, bool persist = true)
    {
        Token = token;
        if (!persist) return;
        try { await secure.SetAsync(Key, token); } catch (Exception) { /* storage unavailable: keep in memory */ }
    }

    /// <summary>Clears the token everywhere.</summary>
    public async Task ClearAsync()
    {
        Token = null;
        try { await secure.RemoveAsync(Key); } catch (Exception) { /* ignore */ }
    }
}
