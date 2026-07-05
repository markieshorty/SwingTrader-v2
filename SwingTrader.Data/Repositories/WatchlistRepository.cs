using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Data.Repositories;

public class WatchlistRepository(SwingTraderDbContext db) : IWatchlistRepository
{
    // Small starter list for brand-new accounts, matching the 'system'
    // account's original seed data. The WatchlistAgent's dynamic universe
    // (Finnhub index constituents) replaces this once that's ported -
    // this is just enough for a new account to not start completely empty.
    private static readonly (string Symbol, string Company, string Sector)[] StarterSymbols =
    [
        ("AAPL", "Apple Inc.", "Technology"),
        ("MSFT", "Microsoft Corporation", "Technology"),
        ("NVDA", "NVIDIA Corporation", "Technology"),
        ("GOOGL", "Alphabet Inc.", "Technology"),
        ("AMZN", "Amazon.com Inc.", "Consumer"),
        ("JNJ", "Johnson & Johnson", "Healthcare"),
        ("JPM", "JPMorgan Chase & Co.", "Finance"),
        ("V", "Visa Inc.", "Finance"),
        ("WMT", "Walmart Inc.", "Consumer"),
        ("HD", "The Home Depot", "Consumer"),
    ];

    public async Task SeedDefaultAsync(int accountId, CancellationToken ct = default)
    {
        var alreadySeeded = await db.WatchlistItems.AnyAsync(w => w.AccountId == accountId, ct);
        if (alreadySeeded) return;

        var now = DateTime.UtcNow;
        var items = StarterSymbols.Select(s => new WatchlistItem
        {
            AccountId = accountId,
            Symbol = s.Symbol,
            CompanyName = s.Company,
            Sector = s.Sector,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });

        db.WatchlistItems.AddRange(items);
        await db.SaveChangesAsync(ct);
    }
}
