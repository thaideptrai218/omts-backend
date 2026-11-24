using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HarvestPlanReceiveHandOverAndPurchaseStaffingWithoutWaiting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "Statuses",
                columns: new[] { "Id", "Code", "DisplayColor", "DisplayName", "Group", "IsFinal", "IsInitial", "Meaning", "SortOrder" },
                values: new object[,]
                {
                    { 4206, "RECEIVED", null, "Đã nhận", "HARVEST_PLAN", false, false, "A Zone Manager of the plan's zone pressed Receive (Nhận); lots can be recorded and lanes left out from now on.", 6 },
                    { 4207, "HANDED_OVER", null, "Đã bàn giao", "HARVEST_PLAN", false, false, "A Zone Manager pressed Hand over (Bàn giao): every line has its lot or all its lanes left out; waits for the Owner to complete the plan.", 7 }
                });

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30113);

            migrationBuilder.UpdateData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 180,
                column: "GuardCondition",
                value: "A staffing plan exists (employees assigned at once, no agreement to wait for); the accepted cages are above 0, not more than the requested cages and equal to the planned cages; the pickup date is not before today and equals the planned pickup.");

            migrationBuilder.UpdateData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30082,
                column: "GuardCondition",
                value: "Legacy, no longer made: since 24/11/2026 the Owner picks the employees, their shifts become work assignments at once and the request stays Awaiting response.");

            migrationBuilder.UpdateData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30085,
                column: "GuardCondition",
                value: "A staffing plan exists (employees chosen, assignments made); cages and pickup match it. No employee agreement is counted any more.");

            migrationBuilder.UpdateData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30111,
                column: "FromStatusId",
                value: 4206);

            migrationBuilder.UpdateData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30112,
                columns: new[] { "AllowedPerformers", "FromStatusId", "GuardCondition" },
                values: new object[] { "Cooperative Owner", 4207, "The plan was handed over by a Zone Manager; only the Owner completes it. The system never completes a plan by itself." });

            migrationBuilder.UpdateData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30116,
                column: "GuardCondition",
                value: "The Owner completed the plan and a lot is removed or a lane left out is put back: a line is open again.");

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3205,
                column: "Meaning",
                value: "Legacy (31/10/2026 flow): requests sent before 24/11/2026 may still wait here for employees to answer; new staffing plans keep the request Awaiting response and make the work assignments at once.");

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4202,
                column: "Meaning",
                value: "Handed to the Zone Managers of its zone; none of them has pressed Receive yet, so no lot is recorded and no lane left out.");

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4203,
                column: "Meaning",
                value: "A lot is recorded from a line of the plan, or a lane is left out; it stays here until a Zone Manager hands the plan over.");

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4204,
                column: "Meaning",
                value: "The Owner closed a plan the Zone Manager handed over; removing a lot or putting a lane back opens it again.");

            migrationBuilder.InsertData(
                table: "StatusTransitions",
                columns: new[] { "Id", "AllowedPerformers", "FromStatusId", "GuardCondition", "ReasonRequired", "ToStatusId", "TriggerEvent", "UseCases" },
                values: new object[,]
                {
                    { 30120, "Cooperative Owner; Zone Manager", 4202, "A Zone Manager of a zone of the plan presses Receive; there is no refusal.", false, 4206, "Action", "HARVEST-PLAN" },
                    { 30121, "Cooperative Owner; Zone Manager", 4203, "Every line of the plan has its lot or has every lane left out; a Zone Manager presses Hand over.", false, 4207, "Action", "HARVEST-PLAN" },
                    { 30122, "System", 4207, "A lot of a handed over plan is removed, or a lane left out is put back: a line is open again.", false, 4203, "Action", "HARVEST-PLAN" },
                    { 30123, "Cooperative Owner", 4206, "A reason is given and no lot was recorded from the plan.", true, 4205, "Action", "HARVEST-PLAN" }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30120);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30121);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30122);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30123);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4206);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4207);

            migrationBuilder.UpdateData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 180,
                column: "GuardCondition",
                value: "The accepted cages are above 0 and not more than the requested cages; the pickup date is not before today.");

            migrationBuilder.UpdateData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30082,
                column: "GuardCondition",
                value: "The Owner sends dated shift invitations with a cage capacity.");

            migrationBuilder.UpdateData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30085,
                column: "GuardCondition",
                value: "Enough distinct employees have approved the dated shift; cages and pickup match the staffing plan.");

            migrationBuilder.UpdateData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30111,
                column: "FromStatusId",
                value: 4202);

            migrationBuilder.UpdateData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30112,
                columns: new[] { "AllowedPerformers", "FromStatusId", "GuardCondition" },
                values: new object[] { "Cooperative Owner; System", 4203, "Every line has its lot or every lane left out, or the Owner finishes the plan: lanes still to do stay as they are." });

            migrationBuilder.UpdateData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30116,
                column: "GuardCondition",
                value: "A lot of a plan that finished by itself is removed, or a lane left out is put back: a line is open again.");

            migrationBuilder.InsertData(
                table: "StatusTransitions",
                columns: new[] { "Id", "AllowedPerformers", "FromStatusId", "GuardCondition", "ReasonRequired", "ToStatusId", "TriggerEvent", "UseCases" },
                values: new object[] { 30113, "System", 4202, "Every lane of every line is left out and no lot was recorded.", false, 4204, "Action", "HARVEST-PLAN" });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3205,
                column: "Meaning",
                value: "User decision 31/10/2026: employees approve their shift before the Owner accepts the request.");

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4202,
                column: "Meaning",
                value: "Handed to the Zone Managers of its zone; no lot is recorded and no lane left out yet.");

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4203,
                column: "Meaning",
                value: "A lot is recorded from a line of the plan, or a lane is left out.");

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4204,
                column: "Meaning",
                value: "Every line has its lot or has every lane left out, or the Owner finished the plan by hand; removing a lot of a plan finished by itself opens it again.");
        }
    }
}
