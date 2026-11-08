using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkDayPlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "WorkDayPlanId",
                table: "WorkAssignments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WorkDayPlan",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatorAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResponsibleAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RaftId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JobTypeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkDate = table.Column<DateOnly>(type: "date", nullable: false),
                    PurchaseRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ClientOperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Approved = table.Column<bool>(type: "bit", nullable: true),
                    RespondedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ResponseReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkDayPlan", x => x.Id);
                    table.CheckConstraint("CK_WorkDayPlan_Response", "([Approved] IS NULL AND [RespondedAt] IS NULL) OR ([Approved] IS NOT NULL AND [RespondedAt] IS NOT NULL AND ([Approved] = 1 OR ([ResponseReason] IS NOT NULL AND LEN(LTRIM(RTRIM([ResponseReason]))) > 0)))");
                    table.ForeignKey(
                        name: "FK_WorkDayPlan_Accounts_CreatorAccountId",
                        column: x => x.CreatorAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkDayPlan_Accounts_ResponsibleAccountId",
                        column: x => x.ResponsibleAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkDayPlan_JobType_JobTypeId",
                        column: x => x.JobTypeId,
                        principalTable: "JobType",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkDayPlan_PurchaseRequest_PurchaseRequestId",
                        column: x => x.PurchaseRequestId,
                        principalTable: "PurchaseRequest",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkDayPlan_Rafts_RaftId",
                        column: x => x.RaftId,
                        principalTable: "Rafts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkAssignments_WorkDayPlanId",
                table: "WorkAssignments",
                column: "WorkDayPlanId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkDayPlan_CreatorAccountId_ClientOperationId",
                table: "WorkDayPlan",
                columns: new[] { "CreatorAccountId", "ClientOperationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkDayPlan_JobTypeId",
                table: "WorkDayPlan",
                column: "JobTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkDayPlan_PurchaseRequestId",
                table: "WorkDayPlan",
                column: "PurchaseRequestId",
                unique: true,
                filter: "[PurchaseRequestId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WorkDayPlan_RaftId",
                table: "WorkDayPlan",
                column: "RaftId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkDayPlan_ResponsibleAccountId_WorkDate",
                table: "WorkDayPlan",
                columns: new[] { "ResponsibleAccountId", "WorkDate" });

            migrationBuilder.AddForeignKey(
                name: "FK_WorkAssignments_WorkDayPlan_WorkDayPlanId",
                table: "WorkAssignments",
                column: "WorkDayPlanId",
                principalTable: "WorkDayPlan",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WorkAssignments_WorkDayPlan_WorkDayPlanId",
                table: "WorkAssignments");

            migrationBuilder.DropTable(
                name: "WorkDayPlan");

            migrationBuilder.DropIndex(
                name: "IX_WorkAssignments_WorkDayPlanId",
                table: "WorkAssignments");

            migrationBuilder.DropColumn(
                name: "WorkDayPlanId",
                table: "WorkAssignments");
        }
    }
}
