using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace SwingTrader.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DailyReports",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ReportDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ReportMarkdown = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TopBuysJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TopSellsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    MarketContext = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PortfolioValue = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    DailyPnl = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    WasSent = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyReports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PortfolioSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SnapshotDate = table.Column<DateOnly>(type: "date", nullable: false),
                    TotalCapital = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    LockedCapital = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    ReserveCapital = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    ActiveCapital = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    CashAvailable = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    OpenPositionsValue = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    TotalPnl = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    CurrentTier = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PortfolioSnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReadinessSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SnapshotDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ScoredClosedTrades = table.Column<int>(type: "int", nullable: false),
                    ObservedWinRate = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    TradesPerWeekWeighted = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    RegimeBullCount = table.Column<int>(type: "int", nullable: false),
                    RegimeNeutralCount = table.Column<int>(type: "int", nullable: false),
                    RegimeBearCount = table.Column<int>(type: "int", nullable: false),
                    SystemRunningDays = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReadinessSnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RefinementSuggestions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    GeneratedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AnalysisPeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    AnalysisPeriodEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    TradeCountAnalysed = table.Column<int>(type: "int", nullable: false),
                    WinnerCount = table.Column<int>(type: "int", nullable: false),
                    LoserCount = table.Column<int>(type: "int", nullable: false),
                    OverallWinRate = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    CurrentWeightsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SuggestedWeightsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ComponentAnalysisJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AssessmentSummary = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ConfidenceLevel = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    AppliedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AppliedWeightsId = table.Column<int>(type: "int", nullable: true),
                    RejectedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RejectionNote = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsShadowMode = table.Column<bool>(type: "bit", nullable: false),
                    RegimeBreakdownJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SuggestedRegimeWeightsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MarketAdjustedWinRate = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    UnusualMarketConditions = table.Column<bool>(type: "bit", nullable: false),
                    MarketConditionWarning = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefinementSuggestions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StockCandles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Symbol = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Timestamp = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Open = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    High = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    Low = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    Close = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    Volume = table.Column<long>(type: "bigint", nullable: false),
                    Resolution = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockCandles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StockSignals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Symbol = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SignalDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CurrentPrice = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    Rsi14 = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: true),
                    Macd = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: true),
                    MacdSignal = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: true),
                    MacdHistogram = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: true),
                    VolumeRatio = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: true),
                    BollingerUpper = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: true),
                    BollingerLower = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: true),
                    BollingerMid = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: true),
                    Ema9 = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: true),
                    Ema21 = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: true),
                    SentimentScore = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: true),
                    NewsSummary = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SetupType = table.Column<int>(type: "int", nullable: false),
                    ConvictionScore = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: true),
                    Recommendation = table.Column<int>(type: "int", nullable: false),
                    Reasoning = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    WasExecuted = table.Column<bool>(type: "bit", nullable: false),
                    RsiScore = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: true),
                    MacdScore = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: true),
                    VolumeScore = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: true),
                    SentimentComponentScore = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: true),
                    SetupQualityScore = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: true),
                    RelativeStrengthScore = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: true),
                    PriceLevelScore = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: true),
                    SectorEtf = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    StockReturn5d = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: true),
                    SectorReturn5d = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: true),
                    RelativeReturn = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: true),
                    PriceLevelContext = table.Column<int>(type: "int", nullable: false),
                    NearestSupport = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: true),
                    NearestResistance = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: true),
                    EarningsSetupType = table.Column<int>(type: "int", nullable: false),
                    EpsSurprisePct = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: true),
                    DaysUntilEarnings = table.Column<int>(type: "int", nullable: true),
                    DaysSinceEarnings = table.Column<int>(type: "int", nullable: true),
                    CalculatedStopLoss = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: true),
                    CalculatedTarget = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: true),
                    RiskRewardRatio = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: true),
                    MarketRegimeAtSignal = table.Column<int>(type: "int", nullable: true),
                    FundamentalMomentumScore = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: true),
                    FundamentalNarrative = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AnalystTrend = table.Column<int>(type: "int", nullable: true),
                    InsiderActivity = table.Column<int>(type: "int", nullable: true),
                    EarningsConsistency = table.Column<int>(type: "int", nullable: true),
                    RevenueDirection = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockSignals", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StrategyWeights",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RsiWeight = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    MacdWeight = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    VolumeWeight = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    SentimentWeight = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    SetupQualityWeight = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    RelativeStrengthWeight = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    PriceLevelWeight = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    FundamentalMomentumWeight = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    BuyThreshold = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    WatchThreshold = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    StopLossPctDefault = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ApplicableRegime = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StrategyWeights", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SystemChecklists",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CheckName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemChecklists", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TierEvaluationRecords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EvaluatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EvaluationPeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    EvaluationPeriodEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    CurrentTier = table.Column<int>(type: "int", nullable: false),
                    TotalTrades = table.Column<int>(type: "int", nullable: false),
                    WinRate = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    AvgReturnPct = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    SharpeRatio = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: true),
                    MaxDrawdownPct = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    UnlockCriteriaMet = table.Column<bool>(type: "bit", nullable: false),
                    SuggestedTier = table.Column<int>(type: "int", nullable: false),
                    ActualTierAfter = table.Column<int>(type: "int", nullable: false),
                    WasApplied = table.Column<bool>(type: "bit", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TierEvaluationRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TradeApprovals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TradeDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ApprovalToken = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    IsApproved = table.Column<bool>(type: "bit", nullable: false),
                    ApprovedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ApprovedSymbols = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsExpired = table.Column<bool>(type: "bit", nullable: false),
                    ApprovedVia = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TradeApprovals", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Trades",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Symbol = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Direction = table.Column<int>(type: "int", nullable: false),
                    EntryPrice = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    ExitPrice = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: true),
                    Quantity = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    EntryOrderId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ExitOrderId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    StopLossPrice = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    TargetPrice = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    OpenedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RealizedPnl = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: true),
                    SignalId = table.Column<int>(type: "int", nullable: true),
                    TrailingStopPrice = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SpyPriceAtEntry = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: true),
                    SpyPriceAtExit = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: true),
                    VixAtEntry = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: true),
                    MarketRegimeAtEntry = table.Column<int>(type: "int", nullable: true),
                    SpyReturnDuringTrade = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Trades", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WatchlistHistory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Symbol = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CompanyName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Action = table.Column<int>(type: "int", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    WeekStarting = table.Column<DateOnly>(type: "date", nullable: false),
                    ConvictionAtAdd = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: true),
                    ReplacedSymbol = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WatchlistHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WatchlistItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Symbol = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CompanyName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Sector = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WatchlistItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkerHeartbeats",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    WorkerName = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    LastHeartbeatAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastRunResult = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    LastRunMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkerHeartbeats", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "StrategyWeights",
                columns: new[] { "Id", "ApplicableRegime", "BuyThreshold", "CreatedAt", "FundamentalMomentumWeight", "IsActive", "MacdWeight", "Notes", "PriceLevelWeight", "RelativeStrengthWeight", "RsiWeight", "SentimentWeight", "SetupQualityWeight", "Source", "StopLossPctDefault", "UpdatedAt", "VolumeWeight", "WatchThreshold" },
                values: new object[] { 1, null, 6.0m, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), 0.10m, true, 0.09m, "Default starting weights (sum to 1.0) ported from the v1 system.", 0.05m, 0.10m, 0.17m, 0.16m, 0.12m, "Default", 0.05m, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), 0.21m, 5.0m });

            migrationBuilder.InsertData(
                table: "SystemChecklists",
                columns: new[] { "Id", "CheckName", "CompletedAt", "CreatedAt", "Notes", "UpdatedAt" },
                values: new object[,]
                {
                    { 1, "AccountIdVerified", null, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 2, "LiveTradingConfirmed", null, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) }
                });

            migrationBuilder.InsertData(
                table: "WatchlistItems",
                columns: new[] { "Id", "CompanyName", "CreatedAt", "IsActive", "Notes", "Sector", "Symbol", "UpdatedAt" },
                values: new object[,]
                {
                    { 1, "Apple Inc.", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), true, null, "Technology", "AAPL", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 2, "Microsoft Corporation", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), true, null, "Technology", "MSFT", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 3, "NVIDIA Corporation", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), true, null, "Technology", "NVDA", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 4, "Advanced Micro Devices", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), true, null, "Technology", "AMD", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 5, "Alphabet Inc.", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), true, null, "Technology", "GOOGL", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 6, "Johnson & Johnson", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), true, null, "Healthcare", "JNJ", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 7, "UnitedHealth Group", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), true, null, "Healthcare", "UNH", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 8, "Pfizer Inc.", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), true, null, "Healthcare", "PFE", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 9, "AbbVie Inc.", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), true, null, "Healthcare", "ABBV", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 10, "Merck & Co.", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), true, null, "Healthcare", "MRK", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 11, "JPMorgan Chase & Co.", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), true, null, "Finance", "JPM", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 12, "Bank of America", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), true, null, "Finance", "BAC", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 13, "Goldman Sachs Group", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), true, null, "Finance", "GS", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 14, "Morgan Stanley", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), true, null, "Finance", "MS", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 15, "Visa Inc.", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), true, null, "Finance", "V", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 16, "Amazon.com Inc.", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), true, null, "Consumer", "AMZN", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 17, "Tesla Inc.", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), true, null, "Consumer", "TSLA", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 18, "Walmart Inc.", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), true, null, "Consumer", "WMT", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 19, "The Home Depot", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), true, null, "Consumer", "HD", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 20, "McDonald's Corporation", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), true, null, "Consumer", "MCD", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) }
                });

            migrationBuilder.CreateIndex(
                name: "IX_DailyReports_ReportDate",
                table: "DailyReports",
                column: "ReportDate",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PortfolioSnapshots_SnapshotDate",
                table: "PortfolioSnapshots",
                column: "SnapshotDate");

            migrationBuilder.CreateIndex(
                name: "IX_ReadinessSnapshots_SnapshotDate",
                table: "ReadinessSnapshots",
                column: "SnapshotDate",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RefinementSuggestions_GeneratedAt",
                table: "RefinementSuggestions",
                column: "GeneratedAt");

            migrationBuilder.CreateIndex(
                name: "IX_StockCandles_Symbol_Resolution_Timestamp",
                table: "StockCandles",
                columns: new[] { "Symbol", "Resolution", "Timestamp" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockSignals_Symbol_SignalDate",
                table: "StockSignals",
                columns: new[] { "Symbol", "SignalDate" });

            migrationBuilder.CreateIndex(
                name: "IX_SystemChecklists_CheckName",
                table: "SystemChecklists",
                column: "CheckName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TierEvaluationRecords_EvaluatedAt",
                table: "TierEvaluationRecords",
                column: "EvaluatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_TradeApprovals_ApprovalToken",
                table: "TradeApprovals",
                column: "ApprovalToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TradeApprovals_TradeDate",
                table: "TradeApprovals",
                column: "TradeDate",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Trades_Symbol_Status",
                table: "Trades",
                columns: new[] { "Symbol", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_WatchlistHistory_Symbol_WeekStarting",
                table: "WatchlistHistory",
                columns: new[] { "Symbol", "WeekStarting" });

            migrationBuilder.CreateIndex(
                name: "IX_WatchlistItems_Symbol",
                table: "WatchlistItems",
                column: "Symbol",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkerHeartbeats_WorkerName",
                table: "WorkerHeartbeats",
                column: "WorkerName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DailyReports");

            migrationBuilder.DropTable(
                name: "PortfolioSnapshots");

            migrationBuilder.DropTable(
                name: "ReadinessSnapshots");

            migrationBuilder.DropTable(
                name: "RefinementSuggestions");

            migrationBuilder.DropTable(
                name: "StockCandles");

            migrationBuilder.DropTable(
                name: "StockSignals");

            migrationBuilder.DropTable(
                name: "StrategyWeights");

            migrationBuilder.DropTable(
                name: "SystemChecklists");

            migrationBuilder.DropTable(
                name: "TierEvaluationRecords");

            migrationBuilder.DropTable(
                name: "TradeApprovals");

            migrationBuilder.DropTable(
                name: "Trades");

            migrationBuilder.DropTable(
                name: "WatchlistHistory");

            migrationBuilder.DropTable(
                name: "WatchlistItems");

            migrationBuilder.DropTable(
                name: "WorkerHeartbeats");
        }
    }
}
