using FluentAssertions;
using SwingTrader.Agents.Backtesting;
using Xunit;

namespace SwingTrader.Tests;

public class BacktestApplyExtractorTests
{
    private const string Weights =
        "{\"rsi\":0.15,\"macd\":0.10,\"volume\":0.20,\"sentiment\":0.15,\"setupQuality\":0.10," +
        "\"relativeStrength\":0.10,\"priceLevel\":0.05,\"fundamentalMomentum\":0.15}";

    [Fact]
    public void Extract_Ab_TakesConfigA_WeightsRulesAndStats()
    {
        // ResultJson is camelCase; RequestJson is PascalCase (the two casings
        // the app actually stores) - the extractor must handle both.
        var resultJson =
            "{\"mode\":\"ab\",\"candidates\":[" +
            "{\"label\":\"Your dials\",\"weights\":" + Weights + ",\"buyThreshold\":6.0," +
            "\"result\":{\"trades\":120,\"winRate\":55,\"totalReturnPct\":14,\"maxDrawdownPct\":8,\"profitFactor\":1.3,\"expectancyPct\":0.5}}," +
            "{\"label\":\"Production baseline\",\"weights\":" + Weights + ",\"buyThreshold\":6.5," +
            "\"result\":{\"trades\":100,\"winRate\":50,\"totalReturnPct\":9,\"maxDrawdownPct\":10,\"profitFactor\":1.1,\"expectancyPct\":0.2}}]}";
        var requestJson =
            "{\"Mode\":\"ab\",\"Candidates\":[" +
            "{\"Label\":\"Your dials\",\"Weights\":" + Weights + ",\"BuyThreshold\":6.0," +
            "\"Rules\":{\"MaxHoldDays\":15,\"StopLossPct\":0.05,\"MinHoldDays\":3}}," +
            "{\"Label\":\"Production baseline\",\"Weights\":" + Weights + ",\"BuyThreshold\":6.5}]}";

        var config = BacktestApplyExtractor.Extract("ab", requestJson, resultJson);

        config.Should().NotBeNull();
        config!.Label.Should().Be("Your dials");
        config.Weights.Rsi.Should().Be(0.15m);
        config.BuyThreshold.Should().Be(6.0m);
        config.Stats.Trades.Should().Be(120);
        config.Stats.WinRatePct.Should().Be(55m);
        config.Stats.TotalReturnPct.Should().Be(14m);
        // Risk overrides from the request - only the non-null ones.
        config.Rules.Should().NotBeNull();
        config.Rules!.MaxHoldDays.Should().Be(15);
        config.Rules.StopLossPct.Should().Be(0.05m);
        config.Rules.MinHoldDays.Should().Be(3);
        config.Rules.MaxOpenPositions.Should().BeNull();
    }

    [Fact]
    public void Extract_Ab_NoRules_LeavesRulesNull()
    {
        var resultJson =
            "{\"mode\":\"ab\",\"candidates\":[{\"label\":\"A\",\"weights\":" + Weights + ",\"buyThreshold\":6.0," +
            "\"result\":{\"trades\":10,\"winRate\":50,\"totalReturnPct\":1,\"maxDrawdownPct\":2,\"profitFactor\":1,\"expectancyPct\":0.1}}]}";
        var requestJson = "{\"Mode\":\"ab\",\"Candidates\":[{\"Label\":\"A\",\"Weights\":" + Weights + ",\"BuyThreshold\":6.0}]}";

        var config = BacktestApplyExtractor.Extract("ab", requestJson, resultJson);

        config.Should().NotBeNull();
        config!.Rules.Should().BeNull();
    }

    [Fact]
    public void Extract_Sweep_TakesWinner_WeightsAndStats_NoRules()
    {
        var resultJson =
            "{\"mode\":\"sweep\",\"winner\":{\"label\":\"cand-7\",\"weights\":" + Weights + ",\"buyThreshold\":6.0," +
            "\"trades\":140,\"winRate\":57,\"expectancyPct\":0.6,\"profitFactor\":1.4,\"maxDrawdownPct\":7,\"totalReturnPct\":18}}";
        var requestJson = "{\"Mode\":\"sweep\"}";

        var config = BacktestApplyExtractor.Extract("sweep", requestJson, resultJson);

        config.Should().NotBeNull();
        config!.Label.Should().Be("cand-7");
        config.Weights.FundamentalMomentum.Should().Be(0.15m);
        config.BuyThreshold.Should().Be(6.0m);
        config.Stats.Trades.Should().Be(140);
        config.Stats.ProfitFactor.Should().Be(1.4m);
        config.Rules.Should().BeNull(); // sweep never overrides risk rules
    }

    [Fact]
    public void Extract_NullOrMalformedResult_ReturnsNull()
    {
        BacktestApplyExtractor.Extract("ab", "{}", null).Should().BeNull();
        BacktestApplyExtractor.Extract("ab", "{}", "not json").Should().BeNull();
        BacktestApplyExtractor.Extract("ab", "{}", "{\"mode\":\"ab\",\"candidates\":[]}").Should().BeNull();
        BacktestApplyExtractor.Extract("single", "{}", "{\"mode\":\"single\"}").Should().BeNull();
    }
}
