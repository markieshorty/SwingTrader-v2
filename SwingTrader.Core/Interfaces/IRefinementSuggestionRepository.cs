using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

public interface IRefinementSuggestionRepository
{
    Task<RefinementSuggestion> AddAsync(RefinementSuggestion suggestion);
    Task<RefinementSuggestion?> GetLatestAsync(int accountId, TradingMode tradingMode);
    Task<RefinementSuggestion?> GetByIdAsync(int accountId, int id);
    Task<IEnumerable<RefinementSuggestion>> GetHistoryAsync(int accountId, TradingMode tradingMode, int count = 12);
    Task UpdateAsync(RefinementSuggestion suggestion);
    Task SupersedeAllPendingAsync(int accountId, TradingMode tradingMode);
}
