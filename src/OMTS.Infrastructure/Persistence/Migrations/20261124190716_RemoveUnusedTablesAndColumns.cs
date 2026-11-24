using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveUnusedTablesAndColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_HarvestLots_PurchaseRequests_PurchaseRequestId",
                table: "HarvestLots");

            migrationBuilder.DropForeignKey(
                name: "FK_Payrolls_Accounts_SentBackByAccountId",
                table: "Payrolls");

            migrationBuilder.DropForeignKey(
                name: "FK_Payrolls_Accounts_SentByAccountId",
                table: "Payrolls");

            migrationBuilder.DropTable(
                name: "NurseryBatchAllocations");

            migrationBuilder.DropTable(
                name: "StatusTransitions");

            migrationBuilder.DropIndex(
                name: "IX_Payrolls_SentBackByAccountId",
                table: "Payrolls");

            migrationBuilder.DropIndex(
                name: "IX_Payrolls_SentByAccountId",
                table: "Payrolls");

            migrationBuilder.DropIndex(
                name: "IX_HarvestLots_PurchaseRequestId",
                table: "HarvestLots");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Cooperatives_PayPeriodStartDay",
                table: "Cooperatives");

            // No endpoint sends a payroll or sends it back, so no sheet holds these two statuses; a header an older demo made would, and goes back to Provisional.
            migrationBuilder.Sql("UPDATE Payrolls SET StatusId = 3401 WHERE StatusId IN (3402, 3403)");

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3402);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3403);

            migrationBuilder.DropColumn(
                name: "DisplayColor",
                table: "Statuses");

            migrationBuilder.DropColumn(
                name: "IsInitial",
                table: "Statuses");

            migrationBuilder.DropColumn(
                name: "Meaning",
                table: "Statuses");

            migrationBuilder.DropColumn(
                name: "CreatedBySystem",
                table: "Payrolls");

            migrationBuilder.DropColumn(
                name: "SendBackNote",
                table: "Payrolls");

            migrationBuilder.DropColumn(
                name: "SentAt",
                table: "Payrolls");

            migrationBuilder.DropColumn(
                name: "SentBackAt",
                table: "Payrolls");

            migrationBuilder.DropColumn(
                name: "SentBackByAccountId",
                table: "Payrolls");

            migrationBuilder.DropColumn(
                name: "SentByAccountId",
                table: "Payrolls");

            migrationBuilder.DropColumn(
                name: "PurchaseRequestId",
                table: "HarvestLots");

            migrationBuilder.DropColumn(
                name: "IllustrationUrl",
                table: "FarmingInputItems");

            migrationBuilder.DropColumn(
                name: "LastQrStep",
                table: "EmployeeDevices");

            migrationBuilder.DropColumn(
                name: "ExpectedHarvestDate",
                table: "CultivationCycles");

            migrationBuilder.DropColumn(
                name: "NurseryAllocatedQuantity",
                table: "CultivationCycles");

            migrationBuilder.DropColumn(
                name: "AutomaticPayroll",
                table: "Cooperatives");

            migrationBuilder.DropColumn(
                name: "PayPeriodStartDay",
                table: "Cooperatives");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DisplayColor",
                table: "Statuses",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsInitial",
                table: "Statuses",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Meaning",
                table: "Statuses",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CreatedBySystem",
                table: "Payrolls",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SendBackNote",
                table: "Payrolls",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SentAt",
                table: "Payrolls",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SentBackAt",
                table: "Payrolls",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SentBackByAccountId",
                table: "Payrolls",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SentByAccountId",
                table: "Payrolls",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PurchaseRequestId",
                table: "HarvestLots",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IllustrationUrl",
                table: "FarmingInputItems",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LastQrStep",
                table: "EmployeeDevices",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ExpectedHarvestDate",
                table: "CultivationCycles",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "NurseryAllocatedQuantity",
                table: "CultivationCycles",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AutomaticPayroll",
                table: "Cooperatives",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "PayPeriodStartDay",
                table: "Cooperatives",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "NurseryBatchAllocations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CycleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NurseryBatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RaftId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Quantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NurseryBatchAllocations", x => x.Id);
                    table.CheckConstraint("CK_NurseryBatchAllocation_Quantity", "[Quantity] > 0");
                    table.ForeignKey(
                        name: "FK_NurseryBatchAllocations_CultivationCycles_CycleId",
                        column: x => x.CycleId,
                        principalTable: "CultivationCycles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_NurseryBatchAllocations_NurseryBatches_NurseryBatchId",
                        column: x => x.NurseryBatchId,
                        principalTable: "NurseryBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_NurseryBatchAllocations_Rafts_RaftId",
                        column: x => x.RaftId,
                        principalTable: "Rafts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StatusTransitions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    FromStatusId = table.Column<int>(type: "int", nullable: false),
                    ToStatusId = table.Column<int>(type: "int", nullable: false),
                    AllowedPerformers = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    GuardCondition = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ReasonRequired = table.Column<bool>(type: "bit", nullable: false),
                    TriggerEvent = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    UseCases = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StatusTransitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StatusTransitions_Statuses_FromStatusId",
                        column: x => x.FromStatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StatusTransitions_Statuses_ToStatusId",
                        column: x => x.ToStatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "StatusTransitions",
                columns: new[] { "Id", "AllowedPerformers", "FromStatusId", "GuardCondition", "ReasonRequired", "ToStatusId", "TriggerEvent", "UseCases" },
                values: new object[,]
                {
                    { 1, "Cooperative Owner; Zone Manager", 101, "Not blocked by the entity's Discontinued condition (section 3.4.3).", false, 102, "Action", "UC12, UC15, UC18, UC20, UC22, UC42, UC55" },
                    { 2, "Cooperative Owner; Zone Manager", 102, "Return the item to use.", false, 101, "Action", "UC12, UC15, UC18, UC20, UC22, UC42, UC55" },
                    { 4, "Cooperative Owner (staff accounts); System Administrator (trader accounts, platform administration outside the cooperative app)", 301, "Lock the account.", true, 302, "Action", "ACS-09" },
                    { 5, "Cooperative Owner (staff accounts); System Administrator (trader accounts, platform administration outside the cooperative app)", 302, "Unlock after verification; trader accounts are unlocked only by the System Administrator.", true, 301, "Action", "ACS-09" },
                    { 6, "Cooperative Owner; Zone Manager; System", 401, "The last working day has passed: at once for an earlier day, by the system on the next day for today (BR-LABOR-01).", false, 402, "Action", "LABOR-01" },
                    { 7, "Cooperative Owner; Zone Manager", 402, "No Employed employee has the phone number; needs no new approval (BR-LABOR-01).", false, 401, "Action", "LABOR-01" },
                    { 8, "Cooperative Owner", 501, "Revoke the zone–role pair; Cooperative Owner only.", true, 502, "Action", "UC11" },
                    { 9, "Cooperative Owner; Zone Manager", 601, "Every seeded lane is Harvested; any other close is an early close the Cooperative Owner approves first (BR-CYCLE-07).", false, 602, "Action", "CYCLE-06" },
                    { 53, "System", 1601, "The SMS Service or Zalo OA accepts the message.", false, 1602, "System", "SALE-06, SALE-03, CLAIM-02, TRACE-04" },
                    { 54, "System", 1601, "The SMS Service or Zalo OA does not accept the message.", false, 1603, "System", "SALE-06, SALE-03, CLAIM-02, TRACE-04" },
                    { 70, "Zone Manager (own drafts); Cooperative Owner", 1701, "Supplier In Use, date received not later than today, and every line complete with an input item In Use, a quantity greater than 0 and a unit price of 0 or more.", false, 1702, "Action", "PURCHASE-01" },
                    { 72, "Zone Manager (own receipts); Cooperative Owner", 1702, "No seed lot of the receipt has been seeded and the receipt has no purchase return that is not cancelled.", true, 1703, "Action", "PURCHASE-06" },
                    { 73, "Accountant; Cooperative Owner", 1801, "A payment recorded by mistake; payments are never cancelled with a purchase receipt.", true, 1802, "Action", "PURCHASE-05" },
                    { 74, "Cooperative Owner", 1901, "A return recorded by mistake; it no longer counts in the supplier debt or the quantity returned.", true, 1902, "Action", "PURCHASE-02" },
                    { 76, "Cooperative Owner", 2001, "The raft has no open cycle; the last lanes are removed first (BR-FARM-04).", false, 2002, "Action", "FARM-04" },
                    { 77, "Cooperative Owner", 2002, "The raft has no open cycle; Removed lanes return in number order before new numbers (BR-FARM-04).", false, 2001, "Action", "FARM-04" },
                    { 80, "Zone Manager (own); Cooperative Owner", 2101, "Void a wrong measurement with a reason.", true, 2102, "Action", "WATER-02" },
                    { 150, "Cooperative Owner; Zone Manager; System", 2901, "Sold whole lot, closed with the leftover kg, or the time limit reached.", false, 2903, "Action", "HARVEST-02, GFR-05, SALE-05, SALE-07, CLAIM-03" },
                    { 151, "Cooperative Owner; Zone Manager; System", 2902, "Closed again in the same four ways.", false, 2903, "Action", "HARVEST-02, GFR-05, SALE-05, SALE-07, CLAIM-03" },
                    { 152, "Cooperative Owner; Zone Manager; System", 2903, "A sales return puts oysters back, or the invoice that closed the lot is cancelled or loses its tick (the system); a Zone Manager or the Owner reopens a lot closed Unsold, Spoiled or by the time limit, not by an invoice, with a reason. The kg taken off the stock at the close come back.", false, 2902, "System", "CLAIM-03, SALE-07, SALE-08, HARVEST-REOPEN" },
                    { 180, "Cooperative Owner", 3201, "A staffing plan exists (employees assigned at once, no agreement to wait for); the accepted cages are above 0, not more than the requested cages and equal to the planned cages; the pickup date is not before today and equals the planned pickup.", false, 3202, "Action", "SALE-03" },
                    { 181, "Cooperative Owner", 3201, "A note gives the reason.", true, 3203, "Action", "SALE-03" },
                    { 182, "Trader", 3201, "The trader cancels; a reason is optional.", false, 3204, "Action", "SALE-02" },
                    { 183, "Trader", 3202, "The request is not Fulfilled; a reason is optional.", false, 3204, "Action", "SALE-02" },
                    { 200, "Cooperative Owner", 3501, "The invoice has no sales return that is not cancelled; a cash payment is cancelled with it only when the cash was given back.", true, 3502, "Action", "SALE-08" },
                    { 210, "Accountant", 3601, "The cash or the transfer that arrived matches the payment.", false, 3602, "Action", "DEBT-02" },
                    { 211, "Accountant; Cooperative Owner", 3601, "The customer did not pay, or the cash was given back when the invoice was cancelled.", true, 3603, "Action", "DEBT-02, SALE-08" },
                    { 212, "Accountant", 3602, "Recorded by mistake, or the money never arrived.", true, 3603, "Action", "DEBT-02" },
                    { 213, "Accountant; Cooperative Owner", 3602, "The money paid back that settled a payment without a customer was cancelled, so the payment is settled again.", false, 3601, "Action", "DEBT-02.4, DEBT-02.6" },
                    { 220, "Cooperative Owner", 3701, "The invoice is not cancelled and has no sales return; an offer is chosen, a price cut has its amount, and a note is optional.", false, 3704, "Action", "CLAIM-02" },
                    { 221, "Cooperative Owner", 3701, "A note gives the reason.", true, 3702, "Action", "CLAIM-02" },
                    { 222, "Trader", 3704, "The trader of the claim accepts the offer; the Owner then runs the return (CLAIM-03) or the price cut (SALE-07).", false, 3703, "Action", "CLAIM-02" },
                    { 223, "Trader", 3704, "The trader of the claim declines the offer; a note is optional.", false, 3705, "Action", "CLAIM-02" },
                    { 230, "Cooperative Owner", 3801, "The payroll of the pay period of the day is not Awaiting finalization or Finalized, and the times stay in order.", false, 3802, "Action", "LABOR-05" },
                    { 231, "Cooperative Owner", 3801, "A note gives the reason.", true, 3803, "Action", "LABOR-05" },
                    { 232, "Employee", 3801, "The employee cancels their own request before it is reviewed.", false, 3804, "Action", "LABOR-10" },
                    { 233, "Cooperative Owner", 3405, "The lines of the period are calculated.", false, 3401, "Action", "LABOR-12" },
                    { 234, "Cooperative Owner", 3401, "The period has ended, no attendance of it is incomplete, no hours lack a rate and no request for it awaits review.", false, 3404, "Action", "LABOR-13" },
                    { 235, "Cooperative Owner", 3401, "A payroll sheet dropped before it is finalized.", false, 3406, "Action", "LABOR-12" },
                    { 236, "Cooperative Owner", 3405, "A sheet still being made is dropped.", false, 3406, "Action", "LABOR-12" },
                    { 240, "Cooperative Owner", 4101, "A return recorded by mistake; it no longer counts in the customer debt, the lots or the reports, and the invoice can again be corrected or cancelled.", true, 4102, "Action", "CLAIM-03" },
                    { 30074, "Cooperative Owner", 303, "Approve the trader's request.", false, 301, "Action", "ACS-01 (Zalo)" },
                    { 30075, "Cooperative Owner", 303, "Refuse the trader's request.", true, 304, "Action", "ACS-01 (Zalo)" },
                    { 30076, "Trader", 304, "The trader asks again from the Zalo Mini App.", false, 303, "Action", "ACS-01 (Zalo)" },
                    { 30077, "Zone Manager (own drafts); Cooperative Owner", 1903, "The return is complete and within the quantity left on each line of its receipt; dated the day it is completed.", false, 1901, "Action", "PURCHASE-02" },
                    { 30078, "System; Accountant", 3604, "The transfer with the payment code arrived; the payment takes the amount received.", false, 3602, "Action", "DEBT-02, GFR-08" },
                    { 30079, "Accountant; Cooperative Owner", 3604, "The customer did not transfer, or the code was made by mistake.", true, 3603, "Action", "DEBT-02" },
                    { 30080, "Cooperative Owner; Zone Manager", 3901, "The issue is complete and no item or seed lot would go below zero when that is not allowed.", false, 3902, "Action", "INPUT-ISSUE" },
                    { 30081, "Cooperative Owner; Zone Manager", 3902, "An issue recorded by mistake; its quantities go back in the stock.", true, 3903, "Action", "INPUT-ISSUE" },
                    { 30082, "Cooperative Owner", 3201, "Legacy, no longer made: since 24/11/2026 the Owner picks the employees, their shifts become work assignments at once and the request stays Awaiting response.", false, 3205, "Action", "SALE-03" },
                    { 30083, "Cooperative Owner", 3205, "The Owner declines with a reason.", true, 3203, "Action", "SALE-03" },
                    { 30084, "Trader", 3205, "Only the requesting customer may cancel; release shift reservations.", false, 3204, "Action", "SALE-02" },
                    { 30085, "Cooperative Owner", 3205, "A staffing plan exists (employees chosen, assignments made); cages and pickup match it. No employee agreement is counted any more.", false, 3202, "Action", "SALE-03" },
                    { 30090, "Cooperative Owner; Zone Manager", 4001, "The survivors are not above the pieces put in; no share and no cycle is created (v2, 13/11/2026).", false, 4002, "Action", "NURSERY-03" },
                    { 30091, "Cooperative Owner; Zone Manager", 4001, "The batch is still Nursing; the pieces go back in the stock.", true, 4003, "Action", "NURSERY-02" },
                    { 30092, "Cooperative Owner; Zone Manager", 4002, "The batch is Completed and pieces are left in the pool; they are written off as an estimated loss.", false, 4004, "Action", "NURSERY-05" },
                    { 30100, "Cooperative Owner; Zone Manager", 4301, "The disposal has a reason and its lines are complete.", false, 4302, "Action", "PRODUCT-DISPOSAL" },
                    { 30101, "Cooperative Owner; Zone Manager", 4302, "A disposal recorded by mistake; its kg go back in the stock.", true, 4303, "Action", "PRODUCT-DISPOSAL" },
                    { 30102, "System", 4401, "The first invoice of the order is saved with goods still to deliver.", false, 4402, "Action", "SALES-ORDER" },
                    { 30103, "System; Cooperative Owner", 4401, "An invoice covers every line of the order, or the Owner finishes the order.", false, 4403, "Action", "SALES-ORDER" },
                    { 30104, "Cooperative Owner; Zone Manager; System", 4402, "The Owner or a Zone Manager of the zone finishes the order, or every line is delivered.", false, 4403, "Action", "SALES-ORDER" },
                    { 30105, "Cooperative Owner", 4401, "A reason is given.", true, 4404, "Action", "SALES-ORDER" },
                    { 30106, "Cooperative Owner", 4402, "A reason is given and no invoice of the order is left uncancelled; otherwise the order is finished instead.", true, 4404, "Action", "SALES-ORDER" },
                    { 30107, "System", 4403, "An invoice of the finished order is cancelled: the order is open again.", false, 4402, "Action", "SALES-ORDER" },
                    { 30108, "Cooperative Owner", 3201, "The Owner saves an order made from the request.", false, 3206, "Action", "SALES-ORDER" },
                    { 30109, "Cooperative Owner", 3202, "The request is not Fulfilled and has no order yet; the Owner saves an order made from it.", false, 3206, "Action", "SALES-ORDER" },
                    { 30110, "Cooperative Owner", 4201, "The plan has lines, every raft has an Open cycle and no lane is in another open plan; the Zone Managers of the zone are told.", false, 4202, "Action", "HARVEST-PLAN" },
                    { 30111, "System", 4206, "The first lot is recorded from a line of the plan, or the first lane is left out.", false, 4203, "Action", "HARVEST-PLAN" },
                    { 30112, "Cooperative Owner", 4207, "The plan was handed over by a Zone Manager; only the Owner completes it. The system never completes a plan by itself.", false, 4204, "Action", "HARVEST-PLAN" },
                    { 30114, "Cooperative Owner", 4201, "A reason is given.", true, 4205, "Action", "HARVEST-PLAN" },
                    { 30115, "Cooperative Owner", 4202, "A reason is given and no lot was recorded from the plan.", true, 4205, "Action", "HARVEST-PLAN" },
                    { 30116, "System", 4204, "The Owner completed the plan and a lot is removed or a lane left out is put back: a line is open again.", false, 4203, "Action", "HARVEST-PLAN" },
                    { 30117, "Cooperative Owner; Zone Manager", 2904, "The lot is weighed: each product line has its weighed kg, which go into the stock of the products.", false, 2901, "Action", "HARVEST-WEIGH" },
                    { 30118, "System", 4402, "The only invoice of the order that is not cancelled is cancelled: no invoice is left, so the order is a draft again.", false, 4401, "Action", "SALES-ORDER" },
                    { 30119, "System", 4403, "The only invoice of the finished order that is not cancelled is cancelled: no invoice is left, so the order is a draft again.", false, 4401, "Action", "SALES-ORDER" },
                    { 30120, "Cooperative Owner; Zone Manager", 4202, "A Zone Manager of a zone of the plan presses Receive; there is no refusal.", false, 4206, "Action", "HARVEST-PLAN" },
                    { 30121, "Cooperative Owner; Zone Manager", 4203, "Every line of the plan has its lot or has every lane left out; a Zone Manager presses Hand over.", false, 4207, "Action", "HARVEST-PLAN" },
                    { 30122, "System", 4207, "A lot of a handed over plan is removed, or a lane left out is put back: a line is open again.", false, 4203, "Action", "HARVEST-PLAN" },
                    { 30123, "Cooperative Owner", 4206, "A reason is given and no lot was recorded from the plan.", true, 4205, "Action", "HARVEST-PLAN" }
                });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 101,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, true, "Selectable for new records." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 102,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "Not shown when selecting new records; old data stays unchanged." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 301,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, true, "Can log in according to effective zone–role pairs." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 302,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "Cannot log in; data and history stay unchanged." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 303,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "A trader asked for an account from the Zalo Mini App; cannot log in until the Cooperative Owner approves." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 304,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "The Cooperative Owner refused the request; the trader may ask again." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 401,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, true, "The employee is working and can be assigned work and recorded in attendance." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 402,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "No new work or attendance; wage and salary history stays unchanged." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 501,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, true, "The account has the role's permissions in the assigned zone." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 502,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "The grant no longer applies; re-granting creates a new grant." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 601,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, true, "The cycle is in progress; seeding, logging and harvesting are allowed." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 602,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "The cycle has ended; the raft can open a new cycle." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1601,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, true, "Handed to the SMS Service or Zalo OA, no answer yet." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1602,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "Accepted by the service; this does not mean that the trader has read it." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1603,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "Not accepted by the service; nothing that was saved is undone (GFR-06)." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1701,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, true, "Being prepared; counts nowhere and creates no seed lot. Removed, never cancelled." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1702,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "Counts in the supplier debt; each Seed line is a seed lot. May be edited, never its supplier, and every edit is audited." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1703,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "Kept with its reason; counts nowhere; its Seed lines are no longer seed lots." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1801,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, true, "Counts in the supplier debt; cannot be edited." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1802,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "Recorded by mistake; no longer counts in the supplier debt." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1901,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, true, "Lowers the supplier debt. May be edited within the quantity left, never below the money paid back, and every edit is audited." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1902,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "Recorded by mistake; no longer counts." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1903,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, true, "Being prepared; counts nowhere and holds no quantity. Removed, never cancelled." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 2001,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, true, "Seeded and harvested." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 2002,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "Removed at the end of the raft; keeps its number and records." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 2101,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, true, "Counts in the charts and the water alert of the zone." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 2102,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "Kept in the history with its reason; no longer counts." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 2901,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "For sale: it can be chosen on a sales invoice. Weighed; it can be weighed again." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 2902,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "Closed earlier and for sale again; the days before automatic close count from the day it was reopened." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 2903,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "No longer for sale; its close reason is kept and what was left of its products is taken off the stock. The system can reopen it; a lot closed by hand or by the time limit is also reopened by hand." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 2904,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, true, "Recorded with its estimated kg (Chờ cân); not for sale and in no stock until it is weighed. It can be corrected or removed." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3201,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, true, "Sent by the trader; not answered yet." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3202,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "Accepted by the Cooperative Owner, possibly for fewer cages; invoices may be linked to it." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3203,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "Declined by the Cooperative Owner with a note." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3204,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "Cancelled by the trader; no invoice can be linked to it." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3205,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "Legacy (31/10/2026 flow): requests sent before 24/11/2026 may still wait here for employees to answer; new staffing plans keep the request Awaiting response and make the work assignments at once." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3206,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "The Cooperative Owner made an order (DH) from the request; the order carries on from here." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3401,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, true, "Being prepared; its lines are calculated from the current records." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3404,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "The amounts are saved; never changed or reopened." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3405,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "The sheet is being made." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3406,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "Dropped before it was finalized; it no longer counts." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3501,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, true, "The sale is saved; only the Cooperative Owner corrects or cancels it." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3502,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "The sale did not happen; kept with its reason and counts nowhere." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3601,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, true, "Recorded at the port or by the system; counts in the debt until the Accountant confirms or cancels it." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3602,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "The money has arrived or was recorded by the Accountant; counts in the debt." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3603,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "Recorded by mistake or never received; no longer counts." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3604,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, true, "A VietQR code was made for the payment and the transfer has not arrived; counts nowhere until it does." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3701,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, true, "Sent by the trader; not answered yet." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3702,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "Rejected by the Cooperative Owner with a note." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3703,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "The trader accepted the offer of the Cooperative Owner (a return of all the goods or a price cut of the whole invoice)." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3704,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "The Cooperative Owner proposed a return of all the goods or a price cut of the whole invoice; the trader has not answered yet." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3705,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "The trader declined the offer of the Cooperative Owner." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3801,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, true, "Sent by the employee; not reviewed yet." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3802,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "Approved by the Cooperative Owner; leave is recorded, a missed time is in the attendance." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3803,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "Rejected by the Cooperative Owner with a note." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3804,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "Cancelled by the employee while it was awaiting review; kept in the list." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3901,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, true, "Being prepared; holds no quantity. Removed, never cancelled." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3902,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "The quantities have left the stock; may still be edited, which writes them again." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3903,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "Recorded by mistake; the quantities are back in the stock and it counts nowhere." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4001,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, true, "The pieces left the stock and are in the nursery; it can be edited or cancelled." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4002,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "The survivors are recorded (Đã ươm xong): the batch is a pool that cycles seed from; the survivors can still be corrected, not below what is seeded. It is closed once the rafts are seeded." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4003,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "Recorded by mistake before it was completed; the pieces are back in the stock." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4004,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "Closed with pieces left in the pool: the remainder is written off as an estimated loss; nothing is seeded from it any more." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4101,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, true, "Takes the invoice out of the customer debt and counts in the lots and the reports; cannot be edited." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4102,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "Recorded by mistake; counts nowhere." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4201,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, true, "Being prepared by the Owner; the Zone Managers do not see it yet." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4202,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "Handed to the Zone Managers of its zone; none of them has pressed Receive yet, so no lot is recorded and no lane left out." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4203,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "A lot is recorded from a line of the plan, or a lane is left out; it stays here until a Zone Manager hands the plan over." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4204,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "The Owner closed a plan the Zone Manager handed over; removing a lot or putting a lane back opens it again." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4205,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "Cancelled with a reason before any lot was recorded." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4206,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "A Zone Manager of the plan's zone pressed Receive (Nhận); lots can be recorded and lanes left out from now on." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4207,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "A Zone Manager pressed Hand over (Bàn giao): every line has its lot or all its lanes left out; waits for the Owner to complete the plan." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4301,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, true, "Being prepared; holds no kg. Removed, never cancelled." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4302,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "The kg have left the stock of their products; may still be edited, which writes them again." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4303,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "Recorded by mistake; the kg are back in the stock and it counts nowhere." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4401,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, true, "The order is agreed with the buyer; no invoice has been made from it yet. Its advance is reserved for it." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4402,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "At least one invoice was made from the order; goods are still to be delivered. Its advance is reserved for it." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4403,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "Finished by hand or fully delivered; what is left of its advance is general credit of the customer. A cancelled invoice reopens it." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4404,
                columns: new[] { "DisplayColor", "IsInitial", "Meaning" },
                values: new object[] { null, false, "Cancelled with a reason; what is left of its advance is general credit of the customer." });

            migrationBuilder.InsertData(
                table: "Statuses",
                columns: new[] { "Id", "Code", "DisplayColor", "DisplayName", "Group", "IsFinal", "IsInitial", "Meaning", "SortOrder" },
                values: new object[,]
                {
                    { 3402, "AWAITING_FINALIZATION", null, "Awaiting finalization", "PAYROLL", false, false, "Sent to the Cooperative Owner; the records of the period are locked.", 2 },
                    { 3403, "SENT_BACK", null, "Sent back", "PAYROLL", false, false, "Returned with a note for correction; the records of the period can change again.", 3 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Payrolls_SentBackByAccountId",
                table: "Payrolls",
                column: "SentBackByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Payrolls_SentByAccountId",
                table: "Payrolls",
                column: "SentByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_HarvestLots_PurchaseRequestId",
                table: "HarvestLots",
                column: "PurchaseRequestId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Cooperatives_PayPeriodStartDay",
                table: "Cooperatives",
                sql: "[PayPeriodStartDay] BETWEEN 1 AND 28");

            migrationBuilder.CreateIndex(
                name: "IX_NurseryBatchAllocations_CycleId",
                table: "NurseryBatchAllocations",
                column: "CycleId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NurseryBatchAllocations_NurseryBatchId_RaftId",
                table: "NurseryBatchAllocations",
                columns: new[] { "NurseryBatchId", "RaftId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NurseryBatchAllocations_RaftId",
                table: "NurseryBatchAllocations",
                column: "RaftId");

            migrationBuilder.CreateIndex(
                name: "IX_StatusTransitions_FromStatusId_ToStatusId",
                table: "StatusTransitions",
                columns: new[] { "FromStatusId", "ToStatusId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StatusTransitions_ToStatusId",
                table: "StatusTransitions",
                column: "ToStatusId");

            migrationBuilder.AddForeignKey(
                name: "FK_HarvestLots_PurchaseRequests_PurchaseRequestId",
                table: "HarvestLots",
                column: "PurchaseRequestId",
                principalTable: "PurchaseRequests",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Payrolls_Accounts_SentBackByAccountId",
                table: "Payrolls",
                column: "SentBackByAccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Payrolls_Accounts_SentByAccountId",
                table: "Payrolls",
                column: "SentByAccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
