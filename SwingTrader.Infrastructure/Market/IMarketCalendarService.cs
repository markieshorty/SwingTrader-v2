namespace SwingTrader.Infrastructure.Market;

public interface IMarketCalendarService
{
    bool IsMarketDay(DateOnly date);
}
