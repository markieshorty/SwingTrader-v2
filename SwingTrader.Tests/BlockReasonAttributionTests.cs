using FluentAssertions;
using SwingTrader.Core.Constants;
using Xunit;

namespace SwingTrader.Tests;

// 7 Aug 2026: the forward scorecard worked out WHY a Buy was blocked by
// grepping the signal's Reasoning for "Distress veto". Every demotion path
// added after that check was written - the insider cluster-selling veto, the
// conviction ceiling, the slot-aware skip - matched nothing and landed in the
// "Setup disabled" bucket, so four unrelated mechanisms shared one number and
// none could be judged separately.
public class BlockReasonAttributionTests
{
    [Fact]
    public void EveryReason_IsDistinct()
    {
        string[] all =
        [
            BlockReasons.DistressVeto, BlockReasons.ForwardVeto, BlockReasons.InsiderSelling,
            BlockReasons.ConvictionCeiling, BlockReasons.PortfolioFull,
        ];

        all.Should().OnlyHaveUniqueItems("pooled reasons are exactly what made the old bucket unjudgeable");
    }

    [Fact]
    public void PortfolioFull_IsNotASignalJudgement()
    {
        // A slot skip says nothing about the symbol - it was queued out, not
        // assessed and rejected. Counting it among the vetoes would measure
        // our own capacity and call it judgement.
        BlockReasons.SignalJudgements.Should().NotContain(BlockReasons.PortfolioFull);
    }

    [Fact]
    public void EveryVeto_CountsAsASignalJudgement()
    {
        BlockReasons.SignalJudgements.Should().Contain(
            [BlockReasons.DistressVeto, BlockReasons.ForwardVeto,
             BlockReasons.InsiderSelling, BlockReasons.ConvictionCeiling]);
    }
}
