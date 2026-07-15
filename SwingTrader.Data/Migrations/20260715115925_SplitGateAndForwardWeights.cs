using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwingTrader.Data.Migrations
{
    /// <inheritdoc />
    public partial class SplitGateAndForwardWeights : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "SentimentWeight",
                table: "StrategyWeights",
                newName: "ForwardSentimentWeight");

            migrationBuilder.RenameColumn(
                name: "FundamentalMomentumWeight",
                table: "StrategyWeights",
                newName: "ForwardFundamentalWeight");

            // The renamed forward weights carry the old sentiment/fundamental
            // values (which summed to <1). Renormalise them to sum 1.0,
            // preserving their relative proportion; fall back to 60/40 if both
            // were zero.
            migrationBuilder.Sql(@"
                UPDATE StrategyWeights
                SET ForwardSentimentWeight = CASE WHEN (ForwardSentimentWeight + ForwardFundamentalWeight) > 0
                        THEN ForwardSentimentWeight / (ForwardSentimentWeight + ForwardFundamentalWeight) ELSE 0.60 END,
                    ForwardFundamentalWeight = CASE WHEN (ForwardSentimentWeight + ForwardFundamentalWeight) > 0
                        THEN ForwardFundamentalWeight / (ForwardSentimentWeight + ForwardFundamentalWeight) ELSE 0.40 END;");

            // The six gate weights lost the ~0.26 that used to sit on the two
            // forward components; renormalise them back to sum 1.0 (redistributed
            // in proportion), so the gate score keeps its 0-10 range.
            migrationBuilder.Sql(@"
                UPDATE sw
                SET RsiWeight = sw.RsiWeight / d.g, MacdWeight = sw.MacdWeight / d.g,
                    VolumeWeight = sw.VolumeWeight / d.g, SetupQualityWeight = sw.SetupQualityWeight / d.g,
                    RelativeStrengthWeight = sw.RelativeStrengthWeight / d.g, PriceLevelWeight = sw.PriceLevelWeight / d.g
                FROM StrategyWeights sw
                CROSS APPLY (VALUES (sw.RsiWeight + sw.MacdWeight + sw.VolumeWeight + sw.SetupQualityWeight
                    + sw.RelativeStrengthWeight + sw.PriceLevelWeight)) AS d(g)
                WHERE d.g > 0;");

            migrationBuilder.UpdateData(
                table: "StrategyWeights",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "ForwardFundamentalWeight", "ForwardSentimentWeight", "MacdWeight", "Notes", "PriceLevelWeight", "RelativeStrengthWeight", "RsiWeight", "SetupQualityWeight", "VolumeWeight" },
                values: new object[] { 0.40m, 0.60m, 0.12m, "Default gate weights (6, sum to 1.0) + forward blend 60/40.", 0.07m, 0.14m, 0.23m, 0.16m, 0.28m });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "ForwardSentimentWeight",
                table: "StrategyWeights",
                newName: "SentimentWeight");

            migrationBuilder.RenameColumn(
                name: "ForwardFundamentalWeight",
                table: "StrategyWeights",
                newName: "FundamentalMomentumWeight");

            migrationBuilder.UpdateData(
                table: "StrategyWeights",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "FundamentalMomentumWeight", "MacdWeight", "Notes", "PriceLevelWeight", "RelativeStrengthWeight", "RsiWeight", "SentimentWeight", "SetupQualityWeight", "VolumeWeight" },
                values: new object[] { 0.10m, 0.09m, "Default starting weights (sum to 1.0) ported from the v1 system.", 0.05m, 0.10m, 0.17m, 0.16m, 0.12m, 0.21m });
        }
    }
}
