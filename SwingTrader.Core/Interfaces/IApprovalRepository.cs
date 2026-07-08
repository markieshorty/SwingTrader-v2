using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

public interface IApprovalRepository
{
    Task<TradeApproval?> GetByDateAsync(int accountId, TradingMode tradingMode, DateOnly date);
    Task<TradeApproval?> GetByIdAsync(int accountId, int id);
    Task<List<TradeApproval>> ListRecentAsync(int accountId, TradingMode tradingMode, int count);
    Task<TradeApproval> AddAsync(TradeApproval approval);
    Task UpdateAsync(TradeApproval approval);
    Task<bool> AnyApprovedAsync(int accountId, TradingMode tradingMode);
}
