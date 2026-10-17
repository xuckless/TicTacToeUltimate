using TicTacToeUltimate.Server.Diagnostics;

namespace TicTacToeUltimate.Server.Tests.Diagnostics;

public sealed class DiagnosticsTests
{
    private const string Stack1 = """
        System.InvalidOperationException: Sequence contains no elements
           at System.Linq.ThrowHelper.ThrowNoElementsException()
           at TicTacToeUltimate.Shared.Pages.ResultPage.<OnParametersSetAsync>d__42.MoveNext() in /src/TicTacToeUltimate.Shared/Pages/ResultPage.razor:line 210
           at TicTacToeUltimate.Shared.Components.Game.Board.Render() in /src/Board.razor:line 12
        """;
    private const string Stack2 = """
        System.InvalidOperationException: Sequence contains no elements
           at System.Linq.ThrowHelper.ThrowNoElementsException()
           at TicTacToeUltimate.Shared.Pages.ResultPage.<OnParametersSetAsync>d__42.MoveNext() in /src/TicTacToeUltimate.Shared/Pages/ResultPage.razor:line 377
           at TicTacToeUltimate.Shared.Components.Game.Board.Render() in /src/Board.razor:line 99
        """;

    [Fact]
    public void Same_crash_on_different_lines_shares_a_group()
    {
        Assert.Equal(DiagnosticsService.Fingerprint("System.InvalidOperationException", Stack1, "a"),
                     DiagnosticsService.Fingerprint("System.InvalidOperationException", Stack2, "b"));
        Assert.NotEqual(DiagnosticsService.Fingerprint("System.InvalidOperationException", Stack1, null),
                        DiagnosticsService.Fingerprint("System.NullReferenceException", Stack1, null));
    }

    [Fact]
    public void Suspected_files_point_at_our_code()
    {
        var files = DiagnosticsService.SuspectedFiles(Stack1);
        Assert.Equal("TicTacToeUltimate.Shared/Pages/ResultPage.razor", files[0]);
        Assert.Contains("TicTacToeUltimate.Shared/Components/Game/Board.razor", files);
    }

    [Fact]
    public void Issue_text_is_ready_for_an_agent()
    {
        var g = new CrashGroupRecord("abc", "crash", "InvalidOperationException: Sequence contains no elements", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 7, "open", null, ["Web"]);
        var r = new CrashReportRecord(1, "abc", "crash", null, null, "Web", "1.0", "macOS", null, "Chrome", "en", "390x844@3", "/result/x", "TicTacToeUltimate.Shared.Pages.ResultPage",
            "System.InvalidOperationException", "Sequence contains no elements", Stack1, "[]", """[{"atMs":1,"level":"Warning","category":"MatchStore","message":"late event"}]""",
            """[{"atMs":1,"kind":"nav","text":"/play"},{"atMs":2,"kind":"tap","text":"button:play-again"}]""", null, null, DateTimeOffset.UnixEpoch);
        var body = DiagnosticsService.IssueBody(g, [r], 5, auto: true);
        Assert.Contains("5 players", body, StringComparison.Ordinal);
        Assert.Contains("`TicTacToeUltimate.Shared.Pages.ResultPage`", body, StringComparison.Ordinal);
        Assert.Contains("button:play-again", body, StringComparison.Ordinal);
        Assert.Contains("regression test", body, StringComparison.Ordinal);
    }
}
