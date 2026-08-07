using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

public interface IFilingEventRepository
{
    Task<bool> ExistsAsync(string accessionNumber, CancellationToken ct = default);
    Task AddAsync(FilingEvent evt, CancellationToken ct = default);
    Task<List<FilingEvent>> GetRecentAsync(int days, CancellationToken ct = default);

    // Events young enough to still be worth repricing, staleset first, capped
    // so one run can never blow out the Tiingo budget.
    Task<List<FilingEvent>> GetForPriceRefreshAsync(int windowDays, int max, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}
