using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwingTrader.Data.Migrations
{
    /// <inheritdoc />
    public partial class BackfillForwardVetoFloorDefault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // AddForwardVetoFloor shipped with SQL default 0 (veto off) instead
            // of the model default 2.5, and the Settings PUT wasn't persisting
            // the field yet, so no row's 0 can be a deliberate user choice -
            // safe to move every existing row onto the intended default.
            migrationBuilder.Sql("UPDATE AccountRiskProfiles SET ForwardVetoFloor = 2.5 WHERE ForwardVetoFloor = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
