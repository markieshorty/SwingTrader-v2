namespace SwingTrader.Core.Models;

public abstract class BaseEntity
{
    public int Id { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Tenant/billing unit. Every pre-existing entity got this via the
    // AddMultiTenancy migration, defaulted to the 'system' Account.
    public int AccountId { get; set; }
}

// AppUser, Account, and AccountInvite intentionally do NOT use BaseEntity:
// they're looked up by UserId/Token/Id before an account context even
// exists (first login, invite acceptance), so forcing an AccountId on them
// doesn't make sense the way it does for scoped trading data.
public abstract class UnscopedEntity
{
    public int Id { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
