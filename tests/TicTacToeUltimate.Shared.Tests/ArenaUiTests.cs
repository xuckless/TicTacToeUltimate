using Bunit;
using Microsoft.Extensions.DependencyInjection;
using TicTacToeUltimate.Engine;
using TicTacToeUltimate.Shared.Components.Arena;
using TicTacToeUltimate.Shared.Fakes;

namespace TicTacToeUltimate.Shared.Tests;

public sealed class ArenaUiTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public ArenaUiTests()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddTttSharedUi();
        _ctx.Services.AddTttFakeStores();
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void HealthBar_has_25_hp_ticks_and_meter()
    {
        var cut = _ctx.Render<HealthBar>(p => p.Add(x => x.Hp, 75).Add(x => x.Max, 125).Add(x => x.Piece, Color.O).Add(x => x.Label, "Rook"));
        Assert.Equal(4, cut.FindAll(".arena-hp-tick").Count);               // 25, 50, 75, 100
        var meter = cut.Find("[role=meter]");
        Assert.Equal("75", meter.GetAttribute("aria-valuenow"));
        Assert.Contains("width:60%", cut.Find(".arena-hp-fill").GetAttribute("style"));
        Assert.Empty(cut.FindAll(".arena-hp-ghost"));
    }

    [Fact]
    public void HealthBar_previews_red_alert_hit()
    {
        var cut = _ctx.Render<HealthBar>(p => p.Add(x => x.Hp, 100).Add(x => x.Max, 125).Add(x => x.Preview, 50));
        Assert.Contains("width:40%", cut.Find(".arena-hp-ghost").GetAttribute("style"));
    }

    [Fact]
    public void ComboMeter_armed_and_broken()
    {
        var armed = _ctx.Render<ComboMeter>(p => p.Add(x => x.Streak, 2));
        Assert.Contains("armed", armed.Find(".arena-combo").ClassList);
        Assert.Equal(2, armed.FindAll(".pip.on").Count);
        var broken = _ctx.Render<ComboMeter>(p => p.Add(x => x.Streak, 0).Add(x => x.Broken, true));
        Assert.Contains("COMBO BROKEN", broken.Markup);
    }

    [Fact]
    public void Banner_shows_red_alert_and_overtime_first_mover()
    {
        var red = _ctx.Render<ArenaBanner>(p => p.Add(x => x.RemainingMs, 25_000).Add(x => x.RedAlert, true));
        Assert.Contains("×2 DMG", red.Markup);
        var ot = _ctx.Render<ArenaBanner>(p => p.Add(x => x.Overtime, true).Add(x => x.FirstMover, "You"));
        Assert.Contains("NEXT LINE WINS", ot.Markup);
        Assert.Contains("You move first", ot.Markup);
    }
}
