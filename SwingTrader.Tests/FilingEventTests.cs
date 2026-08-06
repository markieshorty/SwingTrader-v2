using FluentAssertions;
using SwingTrader.Agents.FilingEvents;
using SwingTrader.Infrastructure.Edgar;
using Xunit;

namespace SwingTrader.Tests;

// Small-cap filing events P1 (docs/filing-events-plan): the zero-token
// routing rules, the EDGAR display-name parse and the classification parse.
public class FilingEventTests
{
    [Theory]
    [InlineData(new[] { "4.02", "9.01" }, "NonReliance")]
    [InlineData(new[] { "Item 5.02" }, "OfficerChange")]      // prefixed form
    [InlineData(new[] { "2.02", "9.01" }, null)]              // earnings-only: dropped
    [InlineData(new[] { "7.01" }, null)]                      // Reg FD: dropped
    [InlineData(new[] { "1.03" }, "Bankruptcy")]
    [InlineData(new[] { "1.01" }, null)]                      // agreement codes NOT in the lean default
    [InlineData(new string[0], null)]
    public void RouteEventType_UsesTheLeanDefaultSet(string[] items, string? expected) =>
        FilingEventScanService.RouteEventType(items).Should().Be(expected);

    [Fact]
    public void RouteEventType_ConfiguredCodes_WidenTheSet() =>
        FilingEventScanService.RouteEventType(["1.01"], ["4.02", "1.01"])
            .Should().Be("MaterialAgreement");

    [Theory]
    [InlineData("Acme Corp  (ACME)  (CIK 0001234567)", "Acme Corp", "ACME")]
    [InlineData("Widgets Inc  (WDG, WDG-WS)  (CIK 0000012345)", "Widgets Inc", "WDG")]
    [InlineData("No Ticker Fund  (CIK 0000099999)", "No Ticker Fund", "")]
    [InlineData("", "", "")]
    public void ParseDisplayName_ExtractsCompanyAndPlainTicker(string display, string company, string ticker)
    {
        var (c, t) = EdgarClient.ParseDisplayName(display);
        c.Should().Be(company);
        t.Should().Be(ticker);
    }

    [Fact]
    public void ParseClassification_ClampsAndNormalises()
    {
        var raw = """
            {"direction":"bearish","severity":9,"summary":" CFO resigned abruptly. ","facts":"  "}
            """;
        var (direction, severity, summary, facts) = FilingEventScanService.ParseClassification(raw);
        direction.Should().Be("Bearish");
        severity.Should().Be(5);            // clamped from 9
        summary.Should().Be("CFO resigned abruptly.");
        facts.Should().BeNull();            // whitespace -> null
    }

    [Fact]
    public void ParseClassification_UnknownDirection_BecomesUnclear()
    {
        var (direction, _, _, _) = FilingEventScanService.ParseClassification(
            """{"direction":"sideways","severity":2,"summary":"x","facts":"y"}""");
        direction.Should().Be("Unclear");
    }
}
