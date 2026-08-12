using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SwingTrader.Agents.Scorecard;
using SwingTrader.Core.Interfaces;
using SwingTrader.Data;
using SwingTrader.Data.Repositories;
using SwingTrader.Infrastructure.Storage;

namespace SwingTrader.Backtest;

// Local driver for the shadow-replay backfill (docs/scoring-engine-plan P0).
//
// The replay needs BOTH the signal store (Azure SQL) and the candle store
// (Blob), which is why it cannot run as a plain SQL script. The deployed API
// exposes the same operation behind /api/admin/shadow-replay for anyone with an
// admin token; this path exists so the backfill can be driven and inspected
// without one.
//
//   SWINGTRADER_SQL_CONN   Azure SQL connection string
//   SWINGTRADER_BLOB_CONN  storage connection string (the Functions app's
//                          AzureWebJobsStorage value)
public static class ShadowReplayRunner
{
    public static Task<int> RunAsync(int accountId, DateOnly from, bool force, CancellationToken ct) =>
        RunCoreAsync(accountId, from, force, synthetic: false, to: null, symbolLimit: null, variant: null, ct);

    public static Task<int> RunSyntheticAsync(int accountId, DateOnly from, DateOnly? to, int? symbolLimit, CancellationToken ct) =>
        RunCoreAsync(accountId, from, force: false, synthetic: true, to, symbolLimit, variant: null, ct);

    public static Task<int> RunVariantsAsync(int accountId, string baselineVersion, CancellationToken ct) =>
        RunCoreAsync(accountId, default, false, false, null, null, baselineVersion, ct);

    private static async Task<int> RunCoreAsync(int accountId, DateOnly from, bool force, bool synthetic, DateOnly? to, int? symbolLimit, string? variant, CancellationToken ct)
    {
        var sql = Environment.GetEnvironmentVariable("SWINGTRADER_SQL_CONN");
        var blob = Environment.GetEnvironmentVariable("SWINGTRADER_BLOB_CONN");
        if (string.IsNullOrWhiteSpace(sql) || string.IsNullOrWhiteSpace(blob))
        {
            Console.Error.WriteLine("Set SWINGTRADER_SQL_CONN and SWINGTRADER_BLOB_CONN.");
            return 2;
        }

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Information));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HistoricStore:BlobConnection"] = blob,
            }).Build());

        services.AddDbContext<SwingTraderDbContext>(o => o.UseSqlServer(sql, s =>
            // The Basic tier (5 DTU) times out the default 30s on reads this
            // size; the same 300s ceiling the candle loads use.
            s.CommandTimeout(300)));

        services.AddScoped<ISignalRepository, SignalRepository>();
        services.AddScoped<IShadowOutcomeRepository, ShadowOutcomeRepository>();
        services.AddScoped<IHistoricalCandleRepository, BlobHistoricalCandleRepository>();
        services.AddScoped<ISetupTacticsRepository, SetupTacticsRepository>();
        services.AddScoped<IAccountRiskProfileRepository, AccountRiskProfileRepository>();
        services.AddScoped<IShadowReplayService, ShadowReplayService>();
        services.AddScoped<ISyntheticReplayService, SyntheticReplayService>();
        services.AddSingleton<SwingTrader.Infrastructure.Services.IIndicatorService,
            SwingTrader.Infrastructure.Services.IndicatorService>();

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        if (variant is not null)
        {
            var gen = scope.ServiceProvider.GetRequiredService<ISyntheticReplayService>();

            // A stop x target grid with the trail OFF, since the first sweep
            // showed the trail costs ~26% of expectancy and that "arm late" is
            // just "off" by another name. The target arm runs well past 50%
            // because 50% beat 25% and we have not yet found where it stops
            // improving - a grid that stops at the current setting can only
            // ever confirm it.
            var stops = new[] { 0.08m, 0.10m, 0.15m, 0.20m };
            var targets = new[] { 0.25m, 0.40m, 0.60m, 1.00m };
            var grid = new List<(string, SetupDials)>();
            foreach (var s in stops)
                foreach (var tg in targets)
                    grid.Add(($"s{s * 100:0}/t{tg * 100:0}",
                        new SetupDials(s, tg, 30, 9.99m, 9.99m)));

            // One trail-on point as the control for the whole grid: the dials
            // currently running in production.
            grid.Add(("LIVE (s15/t25/trail)", new SetupDials(0.15m, 0.25m, 30, 0.125m, 0.075m)));

            Console.WriteLine($"Sweeping {grid.Count} dial sets over {variant} (no rows written)...");
            var stats = await gen.SweepAsync(variant, grid, ct);

            Console.WriteLine();
            Console.WriteLine($"{"variant",-22} {"closed",7} {"open",6} {"win%",7} {"avgWin",8} {"avgLoss",8} {"exp%",7} {"control",8} {"setups",8}");
            foreach (var r in stats.OrderByDescending(r => r.ExpectancyPct))
            {
                Console.WriteLine($"{r.Label,-22} {r.Closed,7} {r.StillOpen,6} {r.WinPct,7} {r.AvgWin,8} {r.AvgLoss,8} {r.ExpectancyPct,7} {r.ControlExpectancyPct,8} {r.SetupExpectancyPct,8}");
            }
            return 0;
        }

        if (synthetic)
        {
            var gen = scope.ServiceProvider.GetRequiredService<ISyntheticReplayService>();
            Console.WriteLine($"Synthetic replay from {from:yyyy-MM-dd} (limit={symbolLimit?.ToString() ?? "none"})...");
            var syn = await gen.GenerateAsync(accountId, from, to, symbolLimit, ct);
            Console.WriteLine();
            Console.WriteLine(syn.Summary);
            return 0;
        }

        var replay = scope.ServiceProvider.GetRequiredService<IShadowReplayService>();

        Console.WriteLine($"Replaying account {accountId} from {from:yyyy-MM-dd} (force={force})...");
        var result = await replay.ReplayLiveSignalsAsync(accountId, from, force, ct);
        Console.WriteLine();
        Console.WriteLine(result.Summary);
        return 0;
    }
}
