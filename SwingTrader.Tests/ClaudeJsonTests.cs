using FluentAssertions;
using SwingTrader.Agents;
using SwingTrader.Agents.SecondHop;
using Xunit;

namespace SwingTrader.Tests;

public class ClaudeJsonTests
{
    [Theory]
    [InlineData("{\"a\":1}", "{\"a\":1}")]                                       // clean JSON untouched
    [InlineData("```json\n{\"a\":1}\n```", "{\"a\":1}")]                          // classic fenced block
    [InlineData("{\"a\":1}\n```", "{\"a\":1}")]                                   // trailing stray fence (14 Jul 2026 bellwether failure)
    [InlineData("```\n{\"a\":1}\n```\nDone.", "{\"a\":1}")]                       // text after the closing fence
    [InlineData("Here is the JSON:\n{\"a\":1}", "{\"a\":1}")]                     // preamble before bare JSON
    [InlineData("```json\n[{\"a\":1}]\n```", "[{\"a\":1}]")]                      // array payload
    [InlineData("[1,2,3] trailing", "[1,2,3]")]                                   // array with trailing text
    public void Extract_RecoversJsonFromDecoratedResponses(string raw, string expected) =>
        ClaudeJson.Extract(raw).Should().Be(expected);

    [Fact]
    public void Extract_NestedBracesSurvive() =>
        ClaudeJson.Extract("x {\"a\":{\"b\":[1,{\"c\":2}]}} y").Should().Be("{\"a\":{\"b\":[1,{\"c\":2}]}}");

    [Fact]
    public void Extract_NoJson_ReturnsTrimmedInput() =>
        ClaudeJson.Extract("  no json here  ").Should().Be("no json here");

    // The production failure end-to-end: bare JSON + stray trailing fence
    // must parse instead of throwing JsonException.
    [Fact]
    public void BellwetherParseScore_SurvivesTrailingFence() =>
        BellwetherSyncService.ParseScore("{\"sentiment_score\": 0.4}\n```")
            .Should().Be(0.4m);
}

// US-only instrument resolution (14 Jul 2026: a "HAL" buy matched HAL1a_EQ,
// the Amsterdam listing of HAL Trust, not Halliburton).
public class T212InstrumentResolverTests
{
    private static SwingTrader.Infrastructure.HttpClients.Dtos.InstrumentResponse Instr(string ticker, string name) =>
        new(ticker, name, "STOCK", "USD", "US0000000000");

    [Fact]
    public void PrefersUsListing_OverForeignListingListedFirst()
    {
        var instruments = new[]
        {
            Instr("HAL1a_EQ", "HAL"),      // Amsterdam HAL Trust - the incident match
            Instr("HAL_US_EQ", "Halliburton"),
        };

        SwingTrader.Agents.Execution.T212InstrumentResolver.ResolveUsTicker(instruments, "HAL")
            .Should().Be("HAL_US_EQ");
    }

    [Fact]
    public void NoUsListing_ReturnsNull_RatherThanForeign()
    {
        var instruments = new[] { Instr("HAL1a_EQ", "HAL") };

        SwingTrader.Agents.Execution.T212InstrumentResolver.ResolveUsTicker(instruments, "HAL")
            .Should().BeNull();
    }

    [Fact]
    public void DisambiguatedUsListing_Matches_ButDifferentSymbolDoesNot()
    {
        var instruments = new[] { Instr("HAL1a_US_EQ", "Halliburton A"), Instr("HALO_US_EQ", "Halozyme") };

        SwingTrader.Agents.Execution.T212InstrumentResolver.ResolveUsTicker(instruments, "HAL")
            .Should().Be("HAL1a_US_EQ");
    }

    [Fact]
    public void IsUsListing_ChecksSuffix()
    {
        SwingTrader.Agents.Execution.T212InstrumentResolver.IsUsListing("AAPL_US_EQ").Should().BeTrue();
        SwingTrader.Agents.Execution.T212InstrumentResolver.IsUsListing("HAL1a_EQ").Should().BeFalse();
    }
}
