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
    public string? SuspendReason { get; set; }
    public bool IsOnboarded { get; set; } = false;
    public int OnboardingStep { get; set; } = 0;

    // Per-user, deliberately separate from IsOnboarded (which tracks the
    // account's API-key setup) - a Member joining an account whose keys are
    // already configured would otherwise skip the onboarding wizard
    // entirely (isOnboardingComplete only checks account-level key status),
    // meaning they'd never see the email-confirmation step either.
    public bool HasConfirmedEmail { get; set; } = false;

    // Owners (self-registered, no invite) are auto-approved. Members joining
    // via an invite link start unapproved and can't do anything on the app
    // (enforced in UserRegistrationMiddleware) until the Owner explicitly
    // approves them - a link leak/interception only gets someone as far as
    // a "pending approval" screen, not real account access.
    public bool IsApproved { get; set; } = true;

    // Friends-and-family gate, independent of IsApproved above and of role:
    // EVERY brand-new AppUser (Owner or Member) starts unapproved here and
    // is blocked from the whole app (enforced in UserRegistrationMiddleware)
    // until the app's superadmin explicitly approves them. This exists
    // because IsApproved alone isn't enough - an Owner could invite and
    // approve their own Members without the superadmin ever seeing them,
    // which is exactly the "friend of a friend" leak this closes. Existing
    // users are backfilled to true by the introducing migration so nobody
    // already using the app gets locked out.
    public bool AdminApproved { get; set; } = false;
}
