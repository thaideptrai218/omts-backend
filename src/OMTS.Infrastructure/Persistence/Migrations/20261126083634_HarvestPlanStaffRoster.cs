using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HarvestPlanStaffRoster : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RequiredPeople",
                table: "HarvestPlans",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "HarvestPlanStaffMembers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlanId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    State = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    InvitedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RespondedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    AddedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ConfirmedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HarvestPlanStaffMembers", x => x.Id);
                    table.CheckConstraint("CK_HarvestPlanStaffMember_ManualReason", "[State] <> 'ConfirmedManually' OR ([Reason] IS NOT NULL AND [ConfirmedByAccountId] IS NOT NULL)");
                    table.CheckConstraint("CK_HarvestPlanStaffMember_State", "[State] IN ('Invited', 'Confirmed', 'ConfirmedManually', 'Declined', 'External')");
                    table.ForeignKey(
                        name: "FK_HarvestPlanStaffMembers_Accounts_AddedByAccountId",
                        column: x => x.AddedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HarvestPlanStaffMembers_Accounts_ConfirmedByAccountId",
                        column: x => x.ConfirmedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HarvestPlanStaffMembers_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HarvestPlanStaffMembers_HarvestPlans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "HarvestPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_HarvestPlan_RequiredPeople",
                table: "HarvestPlans",
                sql: "[RequiredPeople] IS NULL OR [RequiredPeople] BETWEEN 1 AND 500");

            migrationBuilder.CreateIndex(
                name: "IX_HarvestPlanStaffMembers_AddedByAccountId",
                table: "HarvestPlanStaffMembers",
                column: "AddedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_HarvestPlanStaffMembers_ConfirmedByAccountId",
                table: "HarvestPlanStaffMembers",
                column: "ConfirmedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_HarvestPlanStaffMembers_EmployeeId_State",
                table: "HarvestPlanStaffMembers",
                columns: new[] { "EmployeeId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_HarvestPlanStaffMembers_PlanId_EmployeeId",
                table: "HarvestPlanStaffMembers",
                columns: new[] { "PlanId", "EmployeeId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HarvestPlanStaffMembers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_HarvestPlan_RequiredPeople",
                table: "HarvestPlans");

            migrationBuilder.DropColumn(
                name: "RequiredPeople",
                table: "HarvestPlans");
        }
    }
}
