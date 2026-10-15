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

    /// <summary>Raised after each successful Hello.</summary>
    public event Action<ServerHelloDto>? HelloReceived;

    /// <summary>Creates the store.</summary>
    public SessionStore(TttApiClient api, IRealtimeTransport rt, TokenStore tokens, IKeyValueStore kv, ClientOptions options, ILogger<SessionStore> logger)
        : base(SessionState.Initial)
    {
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
        try
        {
            var config = await _api.GetConfigAsync();
            Update(s => s with { Config = config, Error = null });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Config load failed");
            Update(s => s with { Error = "offline" });
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

    /// <inheritdoc />
    public async Task SignOutAsync()
    {
        await _rt.StopAsync();
        await _tokens.ClearAsync();
        Update(s => s with { Status = SessionStatus.SignedOut, Me = null, ActiveMatchIds = [], DailyYourTurn = 0, Error = null });
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
                if (State.IsGuest || (State.Me is null && _wasGuest))
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
