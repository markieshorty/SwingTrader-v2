using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwingTrader.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddForwardFilingWeightAndDistressFlags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "ForwardFilingWeight",
                table: "StrategyWeights",
                type: "decimal(18,8)",
                precision: 18,
                scale: 8,
                nullable: false,
                defaultValue: 0m);

            // Turn the filing component ON for every existing row (FD2 ships
            // live, per Mark 17 Jul 2026): give filing its 0.25 share and scale
            // sentiment/fundamental by 0.75 so their relative proportions are
            // preserved and the forward blend still sums to 1.0.
            migrationBuilder.Sql(@"
                UPDATE StrategyWeights SET
                    ForwardSentimentWeight = ForwardSentimentWeight * 0.75,
                    ForwardFundamentalWeight = ForwardFundamentalWeight * 0.75,
                    ForwardFilingWeight = 0.25;");

            migrationBuilder.CreateTable(
                name: "DistressFlags",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Symbol = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    AccessionNumber = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    FiledAt = table.Column<DateOnly>(type: "date", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DistressFlags", x => x.Id);
                });

            migrationBuilder.UpdateData(
                table: "StrategyWeights",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "ForwardFilingWeight", "ForwardFundamentalWeight", "ForwardSentimentWeight", "Notes" },
                values: new object[] { 0.25m, 0.30m, 0.45m, "Default gate weights (6, sum to 1.0) + forward blend 45/30/25." });

            migrationBuilder.CreateIndex(
                name: "IX_DistressFlags_AccessionNumber_Reason",
                table: "DistressFlags",
                columns: new[] { "AccessionNumber", "Reason" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DistressFlags_Symbol_FiledAt",
                table: "DistressFlags",
                columns: new[] { "Symbol", "FiledAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DistressFlags");

            migrationBuilder.DropColumn(
                name: "ForwardFilingWeight",
                table: "StrategyWeights");

            migrationBuilder.UpdateData(
                table: "StrategyWeights",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "ForwardFundamentalWeight", "ForwardSentimentWeight", "Notes" },
                values: new object[] { 0.40m, 0.60m, "Default gate weights (6, sum to 1.0) + forward blend 60/40." });
        }
    }
}
