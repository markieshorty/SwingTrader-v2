using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SwingTrader.Core.Models;

namespace SwingTrader.Data;

public class SwingTraderDbContext(DbContextOptions<SwingTraderDbContext> options) : DbContext(options)
{
    public DbSet<Watchlist> Watchlists => Set<Watchlist>();
    public DbSet<WatchlistItem> WatchlistItems => Set<WatchlistItem>();
    public DbSet<StockSignal> StockSignals => Set<StockSignal>();
    public DbSet<Trade> Trades => Set<Trade>();
    public DbSet<DailyReport> DailyReports => Set<DailyReport>();
    public DbSet<PortfolioSnapshot> PortfolioSnapshots => Set<PortfolioSnapshot>();
    public DbSet<StockCandle> StockCandles => Set<StockCandle>();
    public DbSet<WatchlistHistory> WatchlistHistory => Set<WatchlistHistory>();
    public DbSet<TradeApproval> TradeApprovals => Set<TradeApproval>();
    public DbSet<StrategyWeights> StrategyWeights => Set<StrategyWeights>();
    public DbSet<WorkerHeartbeat> WorkerHeartbeats => Set<WorkerHeartbeat>();
    public DbSet<RefinementSuggestion> RefinementSuggestions => Set<RefinementSuggestion>();
    public DbSet<SystemChecklist> SystemChecklists => Set<SystemChecklist>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<AppUser> AppUsers => Set<AppUser>();
    public DbSet<AccountInvite> AccountInvites => Set<AccountInvite>();
    public DbSet<UserApiKey> UserApiKeys => Set<UserApiKey>();
    public DbSet<JobLogEntry> JobLogEntries => Set<JobLogEntry>();
    public DbSet<NotificationRecipient> NotificationRecipients => Set<NotificationRecipient>();
    public DbSet<AccountRiskProfile> AccountRiskProfiles => Set<AccountRiskProfile>();
    public DbSet<AdminActionLog> AdminActionLogs => Set<AdminActionLog>();
    public DbSet<ActivityLog> ActivityLogs => Set<ActivityLog>();
    public DbSet<HistoricalCandle> HistoricalCandles => Set<HistoricalCandle>();
    public DbSet<BacktestRun> BacktestRuns => Set<BacktestRun>();
    public DbSet<SentimentArticle> SentimentArticles => Set<SentimentArticle>();
    public DbSet<SentimentDailyScore> SentimentDailyScores => Set<SentimentDailyScore>();
    public DbSet<Filing> Filings => Set<Filing>();
    public DbSet<FilingDelta> FilingDeltas => Set<FilingDelta>();
    public DbSet<EconomicLink> EconomicLinks => Set<EconomicLink>();

    // The 'system' Account created by the AddMultiTenancy migration - all
    // pre-existing (pre-Phase-10c) data defaults to this AccountId.
    public const int SystemAccountId = 1;

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        // SQL Server returns DateTime with DateTimeKind.Unspecified; mark them
        // all as UTC so System.Text.Json serializes with a Z suffix and Angular
        // converts to local time (BST etc.) correctly.
        configurationBuilder.Properties<DateTime>()
            .HaveColumnType("datetime2")
            .HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>()
            .HaveConversion<NullableUtcDateTimeConverter>();
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

        modelBuilder.Entity<AdminActionLog>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.AdminUserId).IsRequired().HasMaxLength(200);
            e.Property(x => x.TargetUserId).IsRequired().HasMaxLength(200);
            e.Property(x => x.Action).IsRequired().HasMaxLength(50);
            e.HasIndex(x => x.TargetUserId);
            e.HasIndex(x => x.PerformedAt);
        });

        modelBuilder.Entity<Watchlist>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).IsRequired().HasMaxLength(100);
            e.Property(x => x.Description).HasMaxLength(500);
            e.HasIndex(x => x.AccountId);
        });

        modelBuilder.Entity<WatchlistItem>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Symbol).IsRequired().HasMaxLength(20);
            e.Property(x => x.CompanyName).IsRequired().HasMaxLength(200);
            e.Property(x => x.Sector).IsRequired().HasMaxLength(100);
            // Was globally unique on Symbol pre-multi-tenancy, then unique per
            // account; now unique per watchlist, since the same symbol can
            // legitimately sit on more than one of an account's watchlists.
            e.HasIndex(x => new { x.WatchlistId, x.Symbol }).IsUnique();
            e.HasOne(x => x.Watchlist)
                .WithMany(w => w.Items)
                .HasForeignKey(x => x.WatchlistId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => x.IsActive);
        });

        modelBuilder.Entity<StockSignal>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Symbol).IsRequired().HasMaxLength(20);
            e.Property(x => x.CompanyName).HasMaxLength(200);
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
            e.Property(x => x.CompanyName).HasMaxLength(200);
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
            // Scoped by TradingMode too: a Demo and a Live report can legitimately
            // exist for the same account and date, and every read now filters by
            // mode - a (AccountId, ReportDate)-only unique index would reject the
            // second mode's report for that day (see PortfolioSnapshot.TradingMode).
            e.HasIndex(x => new { x.AccountId, x.TradingMode, x.ReportDate }).IsUnique();
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
            e.Property(x => x.ApprovedVia).HasMaxLength(20);
            e.Property(x => x.ApprovedSymbols).HasMaxLength(500);
            // Scoped by TradingMode too: a Demo and a Live approval can legitimately
            // exist for the same account and date, and every read now filters by
            // mode - a (AccountId, TradeDate)-only unique index would reject the
            // second mode's approval for that day (see PortfolioSnapshot.TradingMode).
            e.HasIndex(x => new { x.AccountId, x.TradingMode, x.TradeDate }).IsUnique();
        });

        modelBuilder.Entity<StrategyWeights>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.RsiWeight).HasPrecision(18, 8);
            e.Property(x => x.MacdWeight).HasPrecision(18, 8);
            e.Property(x => x.VolumeWeight).HasPrecision(18, 8);
            e.Property(x => x.SetupQualityWeight).HasPrecision(18, 8);
            e.Property(x => x.RelativeStrengthWeight).HasPrecision(18, 8);
            e.Property(x => x.PriceLevelWeight).HasPrecision(18, 8);
            e.Property(x => x.ForwardSentimentWeight).HasPrecision(18, 8);
            e.Property(x => x.ForwardFundamentalWeight).HasPrecision(18, 8);
            e.Property(x => x.BuyThreshold).HasPrecision(18, 8);
            e.Property(x => x.WatchThreshold).HasPrecision(18, 8);
            e.Property(x => x.StopLossPctDefault).HasPrecision(18, 8);
            e.HasIndex(x => x.AccountId);
            e.HasData(new StrategyWeights
            {
                Id = 1,
                AccountId = SystemAccountId,
                RsiWeight = 0.23m,
                MacdWeight = 0.12m,
                VolumeWeight = 0.28m,
                SetupQualityWeight = 0.16m,
                RelativeStrengthWeight = 0.14m,
                PriceLevelWeight = 0.07m,
                ForwardSentimentWeight = 0.60m,
                ForwardFundamentalWeight = 0.40m,
                BuyThreshold = 6.0m,
                WatchThreshold = 5.0m,
                StopLossPctDefault = 0.05m,
                IsActive = true,
                Source = "Default",
                Notes = "Default gate weights (6, sum to 1.0) + forward blend 60/40.",
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            });
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

        modelBuilder.Entity<UserApiKey>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Provider).IsRequired().HasMaxLength(50);
            e.Property(x => x.EncryptedValue).IsRequired();
            e.Property(x => x.EncryptedDek).IsRequired();
            e.Property(x => x.LastTestResult).HasMaxLength(500);
            e.HasIndex(x => new { x.AccountId, x.Provider }).IsUnique();
        });

        modelBuilder.Entity<AccountRiskProfile>(e =>
        {
            // Fractions like 0.075 (7.5%) need 4dp - the convention default
            // decimal(18,2) would silently truncate to whole percents. DB
            // defaults double as the backfill for pre-existing rows, so an
            // account created before this migration gets the same 5%/8%
            // behaviour it effectively had under the old hardcoded tables.
            e.Property(x => x.StopLossPct).HasPrecision(5, 4).HasDefaultValue(0.05m);
            e.Property(x => x.TargetPct).HasPrecision(5, 4).HasDefaultValue(0.08m);
            e.Property(x => x.FlatPositionPct).HasPrecision(5, 4).HasDefaultValue(0.10m);
            // One risk book per (account, regime).
            e.HasIndex(x => new { x.AccountId, x.Regime }).IsUnique();
        });

        // Sentiment archive (edge-plan Phase 4). Account-agnostic; the unique
        // score index is what makes multi-account same-day research idempotent.
        modelBuilder.Entity<SentimentArticle>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Symbol).IsRequired().HasMaxLength(10);
            e.Property(x => x.Source).IsRequired().HasMaxLength(20);
            e.Property(x => x.Title).IsRequired().HasMaxLength(500);
            e.Property(x => x.Url).HasMaxLength(2000);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.HasIndex(x => new { x.Symbol, x.Date });
            e.HasIndex(x => x.Date); // pruning scans by age alone
        });

        modelBuilder.Entity<SentimentDailyScore>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Symbol).IsRequired().HasMaxLength(10);
            e.Property(x => x.Model).HasMaxLength(100);
            e.Property(x => x.Score).HasPrecision(5, 4);
            // One score per symbol per day, however many accounts researched it.
            e.HasIndex(x => new { x.Symbol, x.Date }).IsUnique();
        });

        // Filing-delta store (docs/filing-delta-plan). Platform-level like
        // HistoricalCandle - one copy per document for every account.
        modelBuilder.Entity<Filing>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Symbol).IsRequired().HasMaxLength(10);
            e.Property(x => x.Cik).IsRequired().HasMaxLength(10);
            e.Property(x => x.AccessionNumber).IsRequired().HasMaxLength(30);
            e.Property(x => x.FilingType).IsRequired().HasMaxLength(10);
            e.Property(x => x.PrimaryDocument).IsRequired().HasMaxLength(200);
            e.Property(x => x.RiskFactorsHash).HasMaxLength(64);
            e.Property(x => x.MdaHash).HasMaxLength(64);
            // Accession numbers are EDGAR-globally unique - the sync's
            // idempotency key across overlapping runs.
            e.HasIndex(x => x.AccessionNumber).IsUnique();
            e.HasIndex(x => new { x.Symbol, x.FilingType, x.FiledAt });
        });

        modelBuilder.Entity<FilingDelta>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Symbol).IsRequired().HasMaxLength(10);
            e.Property(x => x.Direction).HasPrecision(5, 4);
            e.Property(x => x.Materiality).HasPrecision(5, 4);
            e.Property(x => x.Delta).HasPrecision(5, 4);
            e.Property(x => x.Categories).HasMaxLength(500);
            e.Property(x => x.Model).HasMaxLength(100);
            // One delta per filing - re-scoring replaces, never duplicates.
            e.HasIndex(x => x.FilingId).IsUnique();
            e.HasIndex(x => new { x.Symbol, x.FiledAt });
        });

        // Second-hop economic graph (docs/second-hop-plan). Platform-level;
        // suppression is the human kill switch for hallucinated links.
        modelBuilder.Entity<EconomicLink>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Symbol).IsRequired().HasMaxLength(10);
            e.Property(x => x.LinkedName).IsRequired().HasMaxLength(200);
            e.Property(x => x.LinkedTicker).HasMaxLength(10);
            e.Property(x => x.Relation).IsRequired().HasMaxLength(20);
            e.Property(x => x.TransmissionNote).IsRequired().HasMaxLength(500);
            e.Property(x => x.Strength).HasPrecision(5, 4);
            e.Property(x => x.Rationale).IsRequired().HasMaxLength(500);
            e.Property(x => x.Model).HasMaxLength(100);
            e.HasIndex(x => x.Symbol);
            e.HasIndex(x => x.LinkedTicker);
        });

        modelBuilder.Entity<HistoricalCandle>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Symbol).IsRequired().HasMaxLength(10);
            // One row per symbol per day; the sync job relies on this to be
            // idempotent across overlapping runs.
            e.HasIndex(x => new { x.Symbol, x.Date }).IsUnique();
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

    // Seeded watchlist row every pre-existing WatchlistItem is backfilled onto -
    // see the AddMultipleWatchlists migration for the equivalent backfill
    // applied to every other (non-seed-data) account's existing items.
    public const int SystemWatchlistId = 1;

    private sealed class UtcDateTimeConverter()
        : ValueConverter<DateTime, DateTime>(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private sealed class NullableUtcDateTimeConverter()
        : ValueConverter<DateTime?, DateTime?>(v => v, v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : null);

    private static void SeedWatchlist(ModelBuilder modelBuilder)
    {
        var seedDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        modelBuilder.Entity<Watchlist>().HasData(new Watchlist
        {
            Id = SystemWatchlistId,
            AccountId = SystemAccountId,
            Name = "AI Picks",
            Type = SwingTrader.Core.Enums.WatchlistType.AiManaged,
            IsEnabled = true,
            IsDefault = true,
            CreatedAt = seedDate,
            UpdatedAt = seedDate,
        });

        var items = new[]
        {
            new WatchlistItem { Id = 1, AccountId = SystemAccountId, WatchlistId = SystemWatchlistId, Symbol = "AAPL", CompanyName = "Apple Inc.", Sector = "Technology", IsActive = true, CreatedAt = seedDate, UpdatedAt = seedDate },
            new WatchlistItem { Id = 2, AccountId = SystemAccountId, WatchlistId = SystemWatchlistId, Symbol = "MSFT", CompanyName = "Microsoft Corporation", Sector = "Technology", IsActive = true, CreatedAt = seedDate, UpdatedAt = seedDate },
            new WatchlistItem { Id = 3, AccountId = SystemAccountId, WatchlistId = SystemWatchlistId, Symbol = "NVDA", CompanyName = "NVIDIA Corporation", Sector = "Technology", IsActive = true, CreatedAt = seedDate, UpdatedAt = seedDate },
            new WatchlistItem { Id = 4, AccountId = SystemAccountId, WatchlistId = SystemWatchlistId, Symbol = "AMD", CompanyName = "Advanced Micro Devices", Sector = "Technology", IsActive = true, CreatedAt = seedDate, UpdatedAt = seedDate },
            new WatchlistItem { Id = 5, AccountId = SystemAccountId, WatchlistId = SystemWatchlistId, Symbol = "GOOGL", CompanyName = "Alphabet Inc.", Sector = "Technology", IsActive = true, CreatedAt = seedDate, UpdatedAt = seedDate },
            new WatchlistItem { Id = 6, AccountId = SystemAccountId, WatchlistId = SystemWatchlistId, Symbol = "JNJ", CompanyName = "Johnson & Johnson", Sector = "Healthcare", IsActive = true, CreatedAt = seedDate, UpdatedAt = seedDate },
            new WatchlistItem { Id = 7, AccountId = SystemAccountId, WatchlistId = SystemWatchlistId, Symbol = "UNH", CompanyName = "UnitedHealth Group", Sector = "Healthcare", IsActive = true, CreatedAt = seedDate, UpdatedAt = seedDate },
            new WatchlistItem { Id = 8, AccountId = SystemAccountId, WatchlistId = SystemWatchlistId, Symbol = "PFE", CompanyName = "Pfizer Inc.", Sector = "Healthcare", IsActive = true, CreatedAt = seedDate, UpdatedAt = seedDate },
            new WatchlistItem { Id = 9, AccountId = SystemAccountId, WatchlistId = SystemWatchlistId, Symbol = "ABBV", CompanyName = "AbbVie Inc.", Sector = "Healthcare", IsActive = true, CreatedAt = seedDate, UpdatedAt = seedDate },
            new WatchlistItem { Id = 10, AccountId = SystemAccountId, WatchlistId = SystemWatchlistId, Symbol = "MRK", CompanyName = "Merck & Co.", Sector = "Healthcare", IsActive = true, CreatedAt = seedDate, UpdatedAt = seedDate },
            new WatchlistItem { Id = 11, AccountId = SystemAccountId, WatchlistId = SystemWatchlistId, Symbol = "JPM", CompanyName = "JPMorgan Chase & Co.", Sector = "Finance", IsActive = true, CreatedAt = seedDate, UpdatedAt = seedDate },
            new WatchlistItem { Id = 12, AccountId = SystemAccountId, WatchlistId = SystemWatchlistId, Symbol = "BAC", CompanyName = "Bank of America", Sector = "Finance", IsActive = true, CreatedAt = seedDate, UpdatedAt = seedDate },
            new WatchlistItem { Id = 13, AccountId = SystemAccountId, WatchlistId = SystemWatchlistId, Symbol = "GS", CompanyName = "Goldman Sachs Group", Sector = "Finance", IsActive = true, CreatedAt = seedDate, UpdatedAt = seedDate },
            new WatchlistItem { Id = 14, AccountId = SystemAccountId, WatchlistId = SystemWatchlistId, Symbol = "MS", CompanyName = "Morgan Stanley", Sector = "Finance", IsActive = true, CreatedAt = seedDate, UpdatedAt = seedDate },
            new WatchlistItem { Id = 15, AccountId = SystemAccountId, WatchlistId = SystemWatchlistId, Symbol = "V", CompanyName = "Visa Inc.", Sector = "Finance", IsActive = true, CreatedAt = seedDate, UpdatedAt = seedDate },
            new WatchlistItem { Id = 16, AccountId = SystemAccountId, WatchlistId = SystemWatchlistId, Symbol = "AMZN", CompanyName = "Amazon.com Inc.", Sector = "Consumer", IsActive = true, CreatedAt = seedDate, UpdatedAt = seedDate },
            new WatchlistItem { Id = 17, AccountId = SystemAccountId, WatchlistId = SystemWatchlistId, Symbol = "TSLA", CompanyName = "Tesla Inc.", Sector = "Consumer", IsActive = true, CreatedAt = seedDate, UpdatedAt = seedDate },
            new WatchlistItem { Id = 18, AccountId = SystemAccountId, WatchlistId = SystemWatchlistId, Symbol = "WMT", CompanyName = "Walmart Inc.", Sector = "Consumer", IsActive = true, CreatedAt = seedDate, UpdatedAt = seedDate },
            new WatchlistItem { Id = 19, AccountId = SystemAccountId, WatchlistId = SystemWatchlistId, Symbol = "HD", CompanyName = "The Home Depot", Sector = "Consumer", IsActive = true, CreatedAt = seedDate, UpdatedAt = seedDate },
            new WatchlistItem { Id = 20, AccountId = SystemAccountId, WatchlistId = SystemWatchlistId, Symbol = "MCD", CompanyName = "McDonald's Corporation", Sector = "Consumer", IsActive = true, CreatedAt = seedDate, UpdatedAt = seedDate },
        };

        modelBuilder.Entity<WatchlistItem>().HasData(items);
    }
}
