using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

public interface ITierEvaluationRepository
{
    Task<TierEvaluationRecord> AddAsync(TierEvaluationRecord record);
}
