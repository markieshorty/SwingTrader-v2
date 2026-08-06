using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

public interface IFilingEventRepository
{
    Task<bool> ExistsAsync(string accessionNumber, CancellationToken ct = default);
    Task AddAsync(FilingEvent evt, CancellationToken ct = default);
    Task<List<FilingEvent>> GetRecentAsync(int days, CancellationToken ct = default);
}
