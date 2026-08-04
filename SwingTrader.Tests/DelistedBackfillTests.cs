using System.IO.Compression;
using System.Text;
using FluentAssertions;
using SwingTrader.Agents.Backtesting;
using SwingTrader.Core.Models;
using Xunit;

namespace SwingTrader.Tests;

// Survivorship backfill (docs/survivorship-plan P1): candidate selection from
// Tiingo's supported-tickers CSV and the store-only-if-it-ever-screened rule.
public class DelistedBackfillTests
{
    private static readonly DateOnly Today = new(2026, 8, 4);
    private static readonly DateOnly WindowStart = Today.AddYears(-10);

    private static byte[] Zip(string csv)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("supported_tickers.csv").Open(), Encoding.UTF8);
            writer.Write(csv);
        }
        return ms.ToArray();
    }

    private const string Header = "ticker,exchange,assetType,priceCurrency,startDate,endDate";

    [Fact]
    public void ParseSupportedTickers_SelectsDelistedUsCommonStockInWindow()
    {
        var csv = string.Join('\n', Header,
            "DEADC,NYSE,Stock,USD,2015-01-02,2021-06-30",      // ✓ delisted mid-window
            "ALIVE,NASDAQ,Stock,USD,2010-01-04,2026-08-01",    // ✗ still listed (endDate ~today)
            "OLDCO,NYSE,Stock,USD,1995-01-03,2012-05-01",      // ✗ delisted before the window
            "BRIEF,NASDAQ,Stock,USD,2020-01-02,2020-04-01",    // ✗ listed < 180 days
            "EUROX,LSE,Stock,USD,2015-01-02,2021-06-30",       // ✗ wrong exchange
            "FUNDX,NYSE,ETF,USD,2015-01-02,2021-06-30",        // ✗ not common stock
            "PESO,NYSE,Stock,MXN,2015-01-02,2021-06-30",       // ✗ wrong currency
            "WARR-WS,NYSE,Stock,USD,2015-01-02,2021-06-30");   // ✗ suffix ticker (warrant)

        var result = DelistedBackfillService.ParseSupportedTickers(Zip(csv), WindowStart, Today);

        result.Should().ContainSingle(r => r.Symbol == "DEADC");
        result.Single().DelistedAtCheck().Should().Be(new DateOnly(2021, 6, 30));
    }

    [Fact]
    public void EverPassesScreen_LiquidMidPricedSymbol_Passes()
    {
        var candles = Enumerable.Range(0, 40)
            .Select(i => Bar(i, close: 50m, volume: 1_000_000)) // $50m daily dollar volume
            .ToList();
        DelistedBackfillService.EverPassesScreen(candles).Should().BeTrue();
    }

    [Theory]
    [InlineData(5, 1_000_000)]      // penny-ish: below the $15 price floor
    [InlineData(800, 1_000_000)]    // above the $500 ceiling
    [InlineData(50, 100_000)]       // $5m dollar volume: below the $10m floor
    public void EverPassesScreen_NeverLiquidEnough_Fails(double close, long volume)
    {
        var candles = Enumerable.Range(0, 40)
            .Select(i => Bar(i, (decimal)close, volume))
            .ToList();
        DelistedBackfillService.EverPassesScreen(candles).Should().BeFalse();
    }

    [Fact]
    public void EverPassesScreen_TooLittleHistory_Fails()
    {
        var candles = Enumerable.Range(0, 15).Select(i => Bar(i, 50m, 1_000_000)).ToList();
        DelistedBackfillService.EverPassesScreen(candles).Should().BeFalse();
    }

    private static HistoricalCandle Bar(int day, decimal close, long volume) => new()
    {
        Symbol = "X",
        Date = new DateOnly(2020, 1, 1).AddDays(day),
        Open = close, High = close, Low = close, Close = close,
        Volume = volume,
    };
}

internal static class TickerListingExtensions
{
    public static DateOnly DelistedAtCheck(this DelistedBackfillService.TickerListing t) => t.EndDate;
}
