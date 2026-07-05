using SwingTrader.Core.Enums;

namespace SwingTrader.Core.Models;

// Represents one login identity (one B2C object ID). An AppUser belongs
// to exactly one Account at a time.
public class AppUser : UnscopedEntity
{
    public string UserId { get; set; } = string.Empty; // B2C object ID
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int? AccountId { get; set; } // null until they create or join an account
    public AccountRole Role { get; set; }
    public DateTime FirstLoginAt { get; set; }
    public DateTime LastLoginAt { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsSuspended { get; set; } = false;
    public DateTime? SuspendedAt { get; set; }
    public bool IsOnboarded { get; set; } = false;
    public int OnboardingStep { get; set; } = 0;
}
