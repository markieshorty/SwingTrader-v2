using FluentAssertions;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.Storage;
using Xunit;

namespace SwingTrader.Tests;

// Blob candle store codec (docs/blob-candles-plan): the pure serialization +
// merge half of BlobHistoricalCandleRepository, tested without Azure.
public class CandleBlobCodecTests
{
    private static HistoricalCandle Bar(string symbol, string date, decimal close = 10m) => new()
    {
        Symbol = symbol,
        Date = DateOnly.Parse(date),
        Open = close - 0.5m,
        High = close + 1m,
        Low = close - 1m,
        Close = close,
        Volume = 123456m,
    };

    [Fact]
    public void EncodeDecode_RoundTripsBarsExactly()
    {
        var bars = new List<HistoricalCandle>
        {
            Bar("ABC", "2020-03-02", 10.25m),
            Bar("ABC", "2020-03-03", 10.75m),
        };

        var decoded = CandleBlobCodec.Decode("ABC", new MemoryStream(CandleBlobCodec.Encode(bars)));

        decoded.Should().HaveCount(2);
        decoded[0].Symbol.Should().Be("ABC");
        decoded[0].Date.Should().Be(DateOnly.Parse("2020-03-02"));
        decoded[0].Open.Should().Be(9.75m);
        decoded[0].High.Should().Be(11.25m);
        decoded[0].Low.Should().Be(9.25m);
        decoded[0].Close.Should().Be(10.25m);
        decoded[0].Volume.Should().Be(123456m);
        decoded[1].Close.Should().Be(10.75m);
    }

    [Fact]
    public void Encode_SortsByDate()
    {
        var decoded = CandleBlobCodec.Decode("ABC", new MemoryStream(CandleBlobCodec.Encode(
            [Bar("ABC", "2021-01-05"), Bar("ABC", "2020-01-05")])));

        decoded.Select(b => b.Date).Should().BeInAscendingOrder();
    }

    [Fact]
    public void Merge_IncomingWinsOnDateCollision_AndStaysSortedDeduped()
    {
        var existing = new List<HistoricalCandle> { Bar("ABC", "2020-01-02", 10m), Bar("ABC", "2020-01-03", 11m) };
        var incoming = new List<HistoricalCandle> { Bar("ABC", "2020-01-03", 99m), Bar("ABC", "2020-01-06", 12m) };

        var merged = CandleBlobCodec.Merge(existing, incoming);

        merged.Should().HaveCount(3);
        merged.Select(b => b.Date).Should().BeInAscendingOrder();
        merged.Single(b => b.Date == DateOnly.Parse("2020-01-03")).Close.Should().Be(99m);
    }

    [Fact]
    public void Meta_SurvivesJsonRoundTrip_CaseInsensitive()
    {
        var meta = new CandleStoreMeta { DatasetVersion = 2 };
        meta.Symbols["AAPL"] = new CandleSymbolMeta { Min = DateOnly.Parse("2016-01-04"), Max = DateOnly.Parse("2026-08-01"), Count = 2650 };

        var json = System.Text.Json.JsonSerializer.Serialize(meta);
        var back = System.Text.Json.JsonSerializer.Deserialize<CandleStoreMeta>(json)!.Normalize();

        back.DatasetVersion.Should().Be(2);
        // Normalize() must restore the case-insensitive comparer that
        // System.Text.Json drops on deserialize.
        back.Symbols.Should().ContainKey("aapl");
        back.Symbols["AAPL"].Count.Should().Be(2650);
    }
}
