using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwingTrader.Data.Migrations
{
    /// <inheritdoc />
    public partial class FixSetupTacticsUnknownSeed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // AddSetupTactics seeded SetupType VALUES (0),(1),(2),(3),(4) on the
            // mistaken assumption that OversoldRecovery=0. The enum actually
            // starts Unknown=0, so it created a spurious Unknown (0) row - which
            // never trades and rendered as a blank-labelled row in the Setups
            // editor - and skipped TrendFollowing (5) entirely. GetAllAsync's
            // "reseed if fewer than 5 rows" self-heal never fired because the
            // count was already 5. Fix both here.

            // 1. Drop the Unknown rows - Unknown never produces a Buy signal, so
            //    it should never have a tactics row.
            migrationBuilder.Sql("DELETE FROM SetupTactics WHERE SetupType = 0;");

            // 2. Add the missing TrendFollowing (5) row for any account that
            //    lacks it, copied from that account's Neutral risk book (Regime=1)
            //    exactly as the original seed did for the others.
            migrationBuilder.Sql(@"
                INSERT INTO SetupTactics
                    (AccountId, SetupType, StopLossPct, TargetPct, GuideHoldDays,
                     TrailingActivationPct, TrailingDistancePct, CreatedAt, UpdatedAt)
                SELECT p.AccountId, 5, p.StopLossPct, p.TargetPct, p.MaxHoldDays,
                       p.TrailingActivationPct, p.TrailingDistancePct, SYSUTCDATETIME(), SYSUTCDATETIME()
                FROM AccountRiskProfiles p
                WHERE p.Regime = 1
                  AND NOT EXISTS (
                      SELECT 1 FROM SetupTactics s
                      WHERE s.AccountId = p.AccountId AND s.SetupType = 5);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Irreversible data correction - the prior (buggy) seed state isn't
            // worth reconstructing.
        }
    }
}
