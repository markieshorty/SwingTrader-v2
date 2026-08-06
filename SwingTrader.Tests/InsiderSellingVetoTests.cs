using FluentAssertions;
using SwingTrader.Agents.Research;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;
using Xunit;

namespace SwingTrader.Tests;

// Insider cluster-selling veto (6 Aug 2026): a Buy on a symbol whose
// fundamental snapshot shows multiple insiders selling demotes to Watch.
// The fundamental SCORE already penalised it (0.15 vs 0.50 neutral) but
// only diluted through the forward blend - this makes it a hard gate.
public class InsiderSellingVetoTests
{
    [Theory]
    [InlineData(InsiderActivity.ClusterSelling, true)]
    [InlineData(InsiderActivity.Neutral, false)]
    [InlineData(InsiderActivity.Buying, false)]
    [InlineData(InsiderActivity.StrongBuying, false)]
    [InlineData(null, false)]
    public void DemotesOnlyOnClusterSelling(InsiderActivity? activity, bool shouldDemote) =>
        ResearchPipeline.ShouldDemoteForInsiderSelling(activity).Should().Be(shouldDemote);
}
