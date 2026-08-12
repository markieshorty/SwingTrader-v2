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
        RunCoreAsync(accountId, from, force, synthetic: false, to: null, symbolLimit: null, ct);

    public static Task<int> RunSyntheticAsync(int accountId, DateOnly from, DateOnly? to, int? symbolLimit, CancellationToken ct) =>
        RunCoreAsync(accountId, from, force: false, synthetic: true, to, symbolLimit, ct);

    private static async Task<int> RunCoreAsync(int accountId, DateOnly from, bool force, bool synthetic, DateOnly? to, int? symbolLimit, CancellationToken ct)
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
