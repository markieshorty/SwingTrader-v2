using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Data.Repositories;

public class EconomicLinkRepository(SwingTraderDbContext db) : IEconomicLinkRepository
{
    public Task<List<EconomicLink>> GetLinksAsync(string symbol, CancellationToken ct = default) =>
        db.EconomicLinks
            .Where(l => l.Symbol == symbol.ToUpper())
            .OrderByDescending(l => l.Strength)
            .ToListAsync(ct);

    public async Task<List<string>> GetStaleSymbolsAsync(
        IReadOnlyCollection<string> symbols, TimeSpan maxAge, CancellationToken ct = default)
    {
        var upper = symbols.Select(s => s.ToUpperInvariant()).Distinct().ToList();
        var cutoff = DateTime.UtcNow - maxAge;

        var freshSymbols = await db.EconomicLinks
            .Where(l => upper.Contains(l.Symbol) && l.BuiltAt >= cutoff)
            .Select(l => l.Symbol)
            .Distinct()
            .ToListAsync(ct);

        return upper.Except(freshSymbols, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task ReplaceLinksAsync(string symbol, List<EconomicLink> links, CancellationToken ct = default)
    {
        var upper = symbol.ToUpperInvariant();
        var existing = await db.EconomicLinks.Where(l => l.Symbol == upper).ToListAsync(ct);

        // A human veto outlives a rebuild: carry Suppressed forward by
        // LinkedName so a hallucinated link Claude keeps producing stays dead.
        var suppressedNames = existing
            .Where(l => l.Suppressed)
            .Select(l => l.LinkedName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        db.EconomicLinks.RemoveRange(existing);
        foreach (var link in links)
        {
            link.Symbol = upper;
            link.LinkedTicker = link.LinkedTicker?.ToUpperInvariant();
            link.BuiltAt = DateTime.UtcNow;
            if (suppressedNames.Contains(link.LinkedName)) link.Suppressed = true;
        }
        db.EconomicLinks.AddRange(links);
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> SetSuppressedAsync(long linkId, bool suppressed, CancellationToken ct = default)
    {
        var link = await db.EconomicLinks.FirstOrDefaultAsync(l => l.Id == linkId, ct);
        if (link is null) return false;
        link.Suppressed = suppressed;
        await db.SaveChangesAsync(ct);
        return true;
    }
}
