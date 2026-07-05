namespace SwingTrader.Agents.Readiness;

// Wilson score interval — a confidence interval for a binomial proportion (here, win rate)
// that stays well-behaved at the edges (p=0, p=1, small n) unlike the normal approximation.
public static class WilsonScoreInterval
{
    public static (decimal Low, decimal High) Calculate(int wins, int total, decimal confidence)
    {
        if (total == 0) return (0m, 1m);

        var z = confidence switch
        {
            0.90m => 1.645m,
            0.95m => 1.96m,
            0.99m => 2.576m,
            _ => 1.645m
        };

        var p = wins / (decimal)total;
        var n = (decimal)total;

        var centre = (p + (z * z) / (2 * n)) / (1 + (z * z) / n);
        var margin = z * (decimal)Math.Sqrt((double)(p * (1 - p) / n + (z * z) / (4 * n * n))) / (1 + (z * z) / n);

        var low = Math.Clamp(centre - margin, 0m, 1m);
        var high = Math.Clamp(centre + margin, 0m, 1m);
        return (low, high);
    }
}
