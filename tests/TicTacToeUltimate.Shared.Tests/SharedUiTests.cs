using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using TicTacToeUltimate.Client.Abstractions;
using TicTacToeUltimate.Client.State;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Shared.Components.Game;
using TicTacToeUltimate.Shared.Components.Home;
using TicTacToeUltimate.Shared.Fakes;
using TicTacToeUltimate.Shared.Layout;
using TicTacToeUltimate.Shared.Pages;
using TicTacToeUltimate.Shared.Services;

namespace TicTacToeUltimate.Shared.Tests;

public sealed class SharedUiTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public SharedUiTests()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddTttSharedUi();
        _ctx.Services.AddTttFakeStores();
    }

    public void Dispose() => _ctx.Dispose();

    private T Get<T>() where T : notnull => _ctx.Services.GetRequiredService<T>();

    // ---------- Board ----------

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void Board_renders_n_by_n_cells(int n)
    {
        var cut = _ctx.Render<Board>(p => p.Add(x => x.Size, n).Add(x => x.Cells, new string('.', n * n)));
        Assert.Equal(n * n, cut.FindAll(".t-cell").Count);
    }

    [Fact]
    public void Board_fades_next_to_vanish_and_labels_cells()
    {
        var cut = _ctx.Render<Board>(p => p.Add(x => x.Cells, "X.OOOX.XO").Add(x => x.Ghost, 2));
        var cells = cut.FindAll(".t-cell");
        Assert.Contains("fading", cells[2].ClassList);
        Assert.Single(cut.FindAll(".t-cell.fading"));
        Assert.Equal("Row 1, column 3, O, vanishes when the player to move places", cells[2].GetAttribute("aria-label"));
        Assert.Equal("Row 1, column 1, X", cells[0].GetAttribute("aria-label"));
        Assert.Equal("Row 1, column 2, empty", cells[1].GetAttribute("aria-label"));
    }

    [Fact]
    public void Board_draws_win_line()
    {
        var cut = _ctx.Render<Board>(p => p.Add(x => x.Cells, "X.OOX..OX").Add(x => x.WinLine, new[] { 0, 4, 8 }).Add(x => x.Winner, Color.X));
        Assert.Single(cut.FindAll("[data-testid=winline]"));
        Assert.Contains("winning line", cut.FindAll(".t-cell")[4].GetAttribute("aria-label"), StringComparison.Ordinal);
    }

    [Fact]
    public void Board_never_draws_win_line_through_ghost()
    {
        var cut = _ctx.Render<Board>(p => p.Add(x => x.Cells, "X.OOX..OX").Add(x => x.WinLine, new[] { 0, 4, 8 }).Add(x => x.Ghost, 4));
        Assert.Empty(cut.FindAll("[data-testid=winline]"));
        Assert.Empty(cut.FindAll(".t-cell.win"));
    }

    [Fact]
    public void Board_input_only_when_interactive_and_cell_empty()
    {
        var picked = -1;
        var cut = _ctx.Render<Board>(p => p.Add(x => x.Cells, "X.OOOX.XO").Add(x => x.Interactive, true)
            .Add(x => x.OnCellSelected, EventCallback.Factory.Create<int>(this, c => picked = c)));
        var cells = cut.FindAll(".t-cell");
        // aria-disabled, not disabled: occupied cells stay reachable by keyboard and screen readers (they just do nothing).
        Assert.Equal("true", cells[0].GetAttribute("aria-disabled"));
        Assert.False(cells[1].HasAttribute("aria-disabled"));
        cells[1].Click();
        Assert.Equal(1, picked);
    }

    [Fact]
    public void Board_marks_waiting_oldest_and_explains_blocked_clicks()
    {
        int picked = -1, blocked = -1;
        // X to move holding 3 (0 is its oldest, faded); O's oldest (2) gets the subtle marker. Legal excludes the vacated cell 0.
        var cut = _ctx.Render<Board>(p => p.Add(x => x.Cells, "XXO.O..XO").Add(x => x.Ghost, 0).Add(x => x.Aging, 2)
            .Add(x => x.Legal, new[] { 3, 5, 6 }).Add(x => x.Interactive, true)
            .Add(x => x.OnCellSelected, EventCallback.Factory.Create<int>(this, c => picked = c))
            .Add(x => x.OnBlockedSelected, EventCallback.Factory.Create<int>(this, c => blocked = c)));
        var cells = cut.FindAll(".t-cell");
        Assert.Contains("fading", cells[0].ClassList);
        Assert.Contains("aging", cells[2].ClassList);
        Assert.DoesNotContain("fading", cells[2].ClassList);
        Assert.Contains("vanishes on the other player's next move", cells[2].GetAttribute("aria-label"), StringComparison.Ordinal);
        Assert.False(cells[0].HasAttribute("disabled"));          // clickable so the page can explain why
        Assert.Equal("true", cells[0].GetAttribute("aria-disabled"));
        cells[0].Click();
        Assert.Equal(0, blocked);
        Assert.Equal(-1, picked);
        cut.FindAll(".t-cell")[3].Click();
        Assert.Equal(3, picked);
    }

    [Fact]
    public void ReplayFrames_cap_ghost_is_next_placers_oldest()
    {
        var rules = new RuleSetDto("n3k3-cap3-v0-w1-none", 3, 3, RemovalKind.PerPlayerCap, 3, false, true, OpeningRule.None);
        var cells = new[] { 0, 4, 1, 5, 8, 6 };
        var actions = cells.Select((c, i) => new ActionDto(i, i % 2, ActionKind.Place, c, i % 2 == 0 ? Color.X : Color.O, null, 1000, false)).ToList();
        var frames = ReplayFrames.Build(rules, actions);
        Assert.Null(frames[4].Ghost);      // X to move with 2 pieces
        Assert.Null(frames[5].Ghost);      // after X's 3rd piece: O to move with 2
        Assert.Equal(0, frames[6].Ghost);  // X to move holding 3: its oldest (0)
    }

    // ---------- Clock ----------

    [Theory]
    [InlineData(false, false, ClockUrgency.Idle, "idle")]
    [InlineData(true, false, ClockUrgency.Normal, "normal")]
    [InlineData(true, false, ClockUrgency.Warning, "warn")]
    [InlineData(true, false, ClockUrgency.Critical, "warn")]
    [InlineData(true, true, ClockUrgency.Critical, "sd")]
    public void PlayerClock_state_classes(bool active, bool sd, ClockUrgency urgency, string expected)
    {
        var cut = _ctx.Render<PlayerClock>(p => p.Add(x => x.RemainingMs, 3000).Add(x => x.Active, active)
            .Add(x => x.SuddenDeath, sd).Add(x => x.Urgency, urgency));
        var el = cut.Find(".t-clock");
        Assert.Contains(expected, el.ClassList);
        foreach (var other in new[] { "idle", "normal", "warn", "sd" }.Where(c => c != expected))
            Assert.DoesNotContain(other, el.ClassList);
        Assert.Equal("0:03", el.TextContent);
    }

    // ---------- Ads ----------

    [Fact]
    public void AdSlot_renders_in_shell_but_not_under_MatchLayout()
    {
        Assert.Contains("ad-slot", _ctx.Render<AdSlot>().Markup, StringComparison.Ordinal);

        RenderFragment body = b => { b.OpenComponent<AdSlot>(0); b.CloseComponent(); };
        var cut = _ctx.Render<MatchLayout>(p => p.Add(x => x.Body, body));
        Assert.DoesNotContain("ad-slot", cut.Markup, StringComparison.Ordinal);
        Assert.True(Get<FakeAdCoordinator>().MatchRouteActive);
    }

    [Fact]
    public async Task AdOverlay_completes_false_when_closed_early()
    {
        var svc = Get<AdOverlayService>();
        var host = _ctx.Render<AdOverlayHost>();
        var task = svc.ShowAsync(new AdOverlayRequest("rewarded", RewardKind.Analysis, 15), CancellationToken.None);
        host.WaitForAssertion(() => Assert.Contains("Rewarded ad", host.Markup, StringComparison.Ordinal));
        host.Find(".close").Click();
        Assert.False(await task);
    }

    // ---------- Result ----------

    [Fact]
    public void ResultPage_hides_rematch_when_rated()
    {
        Get<FakeMatchStore>().Put(MockupFixtures.Result(rated: true));
        var cut = _ctx.Render<ResultPage>(p => p.Add(x => x.Id, MockupFixtures.MatchId));
        Assert.Empty(cut.FindAll("[data-testid=rematch]"));
        Assert.Contains("No rematch in ranked games", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("VICTORY!", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("48 to Gold I", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("+12", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ResultPage_offers_rematch_when_unrated()
    {
        Get<FakeMatchStore>().Put(MockupFixtures.Result(rated: false));
        var cut = _ctx.Render<ResultPage>(p => p.Add(x => x.Id, MockupFixtures.MatchId));
        cut.Find("[data-testid=rematch]").Click();
        Assert.Contains("Rematch", Get<FakeMatchStore>().Calls);
    }

    // ---------- Match ----------

    [Fact]
    public void MatchPage_disables_input_when_not_your_turn()
    {
        var store = Get<FakeMatchStore>();
        store.Put(MockupFixtures.Match(myTurn: false));
        store.Clock = MockupFixtures.MatchClock(myTurn: false);
        var cut = _ctx.Render<MatchPage>(p => p.Add(x => x.Id, MockupFixtures.MatchId));
        Assert.All(cut.FindAll(".t-board .t-cell"), c => Assert.Equal("true", c.GetAttribute("aria-disabled")));
    }

    [Fact]
    public void MatchPage_your_turn_renders_mockup_and_places()
    {
        var store = Get<FakeMatchStore>();
        store.Put(MockupFixtures.Match());
        store.Clock = MockupFixtures.MatchClock();
        var cut = _ctx.Render<MatchPage>(p => p.Add(x => x.Id, MockupFixtures.MatchId));
        Assert.Contains("Game 0:41 · move 15s", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("kestrel_99", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Your move", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Your faded X vanishes when you place", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("play there this turn", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("fading", cut.FindAll(".t-board .t-cell")[0].ClassList);
        cut.FindAll(".t-board .t-cell")[1].Click();
        Assert.Contains("Place:1", store.Calls);
    }

    [Fact]
    public void MatchPage_sudden_death_escalates_red_alert()
    {
        var store = Get<FakeMatchStore>();
        store.Put(MockupFixtures.SuddenDeath());
        store.Clock = MockupFixtures.SuddenDeathClock;
        var cut = _ctx.Render<MatchPage>(p => p.Add(x => x.Id, MockupFixtures.MatchId));
        var root = cut.Find("[data-testid=match]");
        Assert.Contains("sd", root.ClassList);
        Assert.Contains("sd-1", root.ClassList);
        Assert.Contains("crit", root.ClassList);
        Assert.Contains("SUDDEN DEATH", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("window 8s → 7s in 5s", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("BLOCK IT!", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("YOUR MOVE · 8s WINDOW", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("threat", cut.FindAll(".t-board .t-cell")[2].ClassList);
        Assert.NotEmpty(cut.FindAll(".t-clock.sd"));
    }

    [Fact]
    public void MatchPage_impact_frame_on_sudden_death_start()
    {
        var store = Get<FakeMatchStore>();
        store.Put(MockupFixtures.Match());
        store.Clock = MockupFixtures.MatchClock();
        var cut = _ctx.Render<MatchPage>(p => p.Add(x => x.Id, MockupFixtures.MatchId));
        Assert.Empty(cut.FindAll(".impact"));
        store.Clock = MockupFixtures.SuddenDeathClock;
        store.Tick();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".impact[data-kind=suddendeath]")));
    }

    // ---------- Mockup screens ----------

    [Fact]
    public void HomePage_matches_mockup()
    {
        var cut = _ctx.Render<HomePage>();
        foreach (var s in new[] { "Play Ranked", "Gold II", "1540", "Win 3 games", "2/3", "kestrel_99", "3 your move", "Banner ad", "12" })
            Assert.Contains(s, cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void SocialPage_matches_mockup()
    {
        var cut = _ctx.Render<SocialPage>();
        foreach (var s in new[] { "rook_raider", "Challenges you · Casual x2", "kestrel_99", "In a game", "Offline · 2h ago", "Watch", "Daily", "Invite a friend by link" })
            Assert.Contains(s, cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void SocialPage_daily_challenge_to_offline_friend_renders_waiting_card()
    {
        var cut = _ctx.Render<SocialPage>();
        var daily = cut.FindAll("button").First(b => b.TextContent.Trim() == "Daily");
        daily.Click();
        cut.WaitForAssertion(() => Assert.Contains("Waiting for tic_tac_tess", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("Daily", cut.Markup, StringComparison.Ordinal);
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Cancel").Click();
    }

    [Fact]
    public void ProfilePage_matches_mockup()
    {
        var cut = _ctx.Render<ProfilePage>();
        foreach (var s in new[] { "ali_xo", "1552", "58%", "412 games played", "1610", "31", "Current 12 days", "48 to Gold I", "vs kestrel_99" })
            Assert.Contains(s, cut.Markup, StringComparison.Ordinal);
    }

    // ---------- Replay ----------

    [Fact]
    public void ReplayFrames_rebuild_board_and_ghost()
    {
        // Frames come from the real engine now, so the sequence must be a legal game: X0 O4 X1 O3 X7 O2 X8 O6 (O wins 2-4-6 after X0 vanishes).
        var rules = new RuleSetDto("n3k3-fifo7-v1-w0-none", 3, 3, RemovalKind.GlobalFifo, 7, true, false, OpeningRule.None);
        var cells = new[] { 0, 4, 1, 3, 7, 2, 8, 6 };
        var actions = cells.Select((c, i) => new ActionDto(i, i % 2, ActionKind.Place, c, i % 2 == 0 ? Color.X : Color.O, i == 7 ? 0 : null, 1000, false)).ToList();
        var frames = ReplayFrames.Build(rules, actions);
        Assert.Equal(9, frames.Count);
        Assert.Equal("XXOOO..XX", frames[7].Board);
        Assert.Equal(0, frames[7].Ghost);   // window full: X's first piece fades next
        var engine = TicTacToeUltimate.Engine.Rules.Replay(rules.ToEngine(), cells.Select(TicTacToeUltimate.Engine.GameAction.Place));
        Assert.Equal(TicTacToeUltimate.Engine.Notation.Board(engine), frames[8].Board);
        Assert.Equal('.', frames[8].Board[0]);
        Assert.Equal(1000, frames[8].ThinkMs);
    }

    // ---------- Tokens ----------

    [Fact]
    public void TokensCss_contains_every_token_in_both_themes()
    {
        var root = RepoRoot();
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "design", "system", "tokens.json")));
        var css = File.ReadAllText(Path.Combine(root, "TicTacToeUltimate.Shared", "wwwroot", "css", "tokens.css"));
        var dark = Between(css, "/* theme:dark */", "/* theme:light */");
        var light = Between(css, "/* theme:light */", "@media");

        var themed = json.RootElement.GetProperty("color").GetProperty("tokens").EnumerateArray()
            .Concat(json.RootElement.GetProperty("shadow").GetProperty("tokens").EnumerateArray()).ToList();
        Assert.NotEmpty(themed);
        foreach (var t in themed)
        {
            var name = t.GetProperty("name").GetString();
            var v = t.GetProperty("value");
            Assert.Contains($"--{name}: {v.GetProperty("dark").GetString()};", dark, StringComparison.Ordinal);
            Assert.Contains($"--{name}: {v.GetProperty("light").GetString()};", light, StringComparison.Ordinal);
        }
        foreach (var group in new[] { "spacing", "radius" })
            foreach (var t in json.RootElement.GetProperty(group).GetProperty("tokens").EnumerateArray())
                Assert.Contains($"--{t.GetProperty("name").GetString()}: {t.GetProperty("value").GetString()};", css, StringComparison.Ordinal);
        foreach (var f in json.RootElement.GetProperty("type").GetProperty("families").EnumerateObject())
            Assert.Contains($"--font-{f.Name}: {f.Value.GetString()};", css, StringComparison.Ordinal);
    }

    private static string Between(string s, string a, string b)
    {
        var i = s.IndexOf(a, StringComparison.Ordinal);
        Assert.True(i >= 0, a);
        var j = s.IndexOf(b, i + a.Length, StringComparison.Ordinal);
        return j < 0 ? s[i..] : s[i..j];
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TicTacToeUltimate.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
