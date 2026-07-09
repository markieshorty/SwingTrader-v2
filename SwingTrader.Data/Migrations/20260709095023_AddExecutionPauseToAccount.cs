using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwingTrader.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutionPauseToAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ExecutionPausedDemo",
                table: "Accounts",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ExecutionPausedLive",
                table: "Accounts",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.UpdateData(
                table: "Accounts",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "ExecutionPausedDemo", "ExecutionPausedLive" },
                values: new object[] { false, false });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExecutionPausedDemo",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "ExecutionPausedLive",
                table: "Accounts");
        }
    }
}
