using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkDayPlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "RaftId",
                table: "WorkDayPlans",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AlterColumn<Guid>(
                name: "JobTypeId",
                table: "WorkDayPlans",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<string>(
                name: "CancelReason",
                table: "WorkDayPlans",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CancelledAt",
                table: "WorkDayPlans",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Code",
                table: "WorkDayPlans",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CompletedAt",
                table: "WorkDayPlans",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExpectedEndAt",
                table: "WorkDayPlans",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "HarvestPlanLineId",
                table: "WorkDayPlans",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "WorkDayPlans",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "NurseryBatchId",
                table: "WorkDayPlans",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RequiredPeople",
                table: "WorkDayPlans",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StartsAt",
                table: "WorkDayPlans",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "WorkDayPlans",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "ZoneId",
                table: "WorkDayPlans",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "WorkDayPlanId",
                table: "WorkAssignments",
                type: "uniqueidentifier",
                nullable: true);

            // The plans made before this model keep their rows: each takes the zone of its raft and the kind of its job type (Thu hoạch, Ươm, Treo giống; any other job type is not
            // a work-day plan, so the plan is Cancelled). A plan of a day that has passed is Completed, one of today or later is Staffing with the people already assigned as the number needed
            // (at least 1). A nursery plan is in the zone alone. Codes follow the order of creation. The assignments stay as they are, outside any plan, until the people are confirmed on it.
            migrationBuilder.Sql(@"
UPDATE p SET
    p.ZoneId = r.ZoneId,
    p.Kind = CASE j.Name WHEN N'Thu hoạch' THEN N'Harvest' WHEN N'Ươm' THEN N'Nursery' WHEN N'Treo giống' THEN N'Seeding' ELSE N'Harvest' END
FROM WorkDayPlans p
JOIN Rafts r ON r.Id = p.RaftId
LEFT JOIN JobTypes j ON j.Id = p.JobTypeId;");

            migrationBuilder.Sql(@"
UPDATE p SET p.RequiredPeople = CASE WHEN c.N < 1 THEN 1 WHEN c.N > 500 THEN 500 ELSE c.N END
FROM WorkDayPlans p
CROSS APPLY (
    SELECT COUNT(*) AS N FROM WorkAssignments a
    WHERE a.[Date] = p.WorkDate AND a.RaftId = p.RaftId AND a.JobTypeId = p.JobTypeId AND a.StartsAt IS NULL) c;");

            migrationBuilder.Sql(@"
DECLARE @today date = CAST(SWITCHOFFSET(SYSDATETIMEOFFSET(), '+07:00') AS date);
UPDATE p SET
    p.Status = CASE
        WHEN j.Name IS NULL OR j.Name NOT IN (N'Thu hoạch', N'Ươm', N'Treo giống') THEN N'Cancelled'
        WHEN p.WorkDate < @today THEN N'Completed'
        ELSE N'Staffing' END,
    p.CancelledAt = CASE WHEN j.Name IS NULL OR j.Name NOT IN (N'Thu hoạch', N'Ươm', N'Treo giống') THEN SYSDATETIMEOFFSET() END,
    p.CancelReason = CASE WHEN j.Name IS NULL OR j.Name NOT IN (N'Thu hoạch', N'Ươm', N'Treo giống')
        THEN N'Chuyển dữ liệu: loại việc không thuộc thu hoạch, ươm giống hoặc thả giống.' END,
    p.CompletedAt = CASE WHEN j.Name IN (N'Thu hoạch', N'Ươm', N'Treo giống') AND p.WorkDate < @today
        THEN TODATETIMEOFFSET(CAST(p.WorkDate AS datetime2), '+07:00') END
FROM WorkDayPlans p
LEFT JOIN JobTypes j ON j.Id = p.JobTypeId;");

            migrationBuilder.Sql("UPDATE WorkDayPlans SET RaftId = NULL WHERE Kind = N'Nursery';");

            migrationBuilder.Sql(@"
;WITH n AS (SELECT Id, ROW_NUMBER() OVER (ORDER BY CreatedAt, Id) AS N FROM WorkDayPlans)
UPDATE p SET p.Code = N'KNL' + RIGHT(N'000000' + CAST(n.N AS nvarchar(20)), 6)
FROM WorkDayPlans p
JOIN n ON n.Id = p.Id;");

            migrationBuilder.Sql(@"
IF EXISTS (SELECT 1 FROM WorkDayPlans) AND NOT EXISTS (SELECT 1 FROM DocumentSequences WHERE [Key] = N'KNL')
    INSERT INTO DocumentSequences ([Key], LastValue) VALUES (N'KNL', (SELECT COUNT_BIG(*) FROM WorkDayPlans));");

            migrationBuilder.CreateTable(
                name: "PlanOutsiderHires",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlanId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    People = table.Column<int>(type: "int", nullable: false),
                    RatePerSession = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    PayeeName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CashVoucherId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PaidAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PaidByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlanOutsiderHires", x => x.Id);
                    table.CheckConstraint("CK_PlanOutsiderHire_Paid", "([CashVoucherId] IS NULL AND [PaidAt] IS NULL) OR ([CashVoucherId] IS NOT NULL AND [PaidAt] IS NOT NULL)");
                    table.CheckConstraint("CK_PlanOutsiderHire_People", "[People] BETWEEN 1 AND 500");
                    table.CheckConstraint("CK_PlanOutsiderHire_Rate", "[RatePerSession] >= 0");
                    table.ForeignKey(
                        name: "FK_PlanOutsiderHires_Accounts_PaidByAccountId",
                        column: x => x.PaidByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PlanOutsiderHires_CashVouchers_CashVoucherId",
                        column: x => x.CashVoucherId,
                        principalTable: "CashVouchers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PlanOutsiderHires_WorkDayPlans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "WorkDayPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkDayPlanMembers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlanId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    State = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    InvitedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Deadline = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RespondedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    AddedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ConfirmedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WorkAssignmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkDayPlanMembers", x => x.Id);
                    table.CheckConstraint("CK_WorkDayPlanMember_OnBehalf", "[State] <> 'ConfirmedOnBehalf' OR ([Reason] IS NOT NULL AND [ConfirmedByAccountId] IS NOT NULL)");
                    table.CheckConstraint("CK_WorkDayPlanMember_State", "[State] IN ('Invited', 'Accepted', 'ConfirmedOnBehalf', 'Declined', 'Expired', 'Unsuccessful')");
                    table.ForeignKey(
                        name: "FK_WorkDayPlanMembers_Accounts_AddedByAccountId",
                        column: x => x.AddedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkDayPlanMembers_Accounts_ConfirmedByAccountId",
                        column: x => x.ConfirmedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkDayPlanMembers_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkDayPlanMembers_WorkDayPlans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "WorkDayPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "CashVoucherTypes",
                columns: new[] { "Id", "Category", "CountsInResult", "CreatedAt", "CreatedBy", "Description", "Direction", "IsActive", "IsSystem", "Manual", "Name", "UpdatedAt", "UpdatedBy" },
                values: new object[] { new Guid("5d6c1a10-0000-4c3a-9a10-000000000009"), "Payroll", true, new DateTimeOffset(new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 7, 0, 0, 0)), null, null, "Payment", true, true, false, "Chi nhân công thuê ngoài", null, null });

            migrationBuilder.CreateIndex(
                name: "IX_WorkDayPlans_Code",
                table: "WorkDayPlans",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkDayPlans_HarvestPlanLineId",
                table: "WorkDayPlans",
                column: "HarvestPlanLineId",
                unique: true,
                filter: "[HarvestPlanLineId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WorkDayPlans_NurseryBatchId",
                table: "WorkDayPlans",
                column: "NurseryBatchId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkDayPlans_WorkDate_Status",
                table: "WorkDayPlans",
                columns: new[] { "WorkDate", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkDayPlans_ZoneId_WorkDate",
                table: "WorkDayPlans",
                columns: new[] { "ZoneId", "WorkDate" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_WorkDayPlan_HarvestLine",
                table: "WorkDayPlans",
                sql: "[HarvestPlanLineId] IS NULL OR [Kind] = 'Harvest'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_WorkDayPlan_Kind",
                table: "WorkDayPlans",
                sql: "[Kind] IN ('Harvest', 'Nursery', 'Seeding')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_WorkDayPlan_Place",
                table: "WorkDayPlans",
                sql: "([Kind] = 'Nursery' AND [RaftId] IS NULL) OR ([Kind] <> 'Nursery' AND [RaftId] IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_WorkDayPlan_RequiredPeople",
                table: "WorkDayPlans",
                sql: "[RequiredPeople] BETWEEN 1 AND 500");

            migrationBuilder.AddCheckConstraint(
                name: "CK_WorkDayPlan_Status",
                table: "WorkDayPlans",
                sql: "[Status] IN ('Staffing', 'Ready', 'InProgress', 'Completed', 'Cancelled')");

            migrationBuilder.CreateIndex(
                name: "IX_WorkAssignments_WorkDayPlanId",
                table: "WorkAssignments",
                column: "WorkDayPlanId");

            migrationBuilder.CreateIndex(
                name: "IX_PlanOutsiderHires_CashVoucherId",
                table: "PlanOutsiderHires",
                column: "CashVoucherId");

            migrationBuilder.CreateIndex(
                name: "IX_PlanOutsiderHires_PaidByAccountId",
                table: "PlanOutsiderHires",
                column: "PaidByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_PlanOutsiderHires_PlanId",
                table: "PlanOutsiderHires",
                column: "PlanId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkDayPlanMembers_AddedByAccountId",
                table: "WorkDayPlanMembers",
                column: "AddedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkDayPlanMembers_ConfirmedByAccountId",
                table: "WorkDayPlanMembers",
                column: "ConfirmedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkDayPlanMembers_EmployeeId_State",
                table: "WorkDayPlanMembers",
                columns: new[] { "EmployeeId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkDayPlanMembers_PlanId_EmployeeId",
                table: "WorkDayPlanMembers",
                columns: new[] { "PlanId", "EmployeeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkDayPlanMembers_State_Deadline",
                table: "WorkDayPlanMembers",
                columns: new[] { "State", "Deadline" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkDayPlanMembers_WorkAssignmentId",
                table: "WorkDayPlanMembers",
                column: "WorkAssignmentId");

            migrationBuilder.AddForeignKey(
                name: "FK_WorkAssignments_WorkDayPlans_WorkDayPlanId",
                table: "WorkAssignments",
                column: "WorkDayPlanId",
                principalTable: "WorkDayPlans",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_WorkDayPlans_FarmingZones_ZoneId",
                table: "WorkDayPlans",
                column: "ZoneId",
                principalTable: "FarmingZones",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_WorkDayPlans_HarvestPlanLines_HarvestPlanLineId",
                table: "WorkDayPlans",
                column: "HarvestPlanLineId",
                principalTable: "HarvestPlanLines",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_WorkDayPlans_NurseryBatches_NurseryBatchId",
                table: "WorkDayPlans",
                column: "NurseryBatchId",
                principalTable: "NurseryBatches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM DocumentSequences WHERE [Key] = N'KNL';");

            migrationBuilder.DropForeignKey(
                name: "FK_WorkAssignments_WorkDayPlans_WorkDayPlanId",
                table: "WorkAssignments");

            migrationBuilder.DropForeignKey(
                name: "FK_WorkDayPlans_FarmingZones_ZoneId",
                table: "WorkDayPlans");

            migrationBuilder.DropForeignKey(
                name: "FK_WorkDayPlans_HarvestPlanLines_HarvestPlanLineId",
                table: "WorkDayPlans");

            migrationBuilder.DropForeignKey(
                name: "FK_WorkDayPlans_NurseryBatches_NurseryBatchId",
                table: "WorkDayPlans");

            migrationBuilder.DropTable(
                name: "PlanOutsiderHires");

            migrationBuilder.DropTable(
                name: "WorkDayPlanMembers");

            migrationBuilder.DropIndex(
                name: "IX_WorkDayPlans_Code",
                table: "WorkDayPlans");

            migrationBuilder.DropIndex(
                name: "IX_WorkDayPlans_HarvestPlanLineId",
                table: "WorkDayPlans");

            migrationBuilder.DropIndex(
                name: "IX_WorkDayPlans_NurseryBatchId",
                table: "WorkDayPlans");

            migrationBuilder.DropIndex(
                name: "IX_WorkDayPlans_WorkDate_Status",
                table: "WorkDayPlans");

            migrationBuilder.DropIndex(
                name: "IX_WorkDayPlans_ZoneId_WorkDate",
                table: "WorkDayPlans");

            migrationBuilder.DropCheckConstraint(
                name: "CK_WorkDayPlan_HarvestLine",
                table: "WorkDayPlans");

            migrationBuilder.DropCheckConstraint(
                name: "CK_WorkDayPlan_Kind",
                table: "WorkDayPlans");

            migrationBuilder.DropCheckConstraint(
                name: "CK_WorkDayPlan_Place",
                table: "WorkDayPlans");

            migrationBuilder.DropCheckConstraint(
                name: "CK_WorkDayPlan_RequiredPeople",
                table: "WorkDayPlans");

            migrationBuilder.DropCheckConstraint(
                name: "CK_WorkDayPlan_Status",
                table: "WorkDayPlans");

            migrationBuilder.DropIndex(
                name: "IX_WorkAssignments_WorkDayPlanId",
                table: "WorkAssignments");

            migrationBuilder.DeleteData(
                table: "CashVoucherTypes",
                keyColumn: "Id",
                keyValue: new Guid("5d6c1a10-0000-4c3a-9a10-000000000009"));

            migrationBuilder.DropColumn(
                name: "CancelReason",
                table: "WorkDayPlans");

            migrationBuilder.DropColumn(
                name: "CancelledAt",
                table: "WorkDayPlans");

            migrationBuilder.DropColumn(
                name: "Code",
                table: "WorkDayPlans");

            migrationBuilder.DropColumn(
                name: "CompletedAt",
                table: "WorkDayPlans");

            migrationBuilder.DropColumn(
                name: "ExpectedEndAt",
                table: "WorkDayPlans");

            migrationBuilder.DropColumn(
                name: "HarvestPlanLineId",
                table: "WorkDayPlans");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "WorkDayPlans");

            migrationBuilder.DropColumn(
                name: "NurseryBatchId",
                table: "WorkDayPlans");

            migrationBuilder.DropColumn(
                name: "RequiredPeople",
                table: "WorkDayPlans");

            migrationBuilder.DropColumn(
                name: "StartsAt",
                table: "WorkDayPlans");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "WorkDayPlans");

            migrationBuilder.DropColumn(
                name: "ZoneId",
                table: "WorkDayPlans");

            migrationBuilder.DropColumn(
                name: "WorkDayPlanId",
                table: "WorkAssignments");

            migrationBuilder.AlterColumn<Guid>(
                name: "RaftId",
                table: "WorkDayPlans",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "JobTypeId",
                table: "WorkDayPlans",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);
        }
    }
}
