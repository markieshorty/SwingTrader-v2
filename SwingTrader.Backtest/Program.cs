using SwingTrader.Backtest;

// Local backtesting tool - never deployed, never touches production.
//   dotnet run --project SwingTrader.Backtest -- download [--years 3] [--out backtest-data] [--key-file tingoKey.txt]
//   dotnet run --project SwingTrader.Backtest -- run      [--data backtest-data]
var command = args.FirstOrDefault()?.ToLowerInvariant();

string Arg(string name, string fallback)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

switch (command)
{
    case "download":
    {
        var keyFile = Arg("--key-file", "tingoKey.txt");
        if (!File.Exists(keyFile))
        {
            Console.Error.WriteLine($"Key file not found: {Path.GetFullPath(keyFile)} — put your Tiingo API key in it (gitignored, never committed).");
            return 2;
        }
        var apiKey = (await File.ReadAllTextAsync(keyFile)).Trim();
        var outDir = Arg("--out", "backtest-data");
        var years = int.Parse(Arg("--years", "3"));
        return await DataDownloader.RunAsync(apiKey, outDir, years, cts.Token);
    }

    case "run":
    {
        var dataDir = Arg("--data", "backtest-data");
        var excluded = Arg("--exclude", "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Enum.Parse<SwingTrader.Core.Enums.SetupType>)
            .ToHashSet();
        var bq = Arg("--breakout-quality", "");

        // --weights rsi,macd,volume,setup — the four ACTIVE components (the
        // other four are neutral 0.5 in the sim, so their weights only shift
        // scores by a constant). Values are auto-scaled so the active block
        // keeps its production share (0.59) and total weight stays 1.0,
        // keeping conviction comparable against the 6.0 Buy threshold.
        SwingTrader.Core.Models.StrategyWeights? weights = null;
        var w = Arg("--weights", "");
        if (w.Length > 0)
        {
            var parts = w.Split(',').Select(decimal.Parse).ToArray();
            if (parts.Length != 4) { Console.Error.WriteLine("--weights needs 4 values: rsi,macd,volume,setup"); return 2; }
            var scale = 0.59m / parts.Sum();
            weights = new SwingTrader.Core.Models.StrategyWeights
            {
                RsiWeight = parts[0] * scale,
                MacdWeight = parts[1] * scale,
                VolumeWeight = parts[2] * scale,
                SetupQualityWeight = parts[3] * scale,
                // neutral components keep production weights (sum 0.41)
            };
        }

        var opts = new BacktestEngine.Options(
            BuyThreshold: decimal.Parse(Arg("--threshold", "6.0")),
            RegimeFilter: args.Contains("--regime"),
            ExcludedSetups: excluded.Count > 0 ? excluded : null,
            BreakoutQualityOverride: bq.Length > 0 ? decimal.Parse(bq) : null,
            ConvictionSizing: args.Contains("--conviction-sizing"),
            Weights: weights,
            PositionFraction: decimal.Parse(Arg("--position-fraction", "0.10")),
            MaxOpenPositions: int.Parse(Arg("--max-open", "3")),
            Label: Arg("--label", "baseline"));
        return await BacktestEngine.RunAsync(dataDir, opts, cts.Token);
    }

    default:
        Console.WriteLine("Usage: SwingTrader.Backtest <download|run> [options]");
        return 2;
}
