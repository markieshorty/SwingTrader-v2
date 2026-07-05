using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Models;

namespace SwingTrader.Data;

public class SwingTraderDbContext(DbContextOptions<SwingTraderDbContext> options) : DbContext(options)
{
    public DbSet<WatchlistItem> WatchlistItems => Set<WatchlistItem>();
    public DbSet<StockSignal> StockSignals => Set<StockSignal>();
    public DbSet<Trade> Trades => Set<Trade>();
    public DbSet<DailyReport> DailyReports => Set<DailyReport>();
    public DbSet<PortfolioSnapshot> PortfolioSnapshots => Set<PortfolioSnapshot>();
    public DbSet<StockCandle> StockCandles => Set<StockCandle>();
    public DbSet<WatchlistHistory> WatchlistHistory => Set<WatchlistHistory>();
    public DbSet<TradeApproval> TradeApprovals => Set<TradeApproval>();
    public DbSet<StrategyWeights> StrategyWeights => Set<StrategyWeights>();
    public DbSet<TierEvaluationRecord> TierEvaluationRecords => Set<TierEvaluationRecord>();
    public DbSet<WorkerHeartbeat> WorkerHeartbeats => Set<WorkerHeartbeat>();
    public DbSet<RefinementSuggestion> RefinementSuggestions => Set<RefinementSuggestion>();
    public DbSet<SystemChecklist> SystemChecklists => Set<SystemChecklist>();
    public DbSet<ReadinessSnapshot> ReadinessSnapshots => Set<ReadinessSnapshot>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<AppUser> AppUsers => Set<AppUser>();
    public DbSet<AccountInvite> AccountInvites => Set<AccountInvite>();
    public DbSet<UserApiKey> UserApiKeys => Set<UserApiKey>();
    public DbSet<JobLogEntry> JobLogEntries => Set<JobLogEntry>();
    public DbSet<NotificationRecipient> NotificationRecipients => Set<NotificationRecipient>();

    // The 'system' Account created by the AddMultiTenancy migration - all
    // pre-existing (pre-Phase-10c) data defaults to this AccountId.
    public const int SystemAccountId = 1;

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        configurationBuilder.Properties<DateTime>().HaveColumnType("datetime2");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Account>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).IsRequired().HasMaxLength(200);
            e.Property(x => x.T212AccountId).HasMaxLength(100);
            e.HasData(new Account
            {
                Id = SystemAccountId,
                Name = "system",
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
        });

        modelBuilder.Entity<AppUser>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).IsRequired().HasMaxLength(200);
            e.Property(x => x.Email).IsRequired().HasMaxLength(320);
            e.Property(x => x.DisplayName).IsRequired().HasMaxLength(200);
            e.HasIndex(x => x.UserId).IsUnique();
            e.HasIndex(x => x.AccountId);
        });

        modelBuilder.Entity<AccountInvite>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.InvitedByUserId).IsRequired().HasMaxLength(200);
            e.Property(x => x.InvitedEmail).IsRequired().HasMaxLength(320);
            e.Property(x => x.Token).IsRequired().HasMaxLength(64);
            e.Property(x => x.AcceptedByUserId).HasMaxLength(200);
            e.HasIndex(x => x.Token).IsUnique();
            e.HasIndex(x => x.AccountId);
        });

        modelBuilder.Entity<WatchlistItem>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Symbol).IsRequired().HasMaxLength(20);
            e.Property(x => x.CompanyName).IsRequired().HasMaxLength(200);
            e.Property(x => x.Sector).IsRequired().HasMaxLength(100);
            // Was globally unique on Symbol pre-multi-tenancy; every account
            // now has its own independent watchlist, so the same symbol can
            // legitimately appear once per account.
            e.HasIndex(x => new { x.AccountId, x.Symbol }).IsUnique();
            e.HasQueryFilter(x => x.IsActive);
        });

        modelBuilder.Entity<StockSignal>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Symbol).IsRequired().HasMaxLength(20);
            e.Property(x => x.CurrentPrice).HasPrecision(18, 8);
            e.Property(x => x.Rsi14).HasPrecision(18, 8);
            e.Property(x => x.Macd).HasPrecision(18, 8);
            e.Property(x => x.MacdSignal).HasPrecision(18, 8);
            e.Property(x => x.MacdHistogram).HasPrecision(18, 8);
            e.Property(x => x.VolumeRatio).HasPrecision(18, 8);
            e.Property(x => x.BollingerUpper).HasPrecision(18, 8);
            e.Property(x => x.BollingerLower).HasPrecision(18, 8);
            e.Property(x => x.BollingerMid).HasPrecision(18, 8);
            e.Property(x => x.Ema9).HasPrecision(18, 8);
            e.Property(x => x.Ema21).HasPrecision(18, 8);
            e.Property(x => x.SentimentScore).HasPrecision(18, 8);
            e.Property(x => x.ConvictionScore).HasPrecision(18, 8);
            e.Property(x => x.RsiScore).HasPrecision(18, 8);
            e.Property(x => x.MacdScore).HasPrecision(18, 8);
            e.Property(x => x.VolumeScore).HasPrecision(18, 8);
            e.Property(x => x.SentimentComponentScore).HasPrecision(18, 8);
            e.Property(x => x.SetupQualityScore).HasPrecision(18, 8);
            e.Property(x => x.RelativeStrengthScore).HasPrecision(18, 8);
            e.Property(x => x.PriceLevelScore).HasPrecision(18, 8);
            e.Property(x => x.FundamentalMomentumScore).HasPrecision(18, 8);
            e.Property(x => x.StockReturn5d).HasPrecision(18, 8);
            e.Property(x => x.SectorReturn5d).HasPrecision(18, 8);
            e.Property(x => x.RelativeReturn).HasPrecision(18, 8);
            e.Property(x => x.NearestSupport).HasPrecision(18, 8);
            e.Property(x => x.NearestResistance).HasPrecision(18, 8);
            e.Property(x => x.EpsSurprisePct).HasPrecision(18, 8);
            e.Property(x => x.CalculatedStopLoss).HasPrecision(18, 8);
            e.Property(x => x.CalculatedTarget).HasPrecision(18, 8);
            e.Property(x => x.RiskRewardRatio).HasPrecision(18, 8);
            e.HasIndex(x => new { x.AccountId, x.Symbol, x.SignalDate });
        });

        modelBuilder.Entity<Trade>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Symbol).IsRequired().HasMaxLength(20);
            e.Property(x => x.EntryPrice).HasPrecision(18, 8);
            e.Property(x => x.ExitPrice).HasPrecision(18, 8);
            e.Property(x => x.Quantity).HasPrecision(18, 8);
            e.Property(x => x.StopLossPrice).HasPrecision(18, 8);
            e.Property(x => x.TargetPrice).HasPrecision(18, 8);
            e.Property(x => x.RealizedPnl).HasPrecision(18, 8);
            e.Property(x => x.TrailingStopPrice).HasPrecision(18, 8);
            e.Property(x => x.SpyPriceAtEntry).HasPrecision(18, 8);
            e.Property(x => x.SpyPriceAtExit).HasPrecision(18, 8);
            e.Property(x => x.VixAtEntry).HasPrecision(18, 8);
            e.Property(x => x.SpyReturnDuringTrade).HasPrecision(18, 8);
            e.HasIndex(x => new { x.AccountId, x.Symbol, x.Status });
        });

        modelBuilder.Entity<DailyReport>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.PortfolioValue).HasPrecision(18, 8);
            e.Property(x => x.DailyPnl).HasPrecision(18, 8);
            e.HasIndex(x => new { x.AccountId, x.ReportDate }).IsUnique();
        });

        modelBuilder.Entity<PortfolioSnapshot>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.TotalCapital).HasPrecision(18, 8);
            e.Property(x => x.LockedCapital).HasPrecision(18, 8);
            e.Property(x => x.ReserveCapital).HasPrecision(18, 8);
            e.Property(x => x.ActiveCapital).HasPrecision(18, 8);
            e.Property(x => x.CashAvailable).HasPrecision(18, 8);
            e.Property(x => x.OpenPositionsValue).HasPrecision(18, 8);
            e.Property(x => x.TotalPnl).HasPrecision(18, 8);
            e.HasIndex(x => new { x.AccountId, x.SnapshotDate });
        });

        modelBuilder.Entity<StockCandle>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Symbol).IsRequired().HasMaxLength(20);
            e.Property(x => x.Resolution).IsRequired().HasMaxLength(10);
            e.Property(x => x.Open).HasPrecision(18, 8);
            e.Property(x => x.High).HasPrecision(18, 8);
            e.Property(x => x.Low).HasPrecision(18, 8);
            e.Property(x => x.Close).HasPrecision(18, 8);
            // Candles are market data, not account-specific, but still carry
            // AccountId via BaseEntity for consistency/query-filtering; the
            // uniqueness constraint stays scoped to the market data itself.
            e.HasIndex(x => new { x.Symbol, x.Resolution, x.Timestamp }).IsUnique();
        });

        modelBuilder.Entity<WatchlistHistory>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Symbol).IsRequired().HasMaxLength(20);
            e.Property(x => x.CompanyName).IsRequired().HasMaxLength(200);
            e.Property(x => x.Reason).IsRequired();
            e.Property(x => x.ConvictionAtAdd).HasPrecision(18, 8);
            e.HasIndex(x => new { x.AccountId, x.Symbol, x.WeekStarting });
        });

        modelBuilder.Entity<TradeApproval>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.ApprovalToken).IsRequired().HasMaxLength(64);
            e.Property(x => x.ApprovedVia).HasMaxLength(20);
            e.Property(x => x.ApprovedSymbols).HasMaxLength(500);
            e.HasIndex(x => x.ApprovalToken).IsUnique();
            e.HasIndex(x => new { x.AccountId, x.TradeDate }).IsUnique();
        });

        modelBuilder.Entity<StrategyWeights>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.RsiWeight).HasPrecision(18, 8);
            e.Property(x => x.MacdWeight).HasPrecision(18, 8);
            e.Property(x => x.VolumeWeight).HasPrecision(18, 8);
            e.Property(x => x.SentimentWeight).HasPrecision(18, 8);
            e.Property(x => x.SetupQualityWeight).HasPrecision(18, 8);
            e.Property(x => x.RelativeStrengthWeight).HasPrecision(18, 8);
            e.Property(x => x.PriceLevelWeight).HasPrecision(18, 8);
            e.Property(x => x.FundamentalMomentumWeight).HasPrecision(18, 8);
            e.Property(x => x.BuyThreshold).HasPrecision(18, 8);
            e.Property(x => x.WatchThreshold).HasPrecision(18, 8);
            e.Property(x => x.StopLossPctDefault).HasPrecision(18, 8);
            e.HasIndex(x => x.AccountId);
            e.HasData(new StrategyWeights
            {
                Id = 1,
                AccountId = SystemAccountId,
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
                Notes = "Default starting weights (sum to 1.0) ported from the v1 system.",
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            });
        });

        modelBuilder.Entity<TierEvaluationRecord>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.WinRate).HasPrecision(18, 8);
            e.Property(x => x.AvgReturnPct).HasPrecision(18, 8);
            e.Property(x => x.SharpeRatio).HasPrecision(18, 8);
            e.Property(x => x.MaxDrawdownPct).HasPrecision(18, 8);
            e.HasIndex(x => new { x.AccountId, x.EvaluatedAt });
        });

        modelBuilder.Entity<WorkerHeartbeat>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.WorkerName).IsRequired().HasMaxLength(50);
            e.Property(x => x.LastRunResult).IsRequired().HasMaxLength(20);
            // Worker heartbeats are process-wide (one Functions app), not
            // per-account, but still carry AccountId via BaseEntity - kept
            // unique on WorkerName alone rather than per-account.
            e.HasIndex(x => x.WorkerName).IsUnique();
        });

        modelBuilder.Entity<RefinementSuggestion>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.OverallWinRate).HasPrecision(18, 8);
            e.Property(x => x.CurrentWeightsJson).IsRequired();
            e.Property(x => x.SuggestedWeightsJson).IsRequired();
            e.Property(x => x.ComponentAnalysisJson).IsRequired();
            e.Property(x => x.MarketAdjustedWinRate).HasPrecision(18, 8);
            e.HasIndex(x => new { x.AccountId, x.GeneratedAt });
        });

        modelBuilder.Entity<SystemChecklist>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.CheckName).IsRequired().HasMaxLength(100);
            e.HasIndex(x => new { x.AccountId, x.CheckName }).IsUnique();
            e.HasData(
                new SystemChecklist { Id = 1, AccountId = SystemAccountId, CheckName = "AccountIdVerified", CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
                new SystemChecklist { Id = 2, AccountId = SystemAccountId, CheckName = "LiveTradingConfirmed", CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) }
            );
        });

        modelBuilder.Entity<ReadinessSnapshot>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.ObservedWinRate).HasPrecision(18, 8);
            e.Property(x => x.TradesPerWeekWeighted).HasPrecision(18, 8);
            e.HasIndex(x => new { x.AccountId, x.SnapshotDate }).IsUnique();
        });

        modelBuilder.Entity<UserApiKey>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Provider).IsRequired().HasMaxLength(50);
            e.Property(x => x.EncryptedValue).IsRequired();
            e.Property(x => x.EncryptedDek).IsRequired();
            e.Property(x => x.LastTestResult).HasMaxLength(500);
            e.HasIndex(x => new { x.AccountId, x.Provider }).IsUnique();
        });

        modelBuilder.Entity<JobLogEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.JobType).IsRequired().HasMaxLength(50);
            e.Property(x => x.ErrorMessage).HasMaxLength(2000);
            e.HasIndex(x => new { x.AccountId, x.JobType, x.JobDate }).IsUnique();
        });

        modelBuilder.Entity<NotificationRecipient>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Email).IsRequired().HasMaxLength(320);
            e.HasIndex(x => new { x.AccountId, x.Email }).IsUnique();
        });

        // FK constraint from every scoped (BaseEntity-derived) table's
        // AccountId column to Accounts(Id), applied generically rather than
        // repeating HasOne/WithMany in all 14 entity blocks above. Restrict
        // (not Cascade) so an Account row can never be deleted out from
        // under trading data by accident.
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (entityType.ClrType != typeof(Account) && typeof(BaseEntity).IsAssignableFrom(entityType.ClrType))
            {
                modelBuilder.Entity(entityType.ClrType)
                    .HasOne(typeof(Account))
                    .WithMany()
                    .HasForeignKey(nameof(BaseEntity.AccountId))
                    .OnDelete(DeleteBehavior.Restrict);
            }
        }

        SeedWatchlist(modelBuilder);
    }

    private static void SeedWatchlist(ModelBuilder modelBuilder)
    {
        var seedDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var items = new[]
        {
            new WatchlistItem { Id = 1, AccountId = SystemAccountId, Symbol = "AAPL", CompanyName = "Apple Inc.", Sector = "Technology", IsActive = true, CreatedAt = seedDate, UpdatedAt = seedDate },
            new WatchlistItem { Id = 2, AccountId = SystemAccountId, Symbol = "MSFT", CompanyName = "Microsoft Corporation", Sector = "Technology", IsActive = true, CreatedAt = seedDate, UpdatedAt = seedDate },
            new WatchlistItem { Id = 3, AccountId = SystemAccountId, Symbol = "NVDA", CompanyName = "NVIDIA Corporation", Sector = "Technology", IsActive = true, CreatedAt = seedDate, UpdatedAt = seedDate },
            new WatchlistItem { Id = 4, AccountId = SystemAccountId, Symbol = "AMD", CompanyName = "Advanced Micro Devices", Sector = "Technology", IsActive = true, CreatedAt = seedDate, UpdatedAt = seedDate },
            new WatchlistItem { Id = 5, AccountId = SystemAccountId, Symbol = "GOOGL", CompanyName = "Alphabet Inc.", Sector = "Technology", IsActive = true, CreatedAt = seedDate, UpdatedAt = seedDate },
            new WatchlistItem { Id = 6, AccountId = SystemAccountId, Symbol = "JNJ", CompanyName = "Johnson & Johnson", Sector = "Healthcare", IsActive = true, CreatedAt = seedDate, UpdatedAt = seedDate },
            new WatchlistItem { Id = 7, AccountId = SystemAccountId, Symbol = "UNH", CompanyName = "UnitedHealth Group", Sector = "Healthcare", IsActive = true, CreatedAt = seedDate, UpdatedAt = seedDate },
            new WatchlistItem { Id = 8, AccountId = SystemAccountId, Symbol = "PFE", CompanyName = "Pfizer Inc.", Sector = "Healthcare", IsActive = true, CreatedAt = seedDate, UpdatedAt = seedDate },
            new WatchlistItem { Id = 9, AccountId = SystemAccountId, Symbol = "ABBV", CompanyName = "AbbVie Inc.", Sector = "Healthcare", IsActive = true, CreatedAt = seedDate, UpdatedAt = seedDate },
            new WatchlistItem { Id = 10, AccountId = SystemAccountId, Symbol = "MRK", CompanyName = "Merck & Co.", Sector = "Healthcare", IsActive = true, CreatedAt = seedDate, UpdatedAt = seedDate },
            new WatchlistItem { Id = 11, AccountId = SystemAccountId, Symbol = "JPM", CompanyName = "JPMorgan Chase & Co.", Sector = "Finance", IsActive = true, CreatedAt = seedDate, UpdatedAt = seedDate },
            new WatchlistItem { Id = 12, AccountId = SystemAccountId, Symbol = "BAC", CompanyName = "Bank of America", Sector = "Finance", IsActive = true, CreatedAt = seedDate, UpdatedAt = seedDate },
            new WatchlistItem { Id = 13, AccountId = SystemAccountId, Symbol = "GS", CompanyName = "Goldman Sachs Group", Sector = "Finance", IsActive = true, CreatedAt = seedDate, UpdatedAt = seedDate },
            new WatchlistItem { Id = 14, AccountId = SystemAccountId, Symbol = "MS", CompanyName = "Morgan Stanley", Sector = "Finance", IsActive = true, CreatedAt = seedDate, UpdatedAt = seedDate },
            new WatchlistItem { Id = 15, AccountId = SystemAccountId, Symbol = "V", CompanyName = "Visa Inc.", Sector = "Finance", IsActive = true, CreatedAt = seedDate, UpdatedAt = seedDate },
            new WatchlistItem { Id = 16, AccountId = SystemAccountId, Symbol = "AMZN", CompanyName = "Amazon.com Inc.", Sector = "Consumer", IsActive = true, CreatedAt = seedDate, UpdatedAt = seedDate },
            new WatchlistItem { Id = 17, AccountId = SystemAccountId, Symbol = "TSLA", CompanyName = "Tesla Inc.", Sector = "Consumer", IsActive = true, CreatedAt = seedDate, UpdatedAt = seedDate },
            new WatchlistItem { Id = 18, AccountId = SystemAccountId, Symbol = "WMT", CompanyName = "Walmart Inc.", Sector = "Consumer", IsActive = true, CreatedAt = seedDate, UpdatedAt = seedDate },
            new WatchlistItem { Id = 19, AccountId = SystemAccountId, Symbol = "HD", CompanyName = "The Home Depot", Sector = "Consumer", IsActive = true, CreatedAt = seedDate, UpdatedAt = seedDate },
            new WatchlistItem { Id = 20, AccountId = SystemAccountId, Symbol = "MCD", CompanyName = "McDonald's Corporation", Sector = "Consumer", IsActive = true, CreatedAt = seedDate, UpdatedAt = seedDate },
        };

        modelBuilder.Entity<WatchlistItem>().HasData(items);
    }
}
