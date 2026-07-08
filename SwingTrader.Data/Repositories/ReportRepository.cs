using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Data.Repositories;

public class ReportRepository(SwingTraderDbContext context) : IReportRepository
{
    public Task<DailyReport?> GetByIdAsync(int accountId, int id) =>
        context.DailyReports.FirstOrDefaultAsync(x => x.AccountId == accountId && x.Id == id);

    public Task<DailyReport?> GetByDateAsync(int accountId, TradingMode tradingMode, DateOnly date) =>
        context.DailyReports.FirstOrDefaultAsync(x => x.AccountId == accountId && x.TradingMode == tradingMode && x.ReportDate == date);

    public async Task<IEnumerable<DailyReport>> GetAllAsync(int accountId, TradingMode tradingMode) =>
        await context.DailyReports
            .Where(x => x.AccountId == accountId && x.TradingMode == tradingMode)
            .OrderByDescending(x => x.ReportDate)
            .ToListAsync();

    public async Task<IEnumerable<DailyReport>> GetUnsentReportsAsync(int accountId, TradingMode tradingMode) =>
        await context.DailyReports.Where(x => x.AccountId == accountId && x.TradingMode == tradingMode && !x.WasSent).ToListAsync();

    public async Task<DailyReport> AddAsync(DailyReport report)
    {
        context.DailyReports.Add(report);
        await context.SaveChangesAsync();
        return report;
    }

    public async Task UpdateAsync(DailyReport report)
    {
        report.UpdatedAt = DateTime.UtcNow;
        context.DailyReports.Update(report);
        await context.SaveChangesAsync();
    }

    public async Task DeleteAsync(int accountId, int id)
    {
        var report = await context.DailyReports.FirstOrDefaultAsync(x => x.AccountId == accountId && x.Id == id);
        if (report is not null)
        {
            context.DailyReports.Remove(report);
            await context.SaveChangesAsync();
        }
    }
}
