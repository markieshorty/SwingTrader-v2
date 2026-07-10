using FluentAssertions;
using SwingTrader.Agents.Research;
using Xunit;

namespace SwingTrader.Tests;

// The cross-source news merge feeding the sentiment prompt: URL and
// normalised-title dedup (Finnhub and Tiingo carry the same PR headlines),
// newest-first ordering, and the article cap.
public class NewsBlenderTests
{
    private static NewsArticle Art(string source, string title, string? url = null, int minutesAgo = 0) =>
        new(source, title, "summary", DateTime.UtcNow.AddMinutes(-minutesAgo), url);

    [Fact]
    public void Blend_SameStoryFromBothSources_OnePromptLine()
    {
        // Same PR headline syndicated with different casing/punctuation and
        // different aggregator URLs - must collapse to one line.
        var blended = NewsBlender.Blend(
        [
            Art("Finnhub", "Apple Files Lawsuit Against OpenAI!", "https://a.com/1", minutesAgo: 10),
            Art("Tiingo", "apple files lawsuit against openai", "https://b.com/2", minutesAgo: 5),
        ], maxArticles: 10);

        blended.Should().HaveCount(1);
        blended[0].Source.Should().Be("Tiingo"); // the fresher copy survives
    }

    [Fact]
    public void Blend_IdenticalUrl_Deduped_EvenWithDifferentTitles()
    {
        var blended = NewsBlender.Blend(
        [
            Art("Finnhub", "Title A", "https://same.com/story", minutesAgo: 10),
            Art("Tiingo", "Title B entirely different", "https://same.com/story", minutesAgo: 5),
        ], 10);

        blended.Should().HaveCount(1);
    }

    [Fact]
    public void Blend_DistinctStories_AllKept_NewestFirst_Capped()
    {
        var blended = NewsBlender.Blend(
        [
            Art("Finnhub", "Story one", "https://a.com/1", minutesAgo: 30),
            Art("Tiingo", "Story two", "https://b.com/2", minutesAgo: 20),
            Art("Tiingo", "Story three", "https://b.com/3", minutesAgo: 10),
        ], maxArticles: 2);

        blended.Should().HaveCount(2);
        blended[0].Title.Should().Be("Story three");
        blended[1].Title.Should().Be("Story two");
    }

    [Fact]
    public void Blend_BlankTitles_Dropped()
    {
        NewsBlender.Blend([Art("Tiingo", "  ")], 10).Should().BeEmpty();
    }

    [Theory]
    [InlineData("Apple beats Q3 estimates!", "apple beats q3 estimates")]
    [InlineData("APPLE — Beats  Q3 'Estimates'", "applebeatsq3estimates")]
    public void NormaliseTitle_StripsCasePunctuationSpacing(string input, string atLeastContains)
    {
        NewsBlender.NormaliseTitle(input).Should().Be(
            NewsBlender.NormaliseTitle(atLeastContains.Replace(" ", "")));
    }
}
