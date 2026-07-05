using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwingTrader.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAppUserIsApproved : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing rows default to true (already-legitimate, pre-dates
            // this feature) - only newly-created AppUsers get gated, and
            // the registration middleware always sets this explicitly
            // (true for Owner, false for an invited Member) rather than
            // relying on this column default.
            migrationBuilder.AddColumn<bool>(
                name: "IsApproved",
                table: "AppUsers",
                type: "bit",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsApproved",
                table: "AppUsers");
        }
    }
}
