using Microsoft.Extensions.Options;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Server.Hosting;

namespace TicTacToeUltimate.Server.Ratings;

/// <summary>Pure tier/division math over the configured bands (D12).</summary>
public sealed class TierCalculator
{
    private readonly (Tier Tier, int Min)[] _bands;
    private readonly int _width;

    public TierCalculator(TiersOptions o)
    {
        _bands = [(Tier.Bronze, o.Bronze), (Tier.Silver, o.Silver), (Tier.Gold, o.Gold), (Tier.Platinum, o.Platinum), (Tier.Diamond, o.Diamond), (Tier.Master, o.Master)];
        _width = Math.Max(1, o.DivisionWidth);
    }

    public TierCalculator(IOptions<TttOptions> options) : this(options.Value.Tiers) { }

    /// <summary>Master threshold.</summary>
    public int MasterMin => _bands[^1].Min;

    /// <summary>Bands for /api/config.</summary>
    public TierBandDto[] Bands => _bands.Select(b => new TierBandDto(b.Tier, b.Min, _width)).ToArray();

    /// <summary>Tier and division for a rating.</summary>
    public TierDto For(double rating)
    {
        var r = (int)Math.Round(rating, MidpointRounding.AwayFromZero);
        if (r >= MasterMin) return new TierDto(Tier.Master, 0, 0, 0);
        var i = 0;
        for (var j = 0; j < _bands.Length - 1; j++) if (r >= _bands[j].Min) i = j;
        var top = _bands[i + 1].Min;
        var k = (top - 1 - r) / _width;
        var division = Math.Min(3, k + 1);
        var next = top - (division - 1) * _width;
        var lower = top - division * _width;
        var progress = Math.Clamp((r - lower) / (double)_width, 0, 1);
        return new TierDto(_bands[i].Tier, division, next - r, progress);
    }

    /// <summary>Ordinal for promotion/demotion comparison.</summary>
    public static int Rank(TierDto t) => t.Tier == Tier.Master ? 1000 : (int)t.Tier * 3 + (3 - t.Division);

    /// <summary>Change between two ratings, or null when nothing changed.</summary>
    public TierChangeDto Change(double before, double after)
    {
        var b = For(before);
        var a = For(after);
        return new TierChangeDto(b, a, Rank(a) > Rank(b), Rank(a) < Rank(b));
    }
}
