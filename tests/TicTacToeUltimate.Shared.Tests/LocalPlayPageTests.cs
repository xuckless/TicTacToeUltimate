using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using TicTacToeUltimate.Client.Local;
using TicTacToeUltimate.Engine.Clocks;
using TicTacToeUltimate.Shared.Fakes;
using TicTacToeUltimate.Shared.Pages;

namespace TicTacToeUltimate.Shared.Tests;

public sealed class LocalPlayPageTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public LocalPlayPageTests()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddTttSharedUi();
        _ctx.Services.AddTttFakeStores();
    }

    public void Dispose() => _ctx.Dispose();

    private LocalPlaySession Session => _ctx.Services.GetRequiredService<LocalPlaySession>();

    [Fact]
    public void Setup_starts_a_session_with_chosen_clock()
    {
        var cut = _ctx.Render<LocalSetupPage>();
        cut.Find("[data-testid='tc-Casual']").Click();
        cut.Find("[data-testid='local-start']").Click();
        Assert.NotNull(Session.Current);
        Assert.Equal(TimeControl.Casual, Session.Current!.TimeControl);
        Assert.Equal("Player 1", Session.Current.NameOf(0));
        Assert.EndsWith("/local/play", _ctx.Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void Match_page_shows_turn_banner_and_places_pieces()
    {
        Session.Start(new LocalGameOptions { Player1 = "Ana", Player2 = "Ben" });
        var cut = _ctx.Render<LocalMatchPage>();
        Assert.Contains("X's turn (Ana)", cut.Find("[data-testid='local-banner']").TextContent, StringComparison.Ordinal);
        cut.FindAll(".t-cell")[4].Click();
        Assert.Contains("O's turn (Ben)", cut.Find("[data-testid='local-banner']").TextContent, StringComparison.Ordinal);
        Assert.Equal('X', Session.Current!.Board[4]);
    }

    [Fact]
    public void Match_page_offers_rematch_with_swapped_colours()
    {
        var g = Session.Start(new LocalGameOptions());
        var cut = _ctx.Render<LocalMatchPage>();
        foreach (var c in new[] { 0, 3, 1, 4, 2 }) cut.FindAll(".t-cell")[c].Click();
        // The overlay appears after the impact frame finishes, so the headline never shows twice.
        cut.WaitForElement("[data-testid='local-over']", TimeSpan.FromSeconds(3));
        Assert.Contains("Player 1 wins!", cut.Find("[data-testid='local-over']").TextContent, StringComparison.Ordinal);
        Assert.Contains("1 – 0", cut.Find("[data-testid='local-score']").TextContent, StringComparison.Ordinal);
        cut.Find("[data-testid='local-rematch']").Click();
        Assert.Equal(1, Session.Current!.ToMoveSeat);
        Assert.Contains("X's turn (Player 2)", cut.Find("[data-testid='local-banner']").TextContent, StringComparison.Ordinal);
    }
}
