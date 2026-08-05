using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwingTrader.Data.Migrations
{
    /// <inheritdoc />
    public partial class SleeveAllocationPrecision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AccountAllocations_AccountId",
                table: "AccountAllocations");

            migrationBuilder.AlterColumn<decimal>(
                name: "SwingPct",
                table: "AccountAllocations",
                type: "decimal(6,4)",
                precision: 6,
                scale: 4,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,2)");

            migrationBuilder.AlterColumn<decimal>(
                name: "SpyCorePct",
                table: "AccountAllocations",
                type: "decimal(6,4)",
                precision: 6,
                scale: 4,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,2)");

            migrationBuilder.AlterColumn<decimal>(
                name: "FactorTiltPct",
                table: "AccountAllocations",
                type: "decimal(6,4)",
                precision: 6,
                scale: 4,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,2)");

            migrationBuilder.AlterColumn<string>(
                name: "CoreTicker",
                table: "AccountAllocations",
                type: "nvarchar(12)",
                maxLength: 12,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.CreateIndex(
                name: "IX_AccountAllocations_AccountId",
                table: "AccountAllocations",
                column: "AccountId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AccountAllocations_AccountId",
                table: "AccountAllocations");

            migrationBuilder.AlterColumn<decimal>(
                name: "SwingPct",
                table: "AccountAllocations",
                type: "decimal(18,2)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(6,4)",
                oldPrecision: 6,
                oldScale: 4);

            migrationBuilder.AlterColumn<decimal>(
                name: "SpyCorePct",
                table: "AccountAllocations",
                type: "decimal(18,2)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(6,4)",
                oldPrecision: 6,
                oldScale: 4);

            migrationBuilder.AlterColumn<decimal>(
                name: "FactorTiltPct",
                table: "AccountAllocations",
                type: "decimal(18,2)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(6,4)",
                oldPrecision: 6,
                oldScale: 4);

            migrationBuilder.AlterColumn<string>(
                name: "CoreTicker",
                table: "AccountAllocations",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(12)",
                oldMaxLength: 12);

            migrationBuilder.CreateIndex(
                name: "IX_AccountAllocations_AccountId",
                table: "AccountAllocations",
                column: "AccountId");
        }
    }
}
