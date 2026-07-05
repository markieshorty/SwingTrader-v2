using SwingTrader.Core.Enums;

namespace SwingTrader.Core.Interfaces;

public interface IAccountContext
{
    string UserId { get; }
    string Email { get; }
    int AccountId { get; }
    AccountRole Role { get; }
    bool IsAuthenticated { get; }
}
