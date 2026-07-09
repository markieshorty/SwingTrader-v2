using System.Net;
using FluentAssertions;
using SwingTrader.Infrastructure.Market;
using Xunit;

namespace SwingTrader.Tests;

public class WikipediaIndexClientTests
{
    private sealed class FakeHandler(string html) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html) });
    }

    private static WikipediaIndexClient CreateSut(string html) => new(new HttpClient(new FakeHandler(html)));

    private const string Sp500StyleHtml = """
        <html><body>
        <table class="wikitable sortable">
        <tr><th>Symbol</th><th>Security</th><th>GICS Sector</th></tr>
        <tr><td>AAPL</td><td>Apple Inc.</td><td>Technology</td></tr>
        <tr><td>MSFT</td><td>Microsoft Corporation</td><td>Technology</td></tr>
        </table>
        </body></html>
        """;

    private const string Nasdaq100StyleHtml = """
        <html><body>
        <table class="wikitable">
        <tr><th>Company</th><th>Sector weighting</th></tr>
        <tr><td>Information Technology</td><td>50%</td></tr>
        </table>
        <table class="wikitable sortable">
        <tr><th>Ticker</th><th>Company</th><th>ICB Industry</th></tr>
        <tr><td>AAPL</td><td>Apple Inc.</td><td>Technology</td></tr>
        <tr><td>NVDA</td><td>NVIDIA Corporation</td><td>Technology</td></tr>
        </table>
        </body></html>
        """;

    [Fact]
    public async Task GetSp500ConstituentsAsync_ParsesSymbolColumn()
    {
        var sut = CreateSut(Sp500StyleHtml);

        var result = await sut.GetSp500ConstituentsAsync();

        result.Select(r => r.Symbol).Should().BeEquivalentTo(["AAPL", "MSFT"]);
    }

    [Fact]
    public async Task GetNasdaq100ConstituentsAsync_SkipsNonMatchingTableAndFindsTickerColumn()
    {
        // The page has a sector-weighting table before the real constituents
        // table - the client must find the one with a Symbol/Ticker header,
        // not just take the first wikitable on the page.
        var sut = CreateSut(Nasdaq100StyleHtml);

        var result = await sut.GetNasdaq100ConstituentsAsync();

        result.Select(r => r.Symbol).Should().BeEquivalentTo(["AAPL", "NVDA"]);
    }

    [Fact]
    public async Task GetSp400ConstituentsAsync_ParsesSymbolColumn()
    {
        var sut = CreateSut(Sp500StyleHtml); // S&P 400 page uses the same Symbol-headed wikitable

        var result = await sut.GetSp400ConstituentsAsync();

        result.Select(r => r.Symbol).Should().BeEquivalentTo(["AAPL", "MSFT"]);
    }

    [Fact]
    public async Task GetSp600ConstituentsAsync_ParsesSymbolColumn()
    {
        var sut = CreateSut(Sp500StyleHtml); // S&P 600 page uses the same Symbol-headed wikitable

        var result = await sut.GetSp600ConstituentsAsync();

        result.Select(r => r.Symbol).Should().BeEquivalentTo(["AAPL", "MSFT"]);
    }

    [Fact]
    public async Task GetSp500ConstituentsAsync_NoMatchingTable_ReturnsEmpty()
    {
        const string html = """
            <html><body>
            <table class="wikitable"><tr><th>Foo</th><th>Bar</th></tr><tr><td>1</td><td>2</td></tr></table>
            </body></html>
            """;
        var sut = CreateSut(html);

        var result = await sut.GetSp500ConstituentsAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetSp500ConstituentsAsync_UppercasesAndTrimsSymbols()
    {
        const string html = """
            <html><body>
            <table class="wikitable">
            <tr><th>Symbol</th><th>Security</th></tr>
            <tr><td>  aapl  </td><td>Apple Inc.</td></tr>
            </table>
            </body></html>
            """;
        var sut = CreateSut(html);

        var result = await sut.GetSp500ConstituentsAsync();

        result.Should().ContainSingle().Which.Symbol.Should().Be("AAPL");
    }

    [Fact]
    public async Task GetSp500ConstituentsAsync_CapturesCompanyNameFromSecurityColumn()
    {
        var sut = CreateSut(Sp500StyleHtml);

        var result = await sut.GetSp500ConstituentsAsync();

        result.Should().ContainEquivalentOf(new UniverseSymbol("AAPL", "Apple Inc."));
        result.Should().ContainEquivalentOf(new UniverseSymbol("MSFT", "Microsoft Corporation"));
    }
}
