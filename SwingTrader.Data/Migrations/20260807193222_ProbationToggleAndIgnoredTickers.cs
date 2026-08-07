using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwingTrader.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProbationToggleAndIgnoredTickers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IgnoredTickersCsv",
                table: "Accounts",
                type: "nvarchar(max)",
                nullable: true);

            // TRUE, not the scaffolder's false: adding a flag must not change
            // behaviour. Every existing book keeps probation exactly as it runs
            // today, and turning it off is a deliberate act.
            migrationBuilder.AddColumn<bool>(
                name: "ProbationEnabled",
                table: "AccountRiskProfiles",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.UpdateData(
                table: "Accounts",
                keyColumn: "Id",
                keyValue: 1,
                column: "IgnoredTickersCsv",
                value: null);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IgnoredTickersCsv",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "ProbationEnabled",
                table: "AccountRiskProfiles");
        }
    }
}
