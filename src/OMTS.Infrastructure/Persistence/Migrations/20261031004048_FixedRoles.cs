using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <summary>Apply the approved fixed roles only after ambiguous legacy grants have been reviewed.</summary>
    public partial class FixedRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DECLARE @today date = CONVERT(date, SWITCHOFFSET(SYSDATETIMEOFFSET(), '+07:00'));
                IF EXISTS (
                    SELECT 1 FROM [AccessGrants] g JOIN [Roles] r ON r.[Id] = g.[RoleId]
                    WHERE g.[StatusId] = 501 AND
                        (g.[EffectiveFrom] > @today OR g.[EffectiveTo] < @today OR r.[StatusId] <> 101))
                    THROW 51021, 'FixedRoles migration stopped: legacy grant validity or discontinued role requires explicit review.', 1;
                IF EXISTS (
                    SELECT 1 FROM [AccessGrants] WHERE [StatusId] = 501
                    GROUP BY [AccountId], [RoleId], [ZoneId] HAVING COUNT(*) > 1)
                    THROW 51022, 'FixedRoles migration stopped: duplicate active grants require explicit review.', 1;
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_Roles_Statuses_StatusId",
                table: "Roles");

            migrationBuilder.DropTable(
                name: "RolePermissions");

            migrationBuilder.DropIndex(
                name: "IX_Roles_StatusId",
                table: "Roles");

            migrationBuilder.DropColumn(
                name: "StatusId",
                table: "Roles");

            migrationBuilder.DropColumn(
                name: "EffectiveFrom",
                table: "AccessGrants");

            migrationBuilder.DropColumn(
                name: "EffectiveTo",
                table: "AccessGrants");

            migrationBuilder.AddColumn<string>(
                name: "Code",
                table: "Roles",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "");

            // The default role rows of an existing database become the fixed roles. Any other row (a role the Owner
            // created) gets a unique placeholder code so the unique index can be built; it is no fixed role, so its pairs
            // give no permission, login or zone any more.
            migrationBuilder.Sql(
                "UPDATE [Roles] SET [Code] = N'ZONE_MANAGER' WHERE [Name] = N'Zone Manager';\n" +
                "UPDATE [Roles] SET [Code] = N'ACCOUNTANT' WHERE [Name] = N'Accountant';\n" +
                "UPDATE [Roles] SET [Code] = N'LEGACY-' + LEFT(CONVERT(nvarchar(36), [Id]), 23) WHERE [Code] = N'';");

            migrationBuilder.CreateIndex(
                name: "IX_Roles_Code",
                table: "Roles",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_AccessGrants_EffectivePair",
                table: "AccessGrants",
                columns: new[] { "AccountId", "RoleId", "ZoneId" },
                unique: true,
                filter: "[StatusId] = 501");
        }

        /// <summary>
        /// Lossy: the RolePermissions table comes back empty (the matrix was never stored), every role is In Use and every
        /// pair gets EffectiveFrom 0001-01-01 with no end. Environments re-seed instead of relying on it.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Roles_Code",
                table: "Roles");

            migrationBuilder.DropIndex(
                name: "UX_AccessGrants_EffectivePair",
                table: "AccessGrants");

            migrationBuilder.DropColumn(
                name: "Code",
                table: "Roles");

            migrationBuilder.AddColumn<int>(
                name: "StatusId",
                table: "Roles",
                type: "int",
                nullable: false,
                // Usage In Use, so the foreign key below finds its status.
                defaultValue: 101);

            migrationBuilder.AddColumn<DateOnly>(
                name: "EffectiveFrom",
                table: "AccessGrants",
                type: "date",
                nullable: false,
                defaultValue: new DateOnly(1, 1, 1));

            migrationBuilder.AddColumn<DateOnly>(
                name: "EffectiveTo",
                table: "AccessGrants",
                type: "date",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RolePermissions",
                columns: table => new
                {
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FunctionCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CanCreate = table.Column<bool>(type: "bit", nullable: false),
                    CanDelete = table.Column<bool>(type: "bit", nullable: false),
                    CanEdit = table.Column<bool>(type: "bit", nullable: false),
                    CanView = table.Column<bool>(type: "bit", nullable: false),
                    DataScope = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    OtherPermissions = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RolePermissions", x => new { x.RoleId, x.FunctionCode });
                    table.ForeignKey(
                        name: "FK_RolePermissions_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Roles_StatusId",
                table: "Roles",
                column: "StatusId");

            migrationBuilder.AddForeignKey(
                name: "FK_Roles_Statuses_StatusId",
                table: "Roles",
                column: "StatusId",
                principalTable: "Statuses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
