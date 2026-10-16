using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging;
using TicTacToeUltimate.Client.Abstractions;
using TicTacToeUltimate.Client.Services;
using TicTacToeUltimate.Client.State;
using TicTacToeUltimate.Contracts;

namespace TicTacToeUltimate.Client.Stores;

/// <summary>Auth, config, connection and Hello. Token in <c>ttt.token</c> (secure), guest device id in <c>ttt.deviceId</c>.</summary>
public sealed class SessionStore : StoreBase<SessionState>, ISessionStore
{
    /// <summary>Key of the guest device id.</summary>
    public const string DeviceIdKey = "ttt.deviceId";

    private readonly TttApiClient _api;
    private readonly SupabaseAuthClient _supa;
    private const string RefreshKey = "ttt.refresh";
    private CancellationTokenSource? _refreshTimer;
    private readonly IRealtimeTransport _rt;
    private readonly TokenStore _tokens;
    private readonly IKeyValueStore _kv;
    private readonly ClientOptions _options;
    private readonly ILogger<SessionStore> _logger;
    private readonly Lock _initGate = new();
    private Task? _init;
    private string? _devAs;
    private int _remintInFlight;
    private volatile bool _wasGuest;
    private const string KindKey = "ttt.kind";
    /// <summary>How long startup waits for the server before going offline.</summary>
    public static int WakeBudgetSeconds { get; set; } = 60;

    /// <summary>Raised after each successful Hello.</summary>
    public event Action<ServerHelloDto>? HelloReceived;

    /// <summary>Creates the store.</summary>
    private readonly IPushService? _push;
    private bool _pushRegistered;

    public SessionStore(TttApiClient api, IRealtimeTransport rt, TokenStore tokens, IKeyValueStore kv, ClientOptions options, ILogger<SessionStore> logger,
        SupabaseAuthClient? supa = null, IPushService? push = null)
        : base(SessionState.Initial)
    {
        _supa = supa ?? new SupabaseAuthClient(new HttpClient());
        _push = push;
        _api = api; _rt = rt; _tokens = tokens; _kv = kv; _options = options; _logger = logger;
        _rt.Handshake = HelloAsync;
        _rt.PhaseChanged += () => Update(s => s with { Connection = _rt.Phase, ReconnectAttempt = _rt.ReconnectAttempt });
        _api.Unauthorized += OnUnauthorized;
    }

    /// <summary>Dev only: sign this tab in as <paramref name="username"/> without persisting the token (the <c>?as=</c> query).</summary>
    public void UseDevImpersonation(string username) => _devAs = string.IsNullOrWhiteSpace(username) ? null : username.Trim();

    /// <inheritdoc />
    public Task InitializeAsync()
    {
        lock (_initGate) return _init ??= InitCoreAsync();
    }

    private async Task InitCoreAsync()
    {
        // The server can be restarting (deploy, VM reboot). Keep trying for up to a minute, showing a "waking up"
        // buffer, before falling back to offline mode.
        var started = DateTime.UtcNow;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                var config = await _api.GetConfigAsync(cts.Token);
                Update(s => s with { Config = config, Error = null, WakingSeconds = 0 });
                break;
            }
            catch (TttApiException ex) when ((int)ex.Status < 500)
            {
                _logger.LogWarning(ex, "Config load failed");
                Update(s => s with { Error = "offline", WakingSeconds = 0 });
                break;
            }
            catch (Exception ex)
            {
                var waited = (int)(DateTime.UtcNow - started).TotalSeconds;
                if (waited >= WakeBudgetSeconds)
                {
                    _logger.LogWarning(ex, "Config load failed");
                    Update(s => s with { Error = "offline", WakingSeconds = 0 });
                    break;
                }
                Update(s => s with { WakingSeconds = Math.Max(1, waited) });
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(4, 1 + attempt)));
            }
        }

        if (_devAs is { } devAs && (_options.IsDevelopment || State.Config?.DevAuth == true))
        {
            try
            {
                var users = await _api.GetDevUsersAsync();
                var match = users.FirstOrDefault(u => string.Equals(u.DisplayName, devAs, StringComparison.OrdinalIgnoreCase));
                var token = match is not null
                    ? await _api.DevTokenAsync(new DevTokenRequestDto(match.UserId, null))
                    : await _api.DevTokenAsync(new DevTokenRequestDto(null, devAs));
                await CompleteSignInAsync(token, persist: false);
                return;
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Dev impersonation failed"); }
        }

        _wasGuest = await _kv.GetAsync(KindKey) == nameof(AccountKind.Guest);
        var stored = await _tokens.LoadAsync();
        // Email accounts: Supabase access tokens last an hour, so start from a fresh one.
        if (!_wasGuest && await _kv.GetAsync(RefreshKey) is { Length: > 0 } && await TryRefreshAsync()) stored = _tokens.Token;
        if (string.IsNullOrEmpty(stored))
        {
            Update(s => s with { Status = SessionStatus.SignedOut });
            return;
        }
        try
        {
            var me = await _api.GetMeAsync();
            Update(s => s with { Status = SessionStatus.SignedIn, Me = me, Error = null });
            await _rt.StartAsync();
        }
        catch (TttApiException e) when (e.Status == HttpStatusCode.Unauthorized)
        {
            // OnUnauthorized handles guest re-mint / sign-out.
        }
        catch (Exception ex)
        {
            // Offline with a token: stay signed in optimistically and keep trying to connect.
            _logger.LogInformation(ex, "Session restore offline");
            Update(s => s with { Status = SessionStatus.SignedIn, Error = "offline" });
            await _rt.StartAsync();
        }
    }

    private async Task HelloAsync()
    {
        var tz = TimeZoneInfo.Local.Id;
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(tz, out var iana)) tz = iana;
        var hello = await _rt.Hello(new ClientHelloDto(ProtocolInfo.Version, _options.ClientVersion, _options.Platform, tz,
            CultureInfo.CurrentCulture.Name));
        Update(s => s with
        {
            Me = hello.Me, Status = SessionStatus.SignedIn, ActiveMatchIds = [.. hello.ActiveMatchIds],
            DailyYourTurn = hello.DailyYourTurn, Error = null,
        });
        HelloReceived?.Invoke(hello);
        if (!_pushRegistered) { _pushRegistered = true; _ = RegisterPushAsync(); }
    }

    /// <summary>
    /// Push: ask once (from the 2nd launch on, so the first thing a new player sees is the game, not a permission prompt),
    /// then send the device token so Bert can nudge about streaks, challenges and messages.
    /// </summary>
    private async Task RegisterPushAsync()
    {
        if (_push is null || _options.Platform is ClientPlatform.Web or ClientPlatform.MacCatalyst) return;
        try
        {
            var launches = int.TryParse(await _kv.GetAsync("ttt.launches"), out var n) ? n + 1 : 1;
            await _kv.SetAsync("ttt.launches", launches.ToString(CultureInfo.InvariantCulture));
            if (launches < 2) return;
            if (!await _push.RequestPermissionAsync()) return;
            var token = await _push.GetTokenAsync();
            if (string.IsNullOrEmpty(token)) return;
            await _api.RegisterPushAsync(new RegisterPushDto(_options.Platform == ClientPlatform.Android ? "android" : "ios", token));
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Push registration skipped"); }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DevUserDto>> GetDevUsersAsync() => _api.GetDevUsersAsync();

    /// <inheritdoc />
    public async Task SignInDevAsync(Guid userId) => await CompleteSignInAsync(await _api.DevTokenAsync(new DevTokenRequestDto(userId, null)), true);

    /// <inheritdoc />
    public async Task SignInDevNewUserAsync(string username)
        => await CompleteSignInAsync(await _api.DevTokenAsync(new DevTokenRequestDto(null, username)), true);

    /// <inheritdoc />
    public async Task SignInGuestAsync() => await CompleteSignInAsync(await _api.GuestTokenAsync(new GuestTokenRequestDto(await DeviceIdAsync())), true);

    private bool IsReviewEmail(string email) =>
        State.Config?.ReviewEmail is { Length: > 0 } review && string.Equals(email.Trim(), review, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async Task<string?> SendCodeAsync(string email)
    {
        if (Supabase() is not { } s) return "Email sign-in isn't available right now.";
        if (IsReviewEmail(email)) return null;   // the reviewer types the fixed code; no email is sent
        return await _supa.SendCodeAsync(s.Url, s.Key, email.Trim().ToLowerInvariant());
    }

    /// <inheritdoc />
    public async Task<string?> VerifyCodeAsync(string email, string code)
    {
        if (Supabase() is not { } s) return "Email sign-in isn't available right now.";
        if (IsReviewEmail(email))
        {
            try
            {
                var rs = await _api.ReviewSignInAsync(new ReviewSignInDto(email.Trim(), code.Trim()));
                if (rs is null) return "That code didn't work. Check it and try again.";
                return await AdoptSupabaseSessionAsync(new SupabaseSession(rs.AccessToken, rs.RefreshToken,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + Math.Max(60, rs.ExpiresIn) * 1000L));
            }
            catch (TttApiException) { return "That code didn't work. Check it and try again."; }
        }
        var r = await _supa.VerifyCodeAsync(s.Url, s.Key, email.Trim().ToLowerInvariant(), code.Trim());
        if (r.Session is null) return r.Error ?? "That code didn't work. Check it and try again.";
        return await AdoptSupabaseSessionAsync(r.Session);
    }

    /// <inheritdoc />
    public async Task<string?> SignInEmailAsync(string email, string password)
    {
        if (Supabase() is not { } s) return "Email sign-in isn't available right now.";
        var r = await _supa.SignInAsync(s.Url, s.Key, email.Trim(), password);
        if (r.Session is null) return r.Error ?? "Sign-in failed.";
        return await AdoptSupabaseSessionAsync(r.Session);
    }

    /// <inheritdoc />
    public async Task<(bool NeedsConfirmation, string? Error)> SignUpEmailAsync(string email, string password)
    {
        if (Supabase() is not { } s) return (false, "Email sign-up isn't available right now.");
        var r = await _supa.SignUpAsync(s.Url, s.Key, email.Trim(), password, RedirectUrl());
        if (r.Error is not null) return (false, r.Error);
        if (r.Session is null) return (true, null);
        return (false, await AdoptSupabaseSessionAsync(r.Session));
    }

    /// <inheritdoc />
    public async Task<string?> ResetPasswordAsync(string email)
    {
        if (Supabase() is not { } s) return "Password reset isn't available right now.";
        return await _supa.RecoverAsync(s.Url, s.Key, email.Trim(), RedirectUrl());
    }

    /// <inheritdoc />
    public async Task<bool> CompleteRedirectAsync(string fragment)
    {
        if (SupabaseAuthClient.FromFragment(fragment, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) is not { } session) return false;
        if (State.Config is null) await InitializeAsync();
        return await AdoptSupabaseSessionAsync(session) is null;
    }

    /// <inheritdoc />
    public async Task<string?> SetPasswordAsync(string password)
    {
        if (Supabase() is not { } s || _tokens.Token is not { } at) return "Sign in first.";
        return await _supa.UpdatePasswordAsync(s.Url, s.Key, at, password);
    }

    private (string Url, string Key)? Supabase()
        => State.Config is { SupabaseAuth: true, SupabaseUrl: { Length: > 0 } url, SupabaseAnonKey: { Length: > 0 } key } ? (url, key) : null;

    private string RedirectUrl() => new Uri(_options.WebBaseUrl, "signin").ToString();

    // Switch to the Supabase session, then move this device's guest history into the account.
    private async Task<string?> AdoptSupabaseSessionAsync(SupabaseSession session)
    {
        var guestToken = (State.IsGuest || _wasGuest) ? _tokens.Token ?? await _tokens.LoadAsync() : null;
        await _tokens.SetAsync(session.AccessToken, true);
        await _kv.SetAsync(RefreshKey, session.RefreshToken);
        ScheduleRefresh(session.ExpiresAtMs);
        MeDto me;
        try { me = await _api.GetMeAsync(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Account load failed"); return "Signed in, but the game server didn't answer. Try again."; }
        if (guestToken is not null && me.Kind == AccountKind.Account)
        {
            try { await _api.ClaimGuestAsync(new ClaimGuestDto(guestToken)); me = await _api.GetMeAsync(); }
            catch (Exception ex) { _logger.LogInformation(ex, "Guest history claim skipped"); }
        }
        await CompleteSignInAsync(new TokenResponseDto(session.AccessToken, session.ExpiresAtMs, me), true);
        return null;
    }

    private async Task<bool> TryRefreshAsync()
    {
        if (await _kv.GetAsync(RefreshKey) is not { Length: > 0 } rt) return false;
        if (State.Config is null) return false;
        if (Supabase() is not { } s) return false;
        var r = await _supa.RefreshAsync(s.Url, s.Key, rt);
        if (r.Session is null) return false;
        await _tokens.SetAsync(r.Session.AccessToken, true);
        await _kv.SetAsync(RefreshKey, r.Session.RefreshToken);
        ScheduleRefresh(r.Session.ExpiresAtMs);
        return true;
    }

    private void ScheduleRefresh(long expiresAtMs)
    {
        _refreshTimer?.Cancel();
        var cts = _refreshTimer = new CancellationTokenSource();
        var delay = TimeSpan.FromMilliseconds(Math.Max(30_000, expiresAtMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 120_000));
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(delay, cts.Token); } catch (OperationCanceledException) { return; }
            if (!await TryRefreshAsync()) _logger.LogInformation("Token refresh failed; will retry on the next 401");
        });
    }

    /// <inheritdoc />
    public async Task SignOutAsync()
    {
        _refreshTimer?.Cancel();
        await _kv.SetAsync(RefreshKey, "");
        await _rt.StopAsync();
        await _tokens.ClearAsync();
        Update(s => s with { Status = SessionStatus.SignedOut, Me = null, ActiveMatchIds = [], DailyYourTurn = 0, Error = null });
    }

    /// <inheritdoc />
    public async Task<string?> DeleteAccountAsync()
    {
        try { await _api.DeleteAccountAsync(); }
        catch (Exception ex) { return ErrorText.Code(ex); }
        await _kv.RemoveAsync(DeviceIdKey);   // a fresh guest identity next time; the old id is dead server-side
        await SignOutAsync();
        return null;
    }

    /// <inheritdoc />
    public async Task<CommandAckDto> SetUsernameAsync(string username, bool acceptTos)
    {
        try
        {
            var me = await _api.SetUsernameAsync(new SetUsernameDto(username, acceptTos));
            Update(s => s with { Me = me });
            return new CommandAckDto(true, null);
        }
        catch (Exception ex) { return new CommandAckDto(false, ErrorText.Code(ex)); }
    }

    /// <inheritdoc />
    public async Task<CommandAckDto> UpdateProfileAsync(UpdateProfileDto patch)
    {
        try
        {
            var me = await _api.UpdateProfileAsync(patch);
            Update(s => s with { Me = me });
            return new CommandAckDto(true, null);
        }
        catch (Exception ex) { return new CommandAckDto(false, ErrorText.Code(ex)); }
    }

    private async Task CompleteSignInAsync(TokenResponseDto token, bool persist)
    {
        await _rt.StopAsync();
        await _tokens.SetAsync(token.AccessToken, persist);
        _wasGuest = token.Me.Kind == AccountKind.Guest;
        if (persist) await _kv.SetAsync(KindKey, token.Me.Kind.ToString());
        Update(s => s with { Status = SessionStatus.SignedIn, Me = token.Me, Error = null });
        await _rt.StartAsync();
    }

    private async Task<string> DeviceIdAsync()
    {
        var id = await _kv.GetAsync(DeviceIdKey);
        if (!string.IsNullOrEmpty(id)) return id;
        id = Guid.NewGuid().ToString();
        await _kv.SetAsync(DeviceIdKey, id);
        return id;
    }

    private void OnUnauthorized()
    {
        if (Interlocked.Exchange(ref _remintInFlight, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                if (!State.IsGuest && !_wasGuest && await TryRefreshAsync())
                {
                    var me = await _api.GetMeAsync();
                    Update(s => s with { Status = SessionStatus.SignedIn, Me = me, Error = null });
                }
                else if (State.IsGuest || (State.Me is null && _wasGuest))
                {
                    await CompleteSignInAsync(await _api.GuestTokenAsync(new GuestTokenRequestDto(await DeviceIdAsync())), true);
                }
                else
                {
                    await SignOutAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Re-authentication failed");
                await SignOutAsync();
            }
            finally { Interlocked.Exchange(ref _remintInFlight, 0); }
        });
    }
}
