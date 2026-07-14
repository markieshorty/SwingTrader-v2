using FluentAssertions;
using SwingTrader.Agents.Watchlist;
using SwingTrader.Infrastructure.Configuration;
using Xunit;

namespace SwingTrader.Tests;

// Qualitative AI watchlist (docs/qualitative-watchlist-plan): the parse
// rules for Claude's picks, and the premium-model routing decisions.
public class QualitativeWatchlistTests
{
    [Fact]
    public void ParsePicks_NormalizesDedupesAndDropsIncompletePicks()
    {
        var raw = "```json\n{\"picks\": [" +
                  "{\"symbol\": \"pltr\", \"archetype\": \"HypeMomentum\", \"reason\": \"AI narrative flow\"}," +
                  "{\"symbol\": \"PLTR\", \"archetype\": \"HypeMomentum\", \"reason\": \"duplicate\"}," +
                  "{\"symbol\": \"COST\", \"archetype\": null, \"reason\": \"Membership compounding\"}," +
                  "{\"symbol\": \"\", \"archetype\": \"Turnaround\", \"reason\": \"no symbol\"}," +
                  "{\"symbol\": \"NKE\", \"archetype\": \"FallenAngel\", \"reason\": \"\"}" +
                  "]}\n```";

        var picks = QualitativeWatchlistService.ParsePicks(raw);

        picks.Should().HaveCount(2); // duplicate, empty-symbol and empty-reason all dropped
        picks[0].Symbol.Should().Be("PLTR"); // uppercased
        picks[1].Archetype.Should().Be("Unlabelled"); // missing archetype defaults, never drops the pick
    }

    [Fact]
    public void FilterValidPicks_DropsHallucinatedAndCrossListOverlaps()
    {
        var universe = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "NVDA", "COST", "PLTR" };
        // NVDA is already on the technical AI list - the candidate pool
        // sent to Claude should have excluded it, but this pins the
        // belt-and-braces catch for when a model recalls it anyway.
        var elsewhere = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "NVDA" };
        var picks = new List<QualitativeWatchlistService.QualitativePick>
        {
            new("NVDA", "HypeMomentum", "already on the other list"),
            new("COST", "StructuralGrowth", "membership compounding"),
            new("ZZZZ", "Turnaround", "not a real universe symbol"),
        };

        var (valid, dropped) = QualitativeWatchlistService.FilterValidPicks(picks, universe, elsewhere);

        valid.Should().ContainSingle(p => p.Symbol == "COST");
        dropped.Should().BeEquivalentTo(["NVDA", "ZZZZ"]);
    }

    // Mark, 14 Jul 2026: high-value judgement calls run on Sonnet (Opus was
    // tried for a day and judged not worth its premium for qualitative text
    // work); high-volume structured extraction stays on the cheap default.
    // This test pins the config shape so a default regression is loud.
    [Fact]
    public void ClaudeConfig_PremiumModelIsSonnet_AndOverridesDefaultToNull()
    {
        var cfg = new ClaudeConfig();

        cfg.PremiumModel.Should().Be("claude-sonnet-5");
        cfg.Model.Should().StartWith("claude-haiku"); // the volume paths stay cheap
        cfg.WatchlistModel.Should().BeNull();  // null = PremiumModel at the call sites
        cfg.RefinementModel.Should().BeNull();
    }
}
