using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;

namespace SwingTrader.Api.Services;

// Resolves the caller's AppUser (by "sub" claim) to their AccountId/Role.
// UserRegistrationMiddleware populates HttpContext.Items once per request,
// right after it resolves (or creates) the caller's AppUser - this avoids
// every consumer of IAccountContext re-querying AppUser itself.
public class AccountContext(IHttpContextAccessor accessor) : IAccountContext
{
    public string UserId =>
        accessor.HttpContext?.User.FindFirst("sub")?.Value
        ?? throw new UnauthorizedAccessException("No authenticated user");

    public string Email =>
        accessor.HttpContext?.User.FindFirst("emails")?.Value ?? string.Empty;

    public int AccountId =>
        int.TryParse(accessor.HttpContext?.Items["AccountId"]?.ToString(), out var id)
            ? id
            : throw new UnauthorizedAccessException("User has no account");

    public AccountRole Role =>
        Enum.TryParse<AccountRole>(accessor.HttpContext?.Items["AccountRole"]?.ToString(), out var role)
            ? role
            : AccountRole.Member;

    public bool IsAuthenticated => accessor.HttpContext?.User.Identity?.IsAuthenticated ?? false;
}
