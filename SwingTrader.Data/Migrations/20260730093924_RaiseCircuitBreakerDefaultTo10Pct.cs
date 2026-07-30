using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwingTrader.Data.Migrations
{
    /// <inheritdoc />
    public partial class RaiseCircuitBreakerDefaultTo10Pct : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Default raised 5% -> 10% (diagnose-issues phase: keep trades
            // flowing). Only books still sitting on the OLD default move -
            // any book an owner has already customised keeps its value.
            migrationBuilder.Sql(
                "UPDATE AccountRiskProfiles SET DailyLossCircuitBreakerPct = 0.10 WHERE DailyLossCircuitBreakerPct = 0.05;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE AccountRiskProfiles SET DailyLossCircuitBreakerPct = 0.05 WHERE DailyLossCircuitBreakerPct = 0.10;");
        }
    }
}
