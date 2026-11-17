using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LeaveRequestAwaitingFirstDayUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_EmployeeRequests_Awaiting_MissedTime",
                table: "EmployeeRequests");

            migrationBuilder.CreateIndex(
                name: "UX_EmployeeRequests_Awaiting_FirstDay",
                table: "EmployeeRequests",
                columns: new[] { "EmployeeId", "Type", "FromDate" },
                unique: true,
                filter: "[StatusId] = 3801");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_EmployeeRequests_Awaiting_FirstDay",
                table: "EmployeeRequests");

            migrationBuilder.CreateIndex(
                name: "UX_EmployeeRequests_Awaiting_MissedTime",
                table: "EmployeeRequests",
                columns: new[] { "EmployeeId", "Type", "FromDate" },
                unique: true,
                filter: "[StatusId] = 3801 AND [Type] <> N'Leave'");
        }
    }
}
