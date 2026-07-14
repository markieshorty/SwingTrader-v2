using SwingTrader.Infrastructure.HttpClients.Dtos;

namespace SwingTrader.Agents.Execution;

// Resolves a research symbol to a T212 instrument - US LISTINGS ONLY.
//
// The previous resolution (name-equals OR ticker-prefix, first match wins)
// could land on a foreign listing of a different company sharing the symbol:
// on 14 Jul 2026 a buy for "HAL" (Halliburton - all research data is US:
// Finnhub news/fundamentals, Tiingo candles, US market-hours monitoring)
// matched "HAL1a_EQ", the Euronext Amsterdam listing of HAL Trust. Signals
// scored one company while the money sat in another, on an exchange that
// closes at 11:30 ET - before most of the monitor's exit window.
//
// T212 US equities use the "<SYMBOL>[disambiguator]_US_EQ" ticker shape.
// Anything without the _US_ segment is ineligible - the caller treats null
// as "no instrument" and skips the trade with a warning.
public static class T212InstrumentResolver
{
    public static string? ResolveUsTicker(IEnumerable<InstrumentResponse> instruments, string symbol)
    {
        var us = instruments
            .Where(i => i.Ticker.EndsWith("_US_EQ", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Exact base-token match first ("AAPL_US_EQ" for "AAPL"), then a
        // disambiguated listing ("HAL1a_US_EQ"-style: symbol + lowercase/digit
        // suffix, which never matches a genuinely different symbol like HALO).
        return (us.FirstOrDefault(i => BaseToken(i.Ticker).Equals(symbol, StringComparison.OrdinalIgnoreCase))
            ?? us.FirstOrDefault(i =>
                BaseToken(i.Ticker).StartsWith(symbol, StringComparison.OrdinalIgnoreCase)
                && BaseToken(i.Ticker).Length > symbol.Length
                && BaseToken(i.Ticker)[symbol.Length..].All(c => char.IsDigit(c) || char.IsLower(c))))
            ?.Ticker;
    }

    public static bool IsUsListing(string ticker) =>
        ticker.EndsWith("_US_EQ", StringComparison.OrdinalIgnoreCase);

    private static string BaseToken(string ticker) => ticker.Split('_')[0];
}
