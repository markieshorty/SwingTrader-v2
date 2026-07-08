using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwingTrader.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTargetWatchlistSizeToRiskProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TargetWatchlistSize",
                table: "AccountRiskProfiles",
                type: "int",
                nullable: false,
                defaultValue: 25);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TargetWatchlistSize",
                table: "AccountRiskProfiles");
        }
    }
}
