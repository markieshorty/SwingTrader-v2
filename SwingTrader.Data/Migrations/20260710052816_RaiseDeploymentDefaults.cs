using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwingTrader.Data.Migrations
{
    /// <inheritdoc />
    public partial class RaiseDeploymentDefaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Deployment defaults raised per the backtest deployment sweep (see
            // CapitalRules): locked capital 0.70 -> 0.60, max position 0.20 ->
            // 0.40 of active. Only rows still sitting on BOTH old defaults are
            // migrated - an account that deliberately customised either slider
            // keeps its chosen values.
            migrationBuilder.Sql("""
                UPDATE AccountRiskProfiles
                SET MaxPositionPctOfActive = 0.40, LockedCapitalPct = 0.60
                WHERE MaxPositionPctOfActive = 0.20 AND LockedCapitalPct = 0.70;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE AccountRiskProfiles
                SET MaxPositionPctOfActive = 0.20, LockedCapitalPct = 0.70
                WHERE MaxPositionPctOfActive = 0.40 AND LockedCapitalPct = 0.60;
                """);
        }
    }
}
