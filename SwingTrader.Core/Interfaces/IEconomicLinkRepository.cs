using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

public interface IEconomicLinkRepository
{
    // All links for a symbol, suppressed included (the UI shows both;
    // consumers filter on Suppressed themselves).
    Task<List<EconomicLink>> GetLinksAsync(string symbol, CancellationToken ct = default);

    // Symbols whose graph is missing or older than maxAge - the weekly
    // refresh's work list.
    Task<List<string>> GetStaleSymbolsAsync(IReadOnlyCollection<string> symbols, TimeSpan maxAge, CancellationToken ct = default);

    // Replaces a symbol's link set wholesale (a rebuild is a new graph, not a
    // merge) - but preserves Suppressed flags for links that survive the
    // rebuild (matched by LinkedName), so a human veto outlives a refresh.
    Task ReplaceLinksAsync(string symbol, List<EconomicLink> links, CancellationToken ct = default);

    Task<bool> SetSuppressedAsync(long linkId, bool suppressed, CancellationToken ct = default);
}
