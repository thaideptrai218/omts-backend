using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// FARM-01 to SRS 27/10: the new profile fields (short name, year established, social link, cover photo, introduction
    /// in Vietnamese and English), the CooperativeCertifications table, and no cooperative status any more (statuses
    /// 201–202 and transition 3 are removed). Schema only: the demo seed fills the new fields. Down puts the status
    /// back as Completed for a cooperative with a tax code, else Preliminary.
    /// </summary>
    public partial class CooperativeProfileV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Cooperatives_Statuses_StatusId",
                table: "Cooperatives");

            migrationBuilder.DropIndex(
                name: "IX_Cooperatives_StatusId",
                table: "Cooperatives");

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 3);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 201);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 202);

            migrationBuilder.DropColumn(
                name: "StatusId",
                table: "Cooperatives");

            migrationBuilder.AddColumn<string>(
                name: "CoverPhotoUrl",
                table: "Cooperatives",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IntroductionEn",
                table: "Cooperatives",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IntroductionVi",
                table: "Cooperatives",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShortName",
                table: "Cooperatives",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SocialLink",
                table: "Cooperatives",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "YearEstablished",
                table: "Cooperatives",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CooperativeCertifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CooperativeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Number = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Issuer = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ExpiryDate = table.Column<DateOnly>(type: "date", nullable: true),
                    FileUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CooperativeCertifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CooperativeCertifications_Cooperatives_CooperativeId",
                        column: x => x.CooperativeId,
                        principalTable: "Cooperatives",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CooperativeCertifications_CooperativeId",
                table: "CooperativeCertifications",
                column: "CooperativeId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CooperativeCertifications");

            migrationBuilder.DropColumn(
                name: "CoverPhotoUrl",
                table: "Cooperatives");

            migrationBuilder.DropColumn(
                name: "IntroductionEn",
                table: "Cooperatives");

            migrationBuilder.DropColumn(
                name: "IntroductionVi",
                table: "Cooperatives");

            migrationBuilder.DropColumn(
                name: "ShortName",
                table: "Cooperatives");

            migrationBuilder.DropColumn(
                name: "SocialLink",
                table: "Cooperatives");

            migrationBuilder.DropColumn(
                name: "YearEstablished",
                table: "Cooperatives");

            migrationBuilder.AddColumn<int>(
                name: "StatusId",
                table: "Cooperatives",
                type: "int",
                nullable: false,
                defaultValue: 201);

            migrationBuilder.InsertData(
                table: "Statuses",
                columns: new[] { "Id", "Code", "DisplayColor", "DisplayName", "Group", "IsFinal", "IsInitial", "Meaning", "SortOrder" },
                values: new object[,]
                {
                    { 201, "DRAFT", null, "Preliminary", "COOPERATIVE", false, true, "Created at deployment; the Cooperative Owner has not completed the legal profile yet.", 1 },
                    { 202, "COMPLETED", null, "Completed", "COOPERATIVE", false, false, "The Cooperative Owner has entered all mandatory cooperative profile information.", 2 }
                });

            migrationBuilder.InsertData(
                table: "StatusTransitions",
                columns: new[] { "Id", "AllowedPerformers", "FromStatusId", "GuardCondition", "ReasonRequired", "ToStatusId", "TriggerEvent", "UseCases" },
                values: new object[] { 3, "Cooperative Owner", 201, "All mandatory fields of the cooperative profile are filled.", false, 202, "Action", "FARM-01" });

            migrationBuilder.Sql("UPDATE Cooperatives SET StatusId = 202 WHERE TaxCode IS NOT NULL;");

            migrationBuilder.CreateIndex(
                name: "IX_Cooperatives_StatusId",
                table: "Cooperatives",
                column: "StatusId");

            migrationBuilder.AddForeignKey(
                name: "FK_Cooperatives_Statuses_StatusId",
                table: "Cooperatives",
                column: "StatusId",
                principalTable: "Statuses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
