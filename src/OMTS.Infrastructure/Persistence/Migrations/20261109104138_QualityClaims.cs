using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class QualityClaims : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "QualityClaims",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Offer = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ResponseNote = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RespondedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RespondedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QualityClaims", x => x.Id);
                    table.CheckConstraint("CK_QualityClaim_Accepted_Has_Offer", "[StatusId] <> 3703 OR [Offer] IS NOT NULL");
                    table.CheckConstraint("CK_QualityClaim_Answered_Has_Response", "[StatusId] = 3701 OR ([RespondedAt] IS NOT NULL AND [RespondedByAccountId] IS NOT NULL)");
                    table.CheckConstraint("CK_QualityClaim_Rejected_Has_Note", "[StatusId] <> 3702 OR [ResponseNote] IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_QualityClaims_Accounts_RespondedByAccountId",
                        column: x => x.RespondedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_QualityClaims_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_QualityClaims_SalesInvoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "SalesInvoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_QualityClaims_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "QualityClaimPhotos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClaimId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    FileKey = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QualityClaimPhotos", x => x.Id);
                    table.CheckConstraint("CK_QualityClaimPhoto_SortOrder", "[SortOrder] BETWEEN 1 AND 10");
                    table.ForeignKey(
                        name: "FK_QualityClaimPhotos_QualityClaims_ClaimId",
                        column: x => x.ClaimId,
                        principalTable: "QualityClaims",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "Statuses",
                columns: new[] { "Id", "Code", "DisplayColor", "DisplayName", "Group", "IsFinal", "IsInitial", "Meaning", "SortOrder" },
                values: new object[,]
                {
                    { 3701, "AWAITING_RESPONSE", null, "Awaiting response", "QUALITY_CLAIM", false, true, "Sent by the trader; not answered yet.", 1 },
                    { 3702, "REJECTED", null, "Rejected", "QUALITY_CLAIM", true, false, "Rejected by the Cooperative Owner with a note.", 2 },
                    { 3703, "ACCEPTED", null, "Accepted", "QUALITY_CLAIM", true, false, "Accepted by the Cooperative Owner, who offered a return of all the goods or a price cut of the whole invoice.", 3 }
                });

            migrationBuilder.InsertData(
                table: "StatusTransitions",
                columns: new[] { "Id", "AllowedPerformers", "FromStatusId", "GuardCondition", "ReasonRequired", "ToStatusId", "TriggerEvent", "UseCases" },
                values: new object[,]
                {
                    { 220, "Cooperative Owner", 3701, "The invoice is not cancelled and has no sales return; an offer is chosen and a note is optional.", false, 3703, "Action", "CLAIM-02" },
                    { 221, "Cooperative Owner", 3701, "A note gives the reason.", true, 3702, "Action", "CLAIM-02" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_QualityClaimPhotos_ClaimId_SortOrder",
                table: "QualityClaimPhotos",
                columns: new[] { "ClaimId", "SortOrder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QualityClaims_Code",
                table: "QualityClaims",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QualityClaims_CustomerId_SentAt",
                table: "QualityClaims",
                columns: new[] { "CustomerId", "SentAt" });

            migrationBuilder.CreateIndex(
                name: "IX_QualityClaims_RespondedByAccountId",
                table: "QualityClaims",
                column: "RespondedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityClaims_StatusId_SentAt",
                table: "QualityClaims",
                columns: new[] { "StatusId", "SentAt" });

            migrationBuilder.CreateIndex(
                name: "UX_QualityClaims_Invoice_Awaiting",
                table: "QualityClaims",
                column: "InvoiceId",
                unique: true,
                filter: "[StatusId] = 3701");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "QualityClaimPhotos");

            migrationBuilder.DropTable(
                name: "QualityClaims");

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 220);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 221);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3701);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3702);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3703);
        }
    }
}
