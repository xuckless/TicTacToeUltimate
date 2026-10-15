namespace TicTacToeUltimate.Server.Ratings;

/// <summary>One opponent result inside a rating period (rating scale, score 1/0.5/0).</summary>
public readonly record struct GlickoGame(double OpponentRating, double OpponentRd, double Score);

/// <summary>A Glicko-2 rating triple in the Glicko (1500-centred) scale.</summary>
public readonly record struct GlickoRating(double Rating, double Rd, double Volatility);

/// <summary>Pure Glicko-2 (Glickman 2012). One game = one rating period in production.</summary>
public static class Glicko2
{
    private const double Scale = 173.7178;
    /// <summary>Maximum RD.</summary>
    public const double MaxRd = 350;

    /// <summary>Updates a player for one rating period.</summary>
    public static GlickoRating Update(GlickoRating player, IReadOnlyList<GlickoGame> games, double tau = 0.5, double epsilon = 1e-6)
    {
        var mu = (player.Rating - 1500) / Scale;
        var phi = player.Rd / Scale;
        var sigma = player.Volatility;
        if (games.Count == 0)
            return player with { Rd = Math.Min(MaxRd, Math.Sqrt(phi * phi + sigma * sigma) * Scale) };

        double vInv = 0, sum = 0;
        foreach (var g in games)
        {
            var muJ = (g.OpponentRating - 1500) / Scale;
            var phiJ = g.OpponentRd / Scale;
            var gPhi = G(phiJ);
            var e = 1 / (1 + Math.Exp(-gPhi * (mu - muJ)));
            vInv += gPhi * gPhi * e * (1 - e);
            sum += gPhi * (g.Score - e);
        }
        var v = 1 / vInv;
        var delta = v * sum;

        // Volatility (Illinois algorithm, step 5).
        var a = Math.Log(sigma * sigma);
        double F(double x)
        {
            var ex = Math.Exp(x);
            var d = phi * phi + v + ex;
            return ex * (delta * delta - phi * phi - v - ex) / (2 * d * d) - (x - a) / (tau * tau);
        }
        double A = a, B;
        if (delta * delta > phi * phi + v) B = Math.Log(delta * delta - phi * phi - v);
        else
        {
            var k = 1;
            while (F(a - k * tau) < 0) k++;
            B = a - k * tau;
        }
        double fA = F(A), fB = F(B);
        var guard = 0;
        while (Math.Abs(B - A) > epsilon && guard++ < 1000)
        {
            var C = A + (A - B) * fA / (fB - fA);
            var fC = F(C);
            if (fC * fB <= 0) { A = B; fA = fB; }
            else fA /= 2;
            B = C; fB = fC;
        }
        var sigmaNew = Math.Exp(A / 2);

        var phiStar = Math.Sqrt(phi * phi + sigmaNew * sigmaNew);
        var phiNew = 1 / Math.Sqrt(1 / (phiStar * phiStar) + 1 / v);
        var muNew = mu + phiNew * phiNew * sum;
        return new GlickoRating(muNew * Scale + 1500, Math.Min(MaxRd, phiNew * Scale), sigmaNew);
    }

    /// <summary>Pre-period RD inflation for idle time: φ' = sqrt(φ² + σ²·periods), capped at 350.</summary>
    public static double InflateRd(double rd, double volatility, int idlePeriods)
    {
        if (idlePeriods <= 0) return Math.Min(MaxRd, rd);
        var phi = rd / Scale;
        return Math.Min(MaxRd, Math.Sqrt(phi * phi + volatility * volatility * idlePeriods) * Scale);
    }

    private static double G(double phi) => 1 / Math.Sqrt(1 + 3 * phi * phi / (Math.PI * Math.PI));
}
