using System.Collections.Immutable;
using TicTacToeUltimate.Client.State;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Engine.Clocks;

namespace TicTacToeUltimate.Shared.Fakes;

/// <summary>Data shown in /design/mockups (ids match the server dev fixtures, §6.7).</summary>
public static class MockupFixtures
{
    /// <summary>Fixture id 00000000-0000-0000-0000-0000000000NN.</summary>
    public static Guid Id(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:000000000000}");

    /// <summary>Match id used by the match/result fixtures.</summary>
    public static readonly Guid MatchId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    /// <summary>Gold II, 48 to Gold I.</summary>
    public static readonly TierDto GoldII = new(Tier.Gold, 2, 48, 0.52);
    /// <summary>Gold I.</summary>
    public static readonly TierDto GoldI = new(Tier.Gold, 1, 88, 0.12);

    /// <summary>ali_xo.</summary>
    public static readonly PlayerCardDto Ali = new(Id(1), "ali_xo", "a1", "CA", AccountKind.Account, GoldII, 1540, false);
    /// <summary>kestrel_99.</summary>
    public static readonly PlayerCardDto Kestrel = new(Id(2), "kestrel_99", "a2", "US", AccountKind.Account, GoldI, 1562, false);
    /// <summary>mira_o.</summary>
    public static readonly PlayerCardDto Mira = new(Id(3), "mira_o", "a3", "GB", AccountKind.Account, new TierDto(Tier.Diamond, 3, 69, 0.31), 2031, false);
    /// <summary>jules_x.</summary>
    public static readonly PlayerCardDto Jules = new(Id(4), "jules_x", "a4", "FR", AccountKind.Account, new TierDto(Tier.Silver, 1, 58, 0.42), 1342, false);
    /// <summary>tic_tac_tess.</summary>
    public static readonly PlayerCardDto Tess = new(Id(5), "tic_tac_tess", "a5", "AU", AccountKind.Account, new TierDto(Tier.Silver, 3, 20, 0.8), 1180, false);
    /// <summary>rook_raider (06).</summary>
    public static readonly PlayerCardDto Rook = new(Id(6), "rook_raider", "a6", "DE", AccountKind.Account, new TierDto(Tier.Gold, 3, 60, 0.4), 1440, false);

    /// <summary>Me (ali_xo).</summary>
    public static readonly MeDto Me = new(Ali.UserId, AccountKind.Account, "ali_xo", "ali_xo", false, "a1", "CA", "America/Toronto", 1,
        [new RatingDto(RatingPool.Ranked, 1540, 60, 412, false, GoldII, 38), new RatingDto(RatingPool.Daily, 1610, 80, 40, false, GoldI, 0)]);

    /// <summary>Signed-in session with 3 daily games waiting.</summary>
    public static SessionState SignedInSession => SessionState.Initial with { Status = SessionStatus.SignedIn, Me = Me, Connection = ConnectionPhase.Online, DailyYourTurn = 3 };

    /// <summary>Guest session.</summary>
    public static SessionState GuestSession => SessionState.Initial with
    {
        Status = SessionStatus.SignedIn,
        Me = new MeDto(Id(99), AccountKind.Guest, "Guest#4821", null, false, "a2", null, "UTC", 0, []),
        Connection = ConnectionPhase.Online,
    };

    /// <summary>Dev users.</summary>
    public static readonly IReadOnlyList<DevUserDto> DevUsers =
        [.. new[] { Ali, Kestrel, Mira, Jules, Tess }.Select(p => new DevUserDto(p.UserId, p.DisplayName, p.Kind, p.Tier, p.Rating))];

    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Friends from the Social mockup.</summary>
    public static readonly ImmutableArray<FriendDto> Friends =
    [
        new(Kestrel, new PresenceDto(Kestrel.UserId, PresenceStatus.Online, null), 0, null),
        new(Mira, new PresenceDto(Mira.UserId, PresenceStatus.InGame, Guid.Parse("22222222-2222-2222-2222-222222222222")), 0, null),
        new(Jules, new PresenceDto(Jules.UserId, PresenceStatus.Online, null), 0, null),
        new(Tess, new PresenceDto(Tess.UserId, PresenceStatus.Offline, null), 0, DateTimeOffset.UtcNow.AddMinutes(-125)),
    ];

    /// <summary>Open CasualX2 challenge from rook_raider.</summary>
    public static readonly ChallengeDto Challenge = new("K7Q2MX", "http://localhost:5028/c/K7Q2MX", Rook, Ali.UserId, TimeControl.CasualX2,
        new RuleSetDto("n3k3-fifo7-v1-w0-none", 3, 3, RemovalKind.GlobalFifo, 7, true, false, OpeningRule.None),
        ChallengeStatus.Open, Now.AddDays(1), null);

    /// <summary>Social state.</summary>
    public static SocialState Social => SocialState.Empty with
    {
        Friends = Friends,
        Requests = [new FriendRequestDto(Guid.Parse("33333333-3333-3333-3333-333333333333"), Rook, Ali, FriendRequestDirection.Incoming, Now)],
        IncomingChallenges = [Challenge],
        Loaded = true,
    };

    /// <summary>Home mockup progress: streak 12, quests 1/1, 2/3, 2/5.</summary>
    public static readonly ProgressDto Progress = new(new StreakDto(12, 31, 1, true, false),
        [new QuestDto("win1", "Win 1 game", 1, 1, true, false), new QuestDto("win3", "Win 3 games", 2, 3, false, false), new QuestDto("win5", "Win 5 games", 2, 5, false, false)],
        new DateOnly(2026, 10, 15), Now.AddHours(6).ToUnixTimeMilliseconds(), false);

    /// <summary>Profile mockup recent games.</summary>
    public static readonly IReadOnlyList<GameSummaryDto> RecentGames =
    [
        new(Guid.NewGuid(), MatchMode.Ranked, TimeControl.Ranked, Kestrel, Color.X, GameResultForUser.Win, EndReason.Line, 12, 19, Now),
        new(Guid.NewGuid(), MatchMode.Ranked, TimeControl.Ranked, new PlayerCardDto(Id(7), "pixel_pawn", "a7", null, AccountKind.Account, GoldII, 1510, false), Color.O, GameResultForUser.Win, EndReason.Line, 14, 23, Now.AddHours(-1)),
        new(Guid.NewGuid(), MatchMode.Ranked, TimeControl.Ranked, Mira, Color.X, GameResultForUser.Loss, EndReason.Timeout, -9, 31, Now.AddHours(-2)),
        new(Guid.NewGuid(), MatchMode.Casual, TimeControl.CasualX2, Jules, Color.O, GameResultForUser.Win, EndReason.Line, null, 15, Now.AddHours(-3)),
    ];

    /// <summary>Profile mockup.</summary>
    public static readonly ProfileDto Profile = new(Ali with { Rating = 1552 }, new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
        [new RatingDto(RatingPool.Ranked, 1552, 60, 412, false, GoldII, 38), new RatingDto(RatingPool.Daily, 1610, 80, 40, false, GoldI, 0)],
        new ProfileStatsDto(412, 239, 170, 3, 0.58, 12, 31, 4), [.. RecentGames], FriendStatus.Self);

    private static readonly RuleSetDto RankedRules = new("n3k3-fifo7-v1-w0-pie", 3, 3, RemovalKind.GlobalFifo, 7, true, false, OpeningRule.PieSwap);

    private static SeatDto[] Seats(bool sd) =>
    [
        new(0, Ali, Color.X, true, 31),
        new(1, Kestrel, Color.O, true, sd ? null : 48),
    ];

    private static int[] Empty(string board) => [.. board.Select((c, i) => (c, i)).Where(t => t.c == '.').Select(t => t.i)];

    /// <summary>Builds a match view (Engine is not needed for rendering; it is null in fixtures).</summary>
    public static MatchView View(MatchSnapshotDto snap, bool myTurn, ImmutableArray<int> threats = default, MatchResultDto? result = null, PostGameSummaryDto? summary = null)
        => new(snap.MatchId, snap, null!, null, snap.YourSeat, snap.YourSeat is null, myTurn, threats.IsDefault ? [] : threats, [], result, summary,
            false, false, null, false, null);

    /// <summary>Match mockup: Ranked, game 0:41, your move (X), faded O at cell 2.</summary>
    public static MatchView Match(int? yourSeat = 0, bool myTurn = true)
    {
        const string board = "X.OOOX.XO";
        var clock = new ClockDto(TimeControl.Ranked, 0, 15000, 15000, 41000, false, 10000, 60000, [4000, 4000]);
        var snap = new MatchSnapshotDto(MatchId, 40, MatchMode.Ranked, TimeControl.Ranked, true, RankedRules, Seats(false), false, [], board,
            8, GamePhase.AwaitingPlacement, Color.X, myTurn ? 0 : 1, Empty(board), 2, clock, null, yourSeat, 0, false, 0);
        return View(snap, myTurn);
    }

    /// <summary>Match mockup clock.</summary>
    public static ClockView MatchClock(bool myTurn = true) => new(11000, 15000, 41000, false, 10000, 19000, ClockUrgency.Normal, myTurn ? 0 : 1);

    /// <summary>Sudden-death mockup: game 1:31, 8s window → 7s in 5s, 3s left, threat at cell 2, faded O at cell 0.</summary>
    public static MatchView SuddenDeath()
    {
        const string board = "OX.XOOO.X";
        var clock = new ClockDto(TimeControl.Ranked, 0, 8000, 8000, 91000, true, 7000, 96000, [3000, 3500]);
        var snap = new MatchSnapshotDto(MatchId, 80, MatchMode.Ranked, TimeControl.Ranked, true, RankedRules, Seats(true), false, [], board,
            14, GamePhase.AwaitingPlacement, Color.X, 0, Empty(board), 0, clock, null, 0, 0, false, 0);
        return View(snap, true, [2]);
    }

    /// <summary>Sudden-death clock.</summary>
    public static ClockView SuddenDeathClock => new(3000, 8000, 91000, true, 7000, 5000, ClockUrgency.Critical, 0);

    /// <summary>Result mockup: VICTORY, 1540 → 1552 (+12), 48 to Gold I, streak 3, 1 blunder.</summary>
    public static MatchView Result(bool rated = true)
    {
        const string board = "X.OOX..OX";
        var result = new MatchResultDto(Outcome.XWins, 0, EndReason.Line, [0, 4, 8], rated, null);
        var snap = new MatchSnapshotDto(MatchId, 120, rated ? MatchMode.Ranked : MatchMode.Casual, rated ? TimeControl.Ranked : TimeControl.CasualX2, rated,
            RankedRules, Seats(false), false, [], board, 19, GamePhase.Ended, Color.O, null, [], null,
            new ClockDto(TimeControl.Ranked, null, null, null, 104000, true, null, null, [4000, 4000]), result, 0, 0, !rated, 0);
        var summary = new PostGameSummaryDto(MatchId, result, GameResultForUser.Win,
            rated ? new RatingChangeDto(RatingPool.Ranked, 1540, 1552, 12, false) : null,
            rated ? new TierChangeDto(GoldII, GoldII, false, false) : null, 3,
            Progress with { Quests = [.. Progress.Quests.Select(q => q.QuestId == "win3" ? q with { Progress = 3, Completed = true } : q)] },
            true, 1, !rated);
        return View(snap, false, default, result, summary);
    }
}
