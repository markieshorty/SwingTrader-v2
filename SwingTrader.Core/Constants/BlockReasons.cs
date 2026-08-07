namespace SwingTrader.Core.Constants;

// Why a gate-passing Buy was demoted to Watch, recorded at the demotion.
//
// 7 Aug 2026: the forward scorecard used to work this out afterwards by
// grepping the signal's Reasoning text for "Distress veto". Every demotion
// path added since then - the insider cluster-selling veto, the conviction
// ceiling, the slot-aware skip - matched nothing and fell through to the
// "Setup disabled" bucket, so four unrelated mechanisms shared one number and
// none of them could be judged. Free-text inference also fails silently the
// moment anyone rewords a message, which is the worst property a measurement
// layer can have.
//
// Stored as strings rather than an enum so a new reason needs no migration.
public static class BlockReasons
{
    // Rules-based SEC distress quarantine (FD3). A fact, not a prediction.
    public const string DistressVeto = "DistressVeto";

    // Funnel F3: forward score below the book's floor.
    public const string ForwardVeto = "ForwardVeto";

    // Insider cluster selling detected on the symbol.
    public const string InsiderSelling = "InsiderSelling";

    // Conviction above the book's MaxConvictionForBuy ceiling.
    public const string ConvictionCeiling = "ConvictionCeiling";

    // No free slot. NOT a judgement about the stock - counting these among
    // the vetoes would make every veto's counterfactual meaningless, because
    // the signal was never assessed and rejected, merely queued out.
    public const string PortfolioFull = "PortfolioFull";

    // Reasons that represent an opinion ABOUT THE SYMBOL, and so belong in a
    // veto counterfactual. PortfolioFull is deliberately absent.
    public static readonly string[] SignalJudgements =
        [DistressVeto, ForwardVeto, InsiderSelling, ConvictionCeiling];
}
