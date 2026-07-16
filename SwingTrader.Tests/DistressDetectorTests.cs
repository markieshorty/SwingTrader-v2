using FluentAssertions;
using SwingTrader.Agents.Filings;
using Xunit;

namespace SwingTrader.Tests;

// FD3's rules-based doom detection: 8-K item codes and going-concern language.
// These rules gate a hard entry veto AND a position exit, so both the hits and
// the deliberate non-hits (routine items, hypothetical risk-factor language)
// are locked down.
public class DistressDetectorTests
{
    [Theory]
    [InlineData("3.01")]          // delisting notice
    [InlineData("1.03")]          // bankruptcy / receivership
    [InlineData("4.02")]          // non-reliance on prior financials
    [InlineData("3.01,9.01")]     // distress item alongside a routine exhibit item
    [InlineData(" 3.01 , 9.01 ")] // whitespace tolerated
    public void DetectFromItems_DistressCodes_AreFlagged(string items)
    {
        DistressDetector.DetectFromItems(items).Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("2.02")]        // results of operations - routine
    [InlineData("5.02")]        // officer changes - routine
    [InlineData("2.02,9.01")]   // earnings 8-K, the most common kind
    [InlineData("")]
    [InlineData(null)]
    public void DetectFromItems_RoutineOrEmptyItems_AreNotFlagged(string? items)
    {
        DistressDetector.DetectFromItems(items).Should().BeEmpty();
    }

    [Fact]
    public void DetectFromItems_MultipleDistressCodes_EachGetsItsOwnReason()
    {
        var reasons = DistressDetector.DetectFromItems("1.03,3.01");
        reasons.Should().HaveCount(2);
        reasons.Should().Contain(r => r.Contains("bankruptcy"));
        reasons.Should().Contain(r => r.Contains("delisting"));
    }

    [Theory]
    [InlineData("There is substantial doubt about the Company's ability to continue as a going concern.")]
    [InlineData("...raise SUBSTANTIAL DOUBT regarding our ability to continue as a GOING CONCERN for twelve months...")]
    [InlineData("as a going concern; these conditions raise substantial doubt about our future.")] // either order
    public void HasGoingConcernLanguage_StandardDisclosurePhrasings_Match(string mda)
    {
        DistressDetector.HasGoingConcernLanguage(mda).Should().BeTrue();
    }

    [Theory]
    [InlineData("Our audited financial statements were prepared on a going concern basis.")] // no doubt expressed
    [InlineData("We have substantial doubt about the timing of the product launch.")]        // doubt, but not going concern
    [InlineData("Routine MD&A discussion of revenue and liquidity.")]
    [InlineData("")]
    [InlineData(null)]
    public void HasGoingConcernLanguage_AbsentOrUnrelated_DoesNotMatch(string? mda)
    {
        DistressDetector.HasGoingConcernLanguage(mda).Should().BeFalse();
    }

    [Fact]
    public void HasGoingConcernLanguage_PhrasesTooFarApart_DoNotMatch()
    {
        // The proximity window (~300 chars) keeps a "going concern basis"
        // boilerplate sentence from pairing with an unrelated "substantial
        // doubt" thousands of characters away.
        var mda = "substantial doubt about our supplier pricing." + new string('x', 5000) +
                  "prepared on a going concern basis.";
        DistressDetector.HasGoingConcernLanguage(mda).Should().BeFalse();
    }
}
