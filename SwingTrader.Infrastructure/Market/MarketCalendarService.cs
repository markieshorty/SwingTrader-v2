namespace SwingTrader.Infrastructure.Market;

public class MarketCalendarService : IMarketCalendarService
{
    private static readonly HashSet<DateOnly> Holidays = new(
    [
        // 2026
        new DateOnly(2026, 1, 1),   // New Year's Day
        new DateOnly(2026, 1, 19),  // MLK Day
        new DateOnly(2026, 2, 16),  // Presidents Day
        new DateOnly(2026, 4, 3),   // Good Friday
        new DateOnly(2026, 5, 25),  // Memorial Day
        new DateOnly(2026, 6, 19),  // Juneteenth
        new DateOnly(2026, 7, 3),   // Independence Day (observed)
        new DateOnly(2026, 9, 7),   // Labor Day
        new DateOnly(2026, 11, 26), // Thanksgiving
        new DateOnly(2026, 11, 27), // Day after Thanksgiving (early close — treated as closed)
        new DateOnly(2026, 12, 25), // Christmas

        // 2027
        new DateOnly(2027, 1, 1),   // New Year's Day
        new DateOnly(2027, 1, 18),  // MLK Day
        new DateOnly(2027, 2, 15),  // Presidents Day
        new DateOnly(2027, 3, 26),  // Good Friday
        new DateOnly(2027, 5, 31),  // Memorial Day
        new DateOnly(2027, 6, 18),  // Juneteenth (observed)
        new DateOnly(2027, 7, 5),   // Independence Day (observed)
        new DateOnly(2027, 9, 6),   // Labor Day
        new DateOnly(2027, 11, 25), // Thanksgiving
        new DateOnly(2027, 12, 24), // Christmas (observed)
    ]);

    public bool IsMarketDay(DateOnly date)
    {
        if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            return false;

        return !Holidays.Contains(date);
    }

    public int TradingDaysBetween(DateOnly from, DateOnly to)
    {
        if (to <= from) return 0;

        var count = 0;
        for (var d = from.AddDays(1); d <= to; d = d.AddDays(1))
            if (IsMarketDay(d)) count++;
        return count;
    }
}
