using Microsoft.AspNetCore.Authorization;

namespace SwingTrader.Api.Auth;

public class AdminRequirement : IAuthorizationRequirement;

public class AdminHandler(IConfiguration configuration) : AuthorizationHandler<AdminRequirement>
{
    private readonly string _adminUserId = configuration["Admin:UserId"] ?? string.Empty;

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, AdminRequirement requirement)
    {
        var userId = context.User.FindFirst("sub")?.Value;
        if (!string.IsNullOrEmpty(_adminUserId) && userId == _adminUserId)
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
