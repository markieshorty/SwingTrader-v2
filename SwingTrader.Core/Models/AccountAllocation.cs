using System.ComponentModel.DataAnnotations;

namespace SwingTrader.Core.Models;

// Capital sleeves (docs/sleeves-plan P1): how an account's capital splits
// between the passive core, the factor sleeve (P2 - inert until built) and
// the swing strategy. Fractions sum to 1. Default 0/0/1 = today's behaviour
// exactly; the pie only matters once the owner moves it.
public class AccountAllocation : BaseEntity
{
    public int AccountId { get; set; }
    public decimal SpyCorePct { get; set; }
    public decimal FactorTiltPct { get; set; }
    public decimal SwingPct { get; set; } = 1m;

    // The core sleeve's instrument. UK retail can't hold US-domiciled ETFs
    // (PRIIPs), so the default is Vanguard's UCITS S&P 500 ETF on the LSE.
    public string CoreTicker { get; set; } = "VUSA";

    public void Validate()
    {
        foreach (var (name, v) in new[] { ("SPY core", SpyCorePct), ("Factor tilt", FactorTiltPct), ("Swing", SwingPct) })
            if (v is < 0m or > 1m)
                throw new ValidationException($"{name} allocation must be between 0% and 100%.");
        if (Math.Abs(SpyCorePct + FactorTiltPct + SwingPct - 1m) > 0.001m)
            throw new ValidationException("Sleeve allocations must sum to exactly 100%.");
        if (FactorTiltPct > 0m)
            throw new ValidationException("The factor sleeve is not available yet (docs/sleeves-plan P2) — set it to 0%.");
        if (string.IsNullOrWhiteSpace(CoreTicker) || CoreTicker.Length > 12)
            throw new ValidationException("Core ticker must be 1-12 characters.");
    }
}
