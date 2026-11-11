using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InputIssuesAndIssueTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InputIssueTypes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IsSystem = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InputIssueTypes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InputIssues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    IssuedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ZoneId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RaftId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IssueTypeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReceiverEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancelledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancelReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InputIssues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InputIssues_Employees_ReceiverEmployeeId",
                        column: x => x.ReceiverEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InputIssues_FarmingZones_ZoneId",
                        column: x => x.ZoneId,
                        principalTable: "FarmingZones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InputIssues_InputIssueTypes_IssueTypeId",
                        column: x => x.IssueTypeId,
                        principalTable: "InputIssueTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InputIssues_Rafts_RaftId",
                        column: x => x.RaftId,
                        principalTable: "Rafts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InputIssues_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InputIssueLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IssueId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineNumber = table.Column<int>(type: "int", nullable: false),
                    ItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ItemUnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UnitFactor = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    Unit = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    EnteredQuantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,5)", precision: 18, scale: 5, nullable: false),
                    SeedLotLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RaftId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UnitCost = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    Value = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InputIssueLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InputIssueLines_FarmingInputItems_ItemId",
                        column: x => x.ItemId,
                        principalTable: "FarmingInputItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InputIssueLines_InputIssues_IssueId",
                        column: x => x.IssueId,
                        principalTable: "InputIssues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InputIssueLines_InputItemUnits_ItemUnitId",
                        column: x => x.ItemUnitId,
                        principalTable: "InputItemUnits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InputIssueLines_PurchaseReceiptLines_SeedLotLineId",
                        column: x => x.SeedLotLineId,
                        principalTable: "PurchaseReceiptLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InputIssueLines_Rafts_RaftId",
                        column: x => x.RaftId,
                        principalTable: "Rafts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "InputIssueTypes",
                columns: new[] { "Id", "CreatedAt", "CreatedBy", "IsActive", "IsSystem", "Name", "UpdatedAt", "UpdatedBy" },
                values: new object[,]
                {
                    { new Guid("6e1f0b20-0000-4d4a-8b20-000000000001"), new DateTimeOffset(new DateTime(2026, 11, 11, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 7, 0, 0, 0)), null, true, true, "Thả giống", null, null },
                    { new Guid("6e1f0b20-0000-4d4a-8b20-000000000002"), new DateTimeOffset(new DateTime(2026, 11, 11, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 7, 0, 0, 0)), null, true, true, "Sửa chữa bè", null, null },
                    { new Guid("6e1f0b20-0000-4d4a-8b20-000000000003"), new DateTimeOffset(new DateTime(2026, 11, 11, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 7, 0, 0, 0)), null, true, true, "Dùng chung cho vùng", null, null }
                });

            migrationBuilder.InsertData(
                table: "Statuses",
                columns: new[] { "Id", "Code", "DisplayColor", "DisplayName", "Group", "IsFinal", "IsInitial", "Meaning", "SortOrder" },
                values: new object[,]
                {
                    { 3801, "DRAFT", null, "Draft", "INPUT_ISSUE", false, true, "Being prepared; holds no quantity. Removed, never cancelled.", 1 },
                    { 3802, "COMPLETED", null, "Completed", "INPUT_ISSUE", false, false, "The quantities have left the stock; may still be edited, which writes them again.", 2 },
                    { 3803, "CANCELLED", null, "Cancelled", "INPUT_ISSUE", true, false, "Recorded by mistake; the quantities are back in the stock and it counts nowhere.", 3 }
                });

            migrationBuilder.InsertData(
                table: "StatusTransitions",
                columns: new[] { "Id", "AllowedPerformers", "FromStatusId", "GuardCondition", "ReasonRequired", "ToStatusId", "TriggerEvent", "UseCases" },
                values: new object[,]
                {
                    { 30080, "Cooperative Owner; Zone Manager", 3801, "The issue is complete and no item or seed lot would go below zero when that is not allowed.", false, 3802, "Action", "INPUT-ISSUE" },
                    { 30081, "Cooperative Owner; Zone Manager", 3802, "An issue recorded by mistake; its quantities go back in the stock.", true, 3803, "Action", "INPUT-ISSUE" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_InputIssueLines_IssueId_LineNumber",
                table: "InputIssueLines",
                columns: new[] { "IssueId", "LineNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InputIssueLines_ItemId",
                table: "InputIssueLines",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_InputIssueLines_ItemUnitId",
                table: "InputIssueLines",
                column: "ItemUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_InputIssueLines_RaftId",
                table: "InputIssueLines",
                column: "RaftId");

            migrationBuilder.CreateIndex(
                name: "IX_InputIssueLines_SeedLotLineId",
                table: "InputIssueLines",
                column: "SeedLotLineId");

            migrationBuilder.CreateIndex(
                name: "IX_InputIssues_Code",
                table: "InputIssues",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InputIssues_IssueTypeId",
                table: "InputIssues",
                column: "IssueTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_InputIssues_Kind_IssuedAt",
                table: "InputIssues",
                columns: new[] { "Kind", "IssuedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_InputIssues_RaftId",
                table: "InputIssues",
                column: "RaftId");

            migrationBuilder.CreateIndex(
                name: "IX_InputIssues_ReceiverEmployeeId",
                table: "InputIssues",
                column: "ReceiverEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_InputIssues_StatusId",
                table: "InputIssues",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "IX_InputIssues_ZoneId",
                table: "InputIssues",
                column: "ZoneId");

            migrationBuilder.CreateIndex(
                name: "IX_InputIssueTypes_IsActive_Name",
                table: "InputIssueTypes",
                columns: new[] { "IsActive", "Name" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InputIssueLines");

            migrationBuilder.DropTable(
                name: "InputIssues");

            migrationBuilder.DropTable(
                name: "InputIssueTypes");

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30080);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30081);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3801);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3802);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3803);
        }
    }
}
