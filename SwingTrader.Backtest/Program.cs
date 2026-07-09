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
        var opts = new BacktestEngine.Options(
            BuyThreshold: decimal.Parse(Arg("--threshold", "6.0")),
            RegimeFilter: args.Contains("--regime"),
            ExcludedSetups: excluded.Count > 0 ? excluded : null,
            Label: Arg("--label", "baseline"));
        return await BacktestEngine.RunAsync(dataDir, opts, cts.Token);
    }

    default:
        Console.WriteLine("Usage: SwingTrader.Backtest <download|run> [options]");
        return 2;
}
