namespace SwingTrader.Core.Models;

public class AccountInvite : UnscopedEntity
{
    public int AccountId { get; set; }
    public string InvitedByUserId { get; set; } = string.Empty;
    public string InvitedEmail { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty; // opaque random token, sent in invite link
    public DateTime ExpiresAt { get; set; }
    public DateTime? AcceptedAt { get; set; }
    public string? AcceptedByUserId { get; set; }
}
