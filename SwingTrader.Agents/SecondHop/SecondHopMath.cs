namespace SwingTrader.Agents.SecondHop;

// Combining transmitted events into one SecondHopScore (docs/second-hop-plan).
// Pure so research and the eventual scorecard share one definition.
public static class SecondHopMath
{
    public sealed record TransmittedEvent(decimal Score, DateOnly EventDate);

    // Sum of per-event contributions, each decayed by age (trading days
    // approximated as calendarDays * 5/7, same convention as the filing
    // delta), clamped to [-1, +1]. Events reinforce each other - three
    // bullish supplier reads beat one - but the clamp stops a pile-on from
    // claiming impossible certainty.
    public static decimal Combine(IReadOnlyList<TransmittedEvent> events, DateOnly asOf, int halfLifeTradingDays)
    {
        if (events.Count == 0) return 0m;

        var total = 0.0;
        foreach (var e in events)
        {
            var calendarDays = Math.Max(0, asOf.DayNumber - e.EventDate.DayNumber);
            var tradingDays = calendarDays * 5.0 / 7.0;
            var decay = halfLifeTradingDays > 0 ? Math.Pow(0.5, tradingDays / halfLifeTradingDays) : 1.0;
            total += (double)e.Score * decay;
        }
        return Math.Round(Math.Clamp((decimal)total, -1m, 1m), 4);
    }
}
