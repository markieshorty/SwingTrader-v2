namespace SwingTrader.Core.Models;

// The tenant/billing unit. All scoped trading data hangs off AccountId.
// Multiple AppUsers can belong to one Account (owner + invited members).
public class Account : UnscopedEntity
{
    public string Name { get; set; } = "My Account";
    public string? T212AccountId { get; set; }
    public bool GlobalRefinementOptIn { get; set; } = false;
}
