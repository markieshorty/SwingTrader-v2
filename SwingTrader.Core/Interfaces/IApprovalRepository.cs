using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

public interface IApprovalRepository
{
    Task<TradeApproval?> GetByDateAsync(int accountId, DateOnly date);
    Task<TradeApproval?> GetByTokenAsync(string token);
    Task<TradeApproval> AddAsync(TradeApproval approval);
    Task UpdateAsync(TradeApproval approval);
    Task<bool> AnyApprovedAsync(int accountId);
}
