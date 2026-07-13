namespace SwingTrader.Agents.Filings;

// The decay that turns a scored filing delta into the day's effective shadow
// score (docs/filing-delta-plan). Pure so research and any later scorecard
// math share one definition.
public static class FilingDeltaMath
{
    // effective = delta * 0.5 ^ (tradingDaysSince / halfLifeTradingDays).
    // Trading days approximated as calendarDays * 5/7 - a day or two of
    // imprecision is irrelevant against a 63-trading-day half-life, and it
    // saves the research pipeline a market-calendar dependency.
    public static decimal EffectiveScore(decimal delta, DateOnly filedAt, DateOnly asOf, int halfLifeTradingDays)
    {
        if (halfLifeTradingDays <= 0) return delta;
        var calendarDays = asOf.DayNumber - filedAt.DayNumber;
        if (calendarDays <= 0) return delta;

        var tradingDays = calendarDays * 5.0 / 7.0;
        var decayed = (double)delta * Math.Pow(0.5, tradingDays / halfLifeTradingDays);
        return Math.Round((decimal)decayed, 4);
    }
}
