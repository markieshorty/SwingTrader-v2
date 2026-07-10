namespace SwingTrader.Infrastructure.Market;

public interface IMarketCalendarService
{
    bool IsMarketDay(DateOnly date);

    // Number of market days strictly after `from` up to and including `to`
    // (weekends and holidays excluded). Used for hold-period accounting so a
    // position opened Friday isn't "3 days old" by Monday. Zero if to <= from.
    int TradingDaysBetween(DateOnly from, DateOnly to);
}
