using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Data.Repositories;

public class StrategyWeightsRepository(SwingTraderDbContext db) : IStrategyWeightsRepository
{
    public async Task SeedDefaultAsync(int accountId, CancellationToken ct = default)
    {
        var alreadySeeded = await db.StrategyWeights.AnyAsync(w => w.AccountId == accountId, ct);
        if (alreadySeeded) return;

        var now = DateTime.UtcNow;
        db.StrategyWeights.Add(new StrategyWeights
        {
            AccountId = accountId,
            RsiWeight = 0.17m,
            MacdWeight = 0.09m,
            VolumeWeight = 0.21m,
            SentimentWeight = 0.16m,
            SetupQualityWeight = 0.12m,
            RelativeStrengthWeight = 0.10m,
            PriceLevelWeight = 0.05m,
            FundamentalMomentumWeight = 0.10m,
            BuyThreshold = 6.0m,
            WatchThreshold = 5.0m,
            StopLossPctDefault = 0.05m,
            IsActive = true,
            Source = "Default",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync(ct);
    }
}
