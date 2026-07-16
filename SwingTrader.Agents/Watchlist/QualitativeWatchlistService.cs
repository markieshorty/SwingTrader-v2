using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.Configuration;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.HttpClients.Dtos;
using SwingTrader.Infrastructure.Market;
using SwingTrader.Infrastructure.RateLimiting;

namespace SwingTrader.Agents.Watchlist;

public interface IQualitativeWatchlistService
{
    // Weekly refresh of the account's qualitative AI watchlist. Returns the
    // number of picks applied (0 = disabled / selection failed - the
    // previous list stands, next week retries).
    Task<int> RefreshAsync(int accountId, IClaudeClient claude, CancellationToken ct = default);
}

// The qualitative sibling of the technical AI watchlist
// (docs/qualitative-watchlist-plan): Claude picks over the WHOLE universe on
// narrative grounds - hype/crowd momentum, structural growth, turnarounds,
// catalyst-rich, fallen angels - one bucket with per-pick archetype labels
// (categories would fragment the sample below evaluability). The list is
// created DISABLED so picks are reviewable before they cost research; the
// funnel gate remains the only thing that turns a pick into a trade.
public class QualitativeWatchlistService(
    IWatchlistRepository watchlists,
    IWatchlistHistoryRepository history,
    ITradeRepository trades,
    IAccountRepository accountRepo,
    IMarketUniverseService universe,
    ISentimentArchiveRepository sentimentArchive,
    IClaudeRateLimiter claudeRateLimiter,
    IOptions<WatchlistConfig> config,
    IOptions<ClaudeConfig> claudeConfig,
    ILogger<QualitativeWatchlistService> logger) : IQualitativeWatchlistService
{
    private const string ListName = "Claude Qualitative";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<int> RefreshAsync(int accountId, IClaudeClient claude, CancellationToken ct = default)
    {
        var cfg = config.Value;
        if (!cfg.QualitativeEnabled) return 0;

        // The pick count is an account-level setting (Watchlists page slider);
        // fall back to the config default only if the account can't be loaded.
        var accountForSize = await accountRepo.GetAsync(accountId, ct);
        var qualitativeSize = accountForSize?.QualitativeWatchlistSize ?? cfg.QualitativeSize;

        var universeNames = await universe.GetUniverseWithNamesAsync(ct);
        if (universeNames.Count == 0)
        {
            logger.LogWarning("Qualitative watchlist skipped for account {AccountId} — universe unavailable", accountId);
            return 0;
        }
        var universeSymbols = universeNames.Select(u => u.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Exclude symbols already covered by any OTHER enabled watchlist
        // (the technical AI list, manual lists) from the candidate pool
        // itself - not just as a prompt instruction, which Claude can and
        // does ignore. If it's not in the list Claude sees, it structurally
        // cannot be picked, so the target count is real new ideas rather
        // than overlaps that get thinned out later. The qualitative list's
        // OWN current picks are deliberately NOT excluded - keeping a good
        // idea from last week is continuity, not a duplicate.
        var ownList = (await watchlists.GetAllWatchlistsAsync(accountId, ct))
            .FirstOrDefault(w => w.Type == WatchlistType.AiQualitative);
        var elsewhereSymbols = (await watchlists.GetAllEnabledSymbolsAsync(accountId, ct))
            .Where(i => ownList is null || i.WatchlistId != ownList.Id)
            .Select(i => i.Symbol)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidatePool = universeNames.Where(u => !elsewhereSymbols.Contains(u.Symbol)).ToList();
        if (candidatePool.Count == 0)
        {
            logger.LogWarning("Qualitative watchlist skipped for account {AccountId} — every universe symbol is already tracked elsewhere", accountId);
            return 0;
        }

        // Recent-context grounding: the archive's strongest movers (watchlist
        // + bellwether coverage) counter the model's training cutoff -
        // "likely to be active" should come from this week's data, not memory.
        var movers = await sentimentArchive.GetTopMoversSinceAsync(
            DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-14)), 15, ct);

        var picks = await SelectAsync(claude, candidatePool, movers, qualitativeSize, ct);
        if (picks is null) return 0; // selection failed - previous list stands

        // Hallucinated tickers are a certainty, not a risk: silently drop
        // anything not in the universe (however firmly the prompt forbade
        // it) and anything already tracked elsewhere (belt-and-braces - the
        // exclusion above stops Claude SEEING those symbols, but a model
        // recalling one from memory rather than the supplied list would
        // still slip through without this second check).
        var (valid, dropped) = FilterValidPicks(picks, universeSymbols, elsewhereSymbols);
        if (dropped.Count > 0)
            logger.LogWarning("Qualitative selection: {Dropped} pick(s) dropped (not in universe, or already tracked on another list): {Symbols}",
                dropped.Count, string.Join(", ", dropped));
        if (valid.Count == 0) return 0;

        var namesBySymbol = universeNames
            .GroupBy(u => u.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        return await ApplyAsync(accountId, valid, namesBySymbol, ct);
    }

    private async Task<List<QualitativePick>?> SelectAsync(
        IClaudeClient claude, IReadOnlyList<UniverseSymbol> universeNames,
        List<SentimentDailyScore> movers, int target, CancellationToken ct)
    {
        var universeLines = string.Join("\n", universeNames.Select(u => $"{u.Symbol} | {u.CompanyName}"));
        var moverLines = movers.Count == 0
            ? "(no recent archive data)"
            : string.Join("\n", movers.Select(m => $"{m.Symbol}: news sentiment {m.Score:+0.00;-0.00} on {m.Date:yyyy-MM-dd}"));

        var systemPrompt =
            "You are an expert equity strategist picking stocks on QUALITATIVE grounds - narrative, business " +
            "quality, crowd attention, catalysts - for a swing-trading system whose technical gate makes the " +
            "actual entry decisions. Your picks only need to be worth WATCHING. Respond only with valid JSON.";

        var userPrompt =
            $"Today is {DateTime.UtcNow:yyyy-MM-dd}. Pick exactly {target} stocks worth watching for the coming " +
            "weeks, chosen on qualitative grounds the price data cannot express. Span a MIX of archetypes:\n" +
            "- HypeMomentum: current crowd favourites with active narrative flow\n" +
            "- StructuralGrowth: secular compounders with durable tailwinds\n" +
            "- Turnaround: credible recovery stories at an inflection\n" +
            "- CatalystRich: pending decisions/launches/rulings that will force a repricing\n" +
            "- FallenAngel: quality names sold off beyond what their story justifies\n\n" +
            $"Recent notable news-sentiment moves (from our own scored archive):\n{moverLines}\n\n" +
            "You MUST pick only from this universe (symbol | company):\n" +
            $"{universeLines}\n\n" +
            "Respond with this exact JSON:\n" +
            "{\n" +
            "  \"picks\": [\n" +
            "    { \"symbol\": \"TICKER\", \"archetype\": \"<one of the five labels>\", \"reason\": \"<one specific sentence - name the narrative/catalyst, no generic praise>\" }\n" +
            "  ]\n" +
            "}";

        try
        {
            await claudeRateLimiter.WaitAsync(ct);
            var response = await claude.SendMessageAsync(new ClaudeRequest(
                claudeConfig.Value.WatchlistModel ?? claudeConfig.Value.PremiumModel,
                claudeConfig.Value.MaxTokens, systemPrompt,
                [new ClaudeMessage("user", userPrompt)]));
            var raw = response.Content.FirstOrDefault(c => c.Type == "text")?.Text ?? string.Empty;
            return ParsePicks(raw);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Qualitative watchlist selection failed");
            return null;
        }
    }

    // Internal static so the drop rules are directly testable: a pick
    // survives only if it's a real universe symbol AND not already tracked
    // on another enabled watchlist (the pre-filtered candidate pool should
    // make the latter case rare - this is the belt-and-braces catch for a
    // model recalling a symbol from memory rather than the supplied list).
    internal static (List<QualitativePick> Valid, List<string> Dropped) FilterValidPicks(
        List<QualitativePick> picks, IReadOnlySet<string> universeSymbols, IReadOnlySet<string> elsewhereSymbols)
    {
        var valid = new List<QualitativePick>();
        var dropped = new List<string>();
        foreach (var p in picks)
        {
            if (universeSymbols.Contains(p.Symbol) && !elsewhereSymbols.Contains(p.Symbol))
                valid.Add(p);
            else
                dropped.Add(p.Symbol);
        }
        return (valid, dropped);
    }

    // Internal static so the parse/shape rules are directly testable.
    internal static List<QualitativePick> ParsePicks(string raw)
    {
        var parsed = JsonSerializer.Deserialize<PicksResponse>(ClaudeJson.Extract(raw), JsonOpts)
            ?? throw new JsonException("null qualitative-picks result");

        return (parsed.Picks ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p.Symbol) && !string.IsNullOrWhiteSpace(p.Reason))
            .Select(p => new QualitativePick(
                p.Symbol.Trim().ToUpperInvariant(),
                string.IsNullOrWhiteSpace(p.Archetype) ? "Unlabelled" : p.Archetype.Trim(),
                p.Reason.Trim()))
            .DistinctBy(p => p.Symbol)
            .ToList();
    }

    // Diff-apply onto the account's (find-or-create, created-disabled)
    // qualitative watchlist: open-position symbols are never removed, and
    // each add records "[Archetype] reason" in the history - the label that
    // lets a later scorecard read per-archetype outcomes.
    private async Task<int> ApplyAsync(
        int accountId, List<QualitativePick> picks,
        IReadOnlyDictionary<string, UniverseSymbol> namesBySymbol, CancellationToken ct)
    {
        var account = await accountRepo.GetAsync(accountId, ct)
            ?? throw new InvalidOperationException($"Account {accountId} not found.");

        var all = await watchlists.GetAllWatchlistsAsync(accountId, ct);
        var list = all.FirstOrDefault(w => w.Type == WatchlistType.AiQualitative)
            ?? await watchlists.CreateWatchlistAsync(accountId, ListName, WatchlistType.AiQualitative,
                "Weekly Claude picks on qualitative grounds — review the rationales, then enable.", ct);

        var current = await watchlists.GetSymbolsAsync(accountId, list.Id, ct);
        var currentSymbols = current.Select(i => i.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newSymbols = picks.Select(p => p.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var openTradeSymbols = (await trades.GetOpenTradesAsync(accountId, account.TradingMode))
            .Select(t => t.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var weekStarting = DateOnly.FromDateTime(DateTime.UtcNow);

        foreach (var symbol in currentSymbols.Except(newSymbols, StringComparer.OrdinalIgnoreCase))
        {
            if (openTradeSymbols.Contains(symbol)) continue; // never pull the list out from under a position
            await watchlists.RemoveSymbolAsync(accountId, list.Id, symbol, ct);
        }

        var applied = 0;
        foreach (var pick in picks)
        {
            if (currentSymbols.Contains(pick.Symbol)) { applied++; continue; }
            try
            {
                var info = namesBySymbol.GetValueOrDefault(pick.Symbol);
                var name = info?.CompanyName ?? pick.Symbol;
                await watchlists.AddSymbolAsync(accountId, list.Id, pick.Symbol, name, info?.Sector ?? "Unknown", ct);
                await history.AddAsync(new WatchlistHistory
                {
                    AccountId = accountId,
                    Symbol = pick.Symbol,
                    CompanyName = name,
                    Action = WatchlistAction.Added,
                    Reason = $"[{pick.Archetype}] {pick.Reason}",
                    WeekStarting = weekStarting,
                });
                applied++;
            }
            catch (Exception ex)
            {
                // Caps (when the list is enabled) or duplicates - skip and
                // keep applying the rest; the log carries the why.
                logger.LogWarning(ex, "Qualitative pick {Symbol} could not be added — skipped", pick.Symbol);
            }
        }

        logger.LogInformation("Qualitative watchlist refreshed for account {AccountId}: {Count} pick(s) applied", accountId, applied);
        return applied;
    }

    internal sealed record QualitativePick(string Symbol, string Archetype, string Reason);

    private sealed record PicksResponse([property: JsonPropertyName("picks")] List<PickEntry>? Picks);
    private sealed record PickEntry(
        [property: JsonPropertyName("symbol")] string Symbol,
        [property: JsonPropertyName("archetype")] string? Archetype,
        [property: JsonPropertyName("reason")] string Reason);
}
