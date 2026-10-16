using Microsoft.Extensions.Options;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Engine.Clocks;
using TicTacToeUltimate.Server.Abstractions;
using TicTacToeUltimate.Server.Abstractions.Persistence;
using TicTacToeUltimate.Server.Hosting;
using TicTacToeUltimate.Server.Matches;
using TicTacToeUltimate.Server.Progression;

namespace TicTacToeUltimate.Server.Persistence.InMemory;

/// <summary>Deterministic dev fixtures (§6.7). Seeded at startup when Persistence:SeedFixtures and Provider=InMemory.</summary>
public static class DevFixtures
{
    /// <summary>Fixture id 00000000-0000-0000-0000-0000000000NN.</summary>
    public static Guid Id(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:D12}");

    public static readonly Guid AliXo = Id(1), Kestrel = Id(2), Mira = Id(3), Jules = Id(4), Tess = Id(5), Rook = Id(6), Pixel = Id(7), Ned = Id(8);

    /// <summary>Fixed id of the pending request 06 → 01.</summary>
    public static readonly Guid RookRequestId = Guid.Parse("00000000-0000-0000-0001-000000000601");

    /// <summary>Code of the open targeted CasualX2 challenge 06 → 01.</summary>
    public const string RookChallengeCode = "ROOK06";

    /// <summary>(id, username, ranked rating, ranked games, rd, country, time zone).</summary>
    public static readonly (Guid Id, string Username, double Rating, int Games, double Rd, string? Country, string Tz)[] Users =
    [
        (AliXo, "ali_xo", 1552, 412, 60, "CA", "America/Toronto"),
        (Kestrel, "kestrel_99", 1612, 233, 65, "US", "America/New_York"),
        (Mira, "mira_o", 2031, 518, 55, "GB", "Europe/London"),
        (Jules, "jules_x", 1342, 97, 80, "FR", "Europe/Paris"),
        (Tess, "tic_tac_tess", 1180, 64, 90, "US", "America/Los_Angeles"),
        (Rook, "rook_raider", 1490, 151, 70, "DE", "Europe/Berlin"),
        (Pixel, "pixel_pawn", 1405, 120, 75, "AU", "Australia/Sydney"),
        (Ned, "newbie_ned", 1200, 3, 290, null, "UTC"),
    ];

    /// <summary>Seeds all fixtures through the repository interfaces. Idempotent (skips when ali_xo exists).</summary>
    public static async Task SeedAsync(IServiceProvider sp, DateTimeOffset now, CancellationToken ct)
    {
        var users = sp.GetRequiredService<IUserRepository>();
        if (await users.GetAsync(AliXo, ct) is not null) return;
        var ratings = sp.GetRequiredService<IRatingRepository>();
        var friends = sp.GetRequiredService<IFriendRepository>();
        var progress = sp.GetRequiredService<IProgressRepository>();
        var challenges = sp.GetRequiredService<IChallengeRepository>();
        var games = sp.GetRequiredService<IGameRepository>();
        var opts = sp.GetRequiredService<IOptions<TttOptions>>().Value;

        var created = now.AddDays(-400);
        foreach (var (id, name, rating, count, rd, country, tz) in Users)
        {
            await users.CreateAsync(new UserRecord(id, AccountKind.Account, name, name, "a" + (id.GetHashCode() % 8 + 8) % 8, country, tz, 1,
                created, null, created, now), ct);
            var wins = (int)(count * 0.52);
            var draws = count / 20;
            await ratings.PutAsync(new RatingRecord(id, RatingPool.Ranked, rating, rd, 0.06, count, wins, count - wins - draws, draws, rating + 40,
                now.AddHours(-3)), ct);
            // Headline "Ranked" is Arena since the 2026-10-15 restructure; fixtures mirror their classic numbers into it.
            await ratings.PutAsync(new RatingRecord(id, RatingPools.Headline, rating, rd, 0.06, count, wins, count - wins - draws, draws, rating + 40,
                now.AddHours(-3)), ct);
        }
        await ratings.PutAsync(new RatingRecord(AliXo, RatingPool.Daily, 1610, 70, 0.06, 58, 33, 22, 3, 1650, now.AddDays(-1)), ct);

        foreach (var f in new[] { Kestrel, Mira, Jules, Tess }) await friends.RequestAsync(AliXo, f, created, ct);
        foreach (var f in new[] { Kestrel, Mira, Jules, Tess }) await friends.RequestAsync(f, AliXo, created, ct);   // mutual → auto-accept
        if (friends is InMemoryFriendRepository mem) mem.SeedRequest(new FriendRequestRecord(RookRequestId, Rook, AliXo, now.AddHours(-5)));
        else await friends.RequestAsync(Rook, AliXo, now.AddHours(-5), ct);

        var casualRules = RuleIds.ParseOrDefault(opts.Modes.TryGetValue("CasualX2", out var m) ? m.Rules : null);
        await challenges.CreateAsync(new ChallengeRecord(RookChallengeCode, Rook, AliXo, TimeControl.CasualX2, casualRules.Id, ChallengeStatus.Open,
            null, null, now.AddHours(-1), now.AddHours(Math.Max(1, opts.Challenges.TtlHours) - 1)), ct);

        var aliDay = LocalDays.Day(now, "America/Toronto");
        await progress.PutStreakAsync(new StreakRecord(AliXo, 12, 31, aliDay, 1), ct);
        await progress.PutQuestDayAsync(new QuestDayRecord(AliXo, aliDay, 2, false, null, false), ct);

        // ~12 finished games per fixture via deterministic playouts (6 pairings × 16 games = 96 seats... each user ≈ 12 games).
        var ids = Users.Select(u => u.Id).ToArray();
        var rnd = new SplitMix64(20261015);
        for (var i = 0; i < 48; i++)
        {
            var a = ids[i % ids.Length];
            var b = ids[(i + 1 + i / ids.Length) % ids.Length];
            if (a == b) b = ids[(i + 3) % ids.Length];
            var ranked = i % 3 == 0;
            var tc = ranked ? TimeControl.Ranked : TimeControl.CasualX2;
            var rules = ranked ? RuleIds.ParseOrDefault(opts.Modes.TryGetValue("Ranked", out var rm) ? rm.Rules : null) : casualRules;
            var seed = rnd.Next();
            var end = now.AddHours(-(i * 7 + 2));
            var setup = new MatchSetup(Guid.Parse($"00000000-0000-0000-0002-{i + 1:D12}"), ranked ? MatchMode.Ranked : MatchMode.Casual, tc, rules, ranked,
                Seat(a), Seat(b), seed, MatchOrigin.Queue, null, null);
            var (actions, swapped, result) = FixturePlayout.Play(rules, seed, ranked, end.AddMinutes(-2));
            await games.SaveCompletedAsync(new GameRecord(setup.MatchId, setup, swapped, actions, result, end.AddMinutes(-2), end, []), ct);
        }

        static SeatSetup Seat(Guid id) => new(id, Users.First(u => u.Id == id).Username, AccountKind.Account);
    }
}

/// <summary>Deterministic random playouts for fixtures. Uses the engine.</summary>
public static class FixturePlayout
{
    /// <summary>Plays one game. Timeouts end games that exceed 60 plies.</summary>
    public static (IReadOnlyList<StoredAction> Actions, bool Swapped, MatchResultDto Result) Play(RuleSet rules, ulong seed, bool rated, DateTimeOffset start)
    {
        var rng = new SplitMix64(seed);
        var state = Rules.Initial(rules);
        var actions = new List<StoredAction>();
        while (state.Phase != GamePhase.Ended && actions.Count < 60)
        {
            var legal = Rules.LegalActions(state);
            if (legal.Length == 0) break;
            var action = legal[rng.NextInt(legal.Length)];
            var seat = SeatOf(state.ToMove, state.Swapped);
            var r = Rules.Apply(state, action);
            actions.Add(new StoredAction(state.Ply, seat, action, 800 + rng.NextInt(4000), false, r.VanishedCell, start.AddSeconds(actions.Count * 3)));
            state = r.State;
        }
        return (actions, state.Swapped, Result(state.Outcome, state.WinLine.ToArray(), state.ToMove, state.Swapped, rated));
    }

    private static int SeatOf(Color c, bool swapped) => (c == Color.X) != swapped ? 0 : 1;

    private static MatchResultDto Result(Outcome o, int[] line, Color toMove, bool swapped, bool rated) => o switch
    {
        Outcome.XWins => new(o, SeatOf(Color.X, swapped), EndReason.Line, line, rated, null),
        Outcome.OWins => new(o, SeatOf(Color.O, swapped), EndReason.Line, line, rated, null),
        Outcome.Draw => new(o, null, EndReason.Draw, [], rated, null),
        _ => toMove == Color.X
            ? new(Outcome.OWins, SeatOf(Color.O, swapped), EndReason.Timeout, [], rated, null)
            : new(Outcome.XWins, SeatOf(Color.X, swapped), EndReason.Timeout, [], rated, null),
    };
}

/// <summary>Seeds fixtures at startup (InMemory only).</summary>
public sealed class FixtureSeeder(IServiceProvider sp, IOptions<TttOptions> options, TimeProvider time, ILogger<FixtureSeeder> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var p = options.Value.Persistence;
        if (!p.SeedFixtures || !string.Equals(p.Provider, "InMemory", StringComparison.OrdinalIgnoreCase)) return;
        await DevFixtures.SeedAsync(sp, time.GetUtcNow(), cancellationToken);
        logger.LogInformation("Seeded {Count} dev fixture users", DevFixtures.Users.Length);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
