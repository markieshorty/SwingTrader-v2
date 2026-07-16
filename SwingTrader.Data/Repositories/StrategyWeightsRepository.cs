using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Data.Repositories;

public class StrategyWeightsRepository(SwingTraderDbContext db) : IStrategyWeightsRepository
{
    public Task<StrategyWeights?> GetActiveWeightsAsync(int accountId) =>
        db.StrategyWeights.FirstOrDefaultAsync(w =>
            w.AccountId == accountId && w.IsActive && w.ApplicableRegime == null);

    public async Task<StrategyWeights?> GetActiveWeightsAsync(int accountId, MarketRegime? regime)
    {
        if (regime is not null)
        {
            var regimeSpecific = await db.StrategyWeights.FirstOrDefaultAsync(w =>
                w.AccountId == accountId && w.IsActive && w.ApplicableRegime == regime);
            if (regimeSpecific is not null)
                return regimeSpecific;
        }

        return await GetActiveWeightsAsync(accountId);
    }

    public async Task<StrategyWeights> AddAsync(StrategyWeights weights)
    {
        weights.Validate();
        weights.CreatedAt = weights.CreatedAt == default ? DateTime.UtcNow : weights.CreatedAt;
        db.StrategyWeights.Add(weights);
        await db.SaveChangesAsync();
        return weights;
    }

    public async Task SetActiveAsync(int accountId, int id)
    {
        var target = await db.StrategyWeights.FirstOrDefaultAsync(w => w.AccountId == accountId && w.Id == id)
            ?? throw new InvalidOperationException($"StrategyWeights with id {id} not found for account {accountId}.");
        target.Validate();

        // General SetActive only ever affects the general (ApplicableRegime == null) rows —
        // regime-specific rows are managed independently via SetRegimeActiveAsync.
        var general = await db.StrategyWeights
            .Where(w => w.AccountId == accountId && w.ApplicableRegime == null)
            .ToListAsync();
        foreach (var w in general)
            w.IsActive = w.Id == id;
        await db.SaveChangesAsync();
    }

    public async Task SetRegimeActiveAsync(int accountId, int id, MarketRegime regime)
    {
        var target = await db.StrategyWeights.FirstOrDefaultAsync(w => w.AccountId == accountId && w.Id == id)
            ?? throw new InvalidOperationException($"StrategyWeights with id {id} not found for account {accountId}.");
        target.Validate();

        var regimeRows = await db.StrategyWeights
            .Where(w => w.AccountId == accountId && w.ApplicableRegime == regime)
            .ToListAsync();
        foreach (var w in regimeRows)
            w.IsActive = w.Id == id;

        target.ApplicableRegime = regime;
        if (!regimeRows.Any(w => w.Id == id))
            target.IsActive = true;

        await db.SaveChangesAsync();
    }

    public async Task UpdateWeightsAsync(int accountId, StrategyWeightsUpdate update)
    {
        var active = await db.StrategyWeights.FirstOrDefaultAsync(w =>
            w.AccountId == accountId && w.IsActive && w.ApplicableRegime == null)
            ?? throw new InvalidOperationException($"No active StrategyWeights found for account {accountId}.");

        active.RsiWeight = update.RsiWeight;
        active.MacdWeight = update.MacdWeight;
        active.VolumeWeight = update.VolumeWeight;
        active.SetupQualityWeight = update.SetupQualityWeight;
        active.RelativeStrengthWeight = update.RelativeStrengthWeight;
        active.PriceLevelWeight = update.PriceLevelWeight;
        active.ForwardSentimentWeight = update.ForwardSentimentWeight;
        active.ForwardFundamentalWeight = update.ForwardFundamentalWeight;
        active.ForwardFilingWeight = update.ForwardFilingWeight;
        active.BuyThreshold = update.BuyThreshold;
        active.WatchThreshold = update.WatchThreshold;
        active.StopLossPctDefault = update.StopLossPctDefault;
        active.Validate();
        active.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    public async Task SeedDefaultAsync(int accountId, CancellationToken ct = default)
    {
        var alreadySeeded = await db.StrategyWeights.AnyAsync(w => w.AccountId == accountId, ct);
        if (alreadySeeded) return;

        var now = DateTime.UtcNow;
        db.StrategyWeights.Add(new StrategyWeights
        {
            AccountId = accountId,
            RsiWeight = 0.23m,
            MacdWeight = 0.12m,
            VolumeWeight = 0.28m,
            SetupQualityWeight = 0.16m,
            RelativeStrengthWeight = 0.14m,
            PriceLevelWeight = 0.07m,
            ForwardSentimentWeight = 0.45m,
            ForwardFundamentalWeight = 0.30m,
            ForwardFilingWeight = 0.25m,
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
