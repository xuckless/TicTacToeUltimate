using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Engine.Clocks;
using TicTacToeUltimate.Server.Abstractions;
using TicTacToeUltimate.Server.Abstractions.Persistence;
using TicTacToeUltimate.Server.Hosting;
using TicTacToeUltimate.Server.Platform;

namespace TicTacToeUltimate.Server.Tests.Persistence;

/// <summary>Fake host environment.</summary>
public sealed class FakeEnv(string name = "Development") : IHostEnvironment
{
    public string EnvironmentName { get; set; } = name;
    public string ApplicationName { get; set; } = "TicTacToeUltimate.Server";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

/// <summary>Recorded hub call.</summary>
public sealed record Sent(Guid UserId, string Method, object? Payload);

/// <summary>IUserNotifier fake that records every client call.</summary>
public sealed class RecordingNotifier : IUserNotifier
{
    private readonly Lock _gate = new();
    private readonly List<Sent> _log = [];

    public IReadOnlyList<Sent> Log { get { lock (_gate) return _log.ToList(); } }
    public IEnumerable<T> Of<T>(Guid user) => Log.Where(s => s.UserId == user && s.Payload is T).Select(s => (T)s.Payload!);

    public Task ToUserAsync(Guid userId, Func<IGameClient, Task> send) => send(new Client(userId, this));
    public async Task ToUsersAsync(IEnumerable<Guid> userIds, Func<IGameClient, Task> send)
    {
        foreach (var u in userIds.Distinct()) await send(new Client(u, this));
    }

    private void Add(Guid u, string m, object? p) { lock (_gate) _log.Add(new Sent(u, m, p)); }

    private sealed class Client(Guid u, RecordingNotifier n) : IGameClient
    {
        private Task R(string m, object? p) { n.Add(u, m, p); return Task.CompletedTask; }
        public Task Ping(int pingId) => R(nameof(Ping), pingId);
        public Task QueueStatus(QueueStatusDto status) => R(nameof(QueueStatus), status);
        public Task MatchFound(MatchFoundDto found) => R(nameof(MatchFound), found);
        public Task MatchEvent(MatchEventEnvelope envelope) => R(nameof(MatchEvent), envelope);
        public Task PostGameSummary(PostGameSummaryDto summary) => R(nameof(PostGameSummary), summary);
        public Task ChallengeReceived(ChallengeDto challenge) => R(nameof(ChallengeReceived), challenge);
        public Task ChallengeUpdated(ChallengeDto challenge) => R(nameof(ChallengeUpdated), challenge);
        public Task PresenceChanged(PresenceDto presence) => R(nameof(PresenceChanged), presence);
        public Task FriendRequestReceived(FriendRequestDto request) => R(nameof(FriendRequestReceived), request);
        public Task FriendsChanged() => R(nameof(FriendsChanged), null);
        public Task DirectMessage(DirectMessageDto message) => R(nameof(DirectMessage), message);
        public Task Notification(NotificationDto notification) => R(nameof(Notification), notification);
    }
}

/// <summary>In-process WP3 service graph (InMemory, Dev auth) with FakeTimeProvider and a recording notifier.</summary>
public sealed class TestPlatform : IDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    public FakeTimeProvider Time { get; } = new(Start);
    public RecordingNotifier Notifier { get; } = new();
    public ServiceProvider Services { get; }

    public TestPlatform(Dictionary<string, string?>? settings = null, string environment = "Development")
    {
        var values = new Dictionary<string, string?>
        {
            ["Ttt:Auth:Dev:Enabled"] = "true",
            ["Ttt:Auth:Dev:SigningKey"] = "test-signing-key-test-signing-key-0123456789",
            ["Ttt:Modes:Ranked:Rules"] = "n3k3-fifo7-v1-w0-pie", ["Ttt:Modes:Ranked:Rated"] = "true",
            ["Ttt:Modes:CasualX2:Rules"] = "n3k3-fifo7-v1-w0-none", ["Ttt:Modes:CasualX2:GuestsAllowed"] = "true",
            ["Ttt:Modes:Daily:Rules"] = "n3k3-fifo7-v1-w0-pie", ["Ttt:Modes:Daily:Rated"] = "true",
        };
        if (settings is not null) foreach (var kv in settings) values[kv.Key] = kv.Value;
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IConfiguration>(cfg);
        services.AddSingleton<TimeProvider>(Time);
        services.AddSingleton<IUserNotifier>(Notifier);
        services.AddOptions<TttOptions>().Bind(cfg.GetSection(TttOptions.Section));
        services.AddTttPlatform(cfg, new FakeEnv(environment));
        services.AddSingleton<ITablebaseCache, NullTablebaseCache>();
        services.AddSingleton<IAnalysisService, NullAnalysisService>();
        Services = services.BuildServiceProvider();
    }

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    /// <summary>Creates an account (or guest) user.</summary>
    public async Task<UserRecord> UserAsync(string? username, AccountKind kind = AccountKind.Account, string tz = "UTC")
    {
        var now = Time.GetUtcNow();
        var u = new UserRecord(Guid.NewGuid(), kind, username, username ?? "Guest#1234", "a1", null, tz, 1, username is null ? null : now, null, now, now);
        Assert.True(await Get<IUserRepository>().CreateAsync(u, CancellationToken.None));
        return u;
    }

    /// <summary>A completed match between two users.</summary>
    public static CompletedMatch Match(UserRecord a, UserRecord b, int? winnerSeat, MatchMode mode = MatchMode.Ranked, TimeControl tc = TimeControl.Ranked,
        bool rated = true, EndReason reason = EndReason.Line, DateTimeOffset? at = null, Guid? id = null)
    {
        var end = at ?? Start;
        var setup = new MatchSetup(id ?? Guid.NewGuid(), mode, tc, mode == MatchMode.Ranked ? RuleSet.RankedDefault : RuleSet.Default, rated,
            new SeatSetup(a.Id, a.DisplayName, a.Kind), new SeatSetup(b.Id, b.DisplayName, b.Kind), 42, MatchOrigin.Queue, null, null);
        var outcome = winnerSeat switch { 0 => Outcome.XWins, 1 => Outcome.OWins, _ => reason == EndReason.Aborted ? Outcome.None : Outcome.Draw };
        var actions = reason == EndReason.Aborted ? Array.Empty<StoredAction>()
            : [new StoredAction(0, 0, GameAction.Place(4), 1000, false, null, end.AddSeconds(-30)), new StoredAction(1, 1, GameAction.KeepSides, 900, false, null, end.AddSeconds(-20))];
        return new CompletedMatch(setup, false, actions, new MatchResultDto(outcome, winnerSeat, reason, [], rated, null), end.AddMinutes(-1), end);
    }

    public void Dispose() => Services.Dispose();
}
