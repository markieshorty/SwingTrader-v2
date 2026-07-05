using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

public interface ITierEvaluationRepository
{
    Task<TierEvaluationRecord> AddAsync(TierEvaluationRecord record);
    Task<TierEvaluationRecord?> GetLatestAsync(int accountId);
    Task<IEnumerable<TierEvaluationRecord>> GetHistoryAsync(int accountId, int count = 12);
}
