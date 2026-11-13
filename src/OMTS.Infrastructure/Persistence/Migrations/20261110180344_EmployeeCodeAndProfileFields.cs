using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EmployeeCodeAndProfileFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Employees_CooperativeId",
                table: "Employees");

            migrationBuilder.AddColumn<string>(
                name: "Code",
                table: "Employees",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Email",
                table: "Employees",
                type: "nvarchar(254)",
                maxLength: 254,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Gender",
                table: "Employees",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdNumber",
                table: "Employees",
                type: "nvarchar(12)",
                maxLength: 12,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "StartDate",
                table: "Employees",
                type: "date",
                nullable: true);

            // Existing employees take codes in the order they were created, and the NV counter continues after them.
            migrationBuilder.Sql(
                """
                UPDATE e SET e.Code = n.Code
                FROM Employees e
                JOIN (SELECT Id, N'NV' + FORMAT(ROW_NUMBER() OVER (ORDER BY CreatedAt, Id), '000000') AS Code
                      FROM Employees) n ON n.Id = e.Id;
                DELETE FROM DocumentSequences WHERE [Key] = N'NV';
                INSERT INTO DocumentSequences ([Key], LastValue)
                SELECT N'NV', COUNT_BIG(*) FROM Employees;
                """);

            migrationBuilder.CreateIndex(
                name: "UX_Employees_Cooperative_Code",
                table: "Employees",
                columns: new[] { "CooperativeId", "Code" },
                unique: true,
                filter: "[Code] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM DocumentSequences WHERE [Key] = N'NV';");

            migrationBuilder.DropIndex(
                name: "UX_Employees_Cooperative_Code",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "Code",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "Email",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "Gender",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "IdNumber",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "StartDate",
                table: "Employees");

            migrationBuilder.CreateIndex(
                name: "IX_Employees_CooperativeId",
                table: "Employees",
                column: "CooperativeId");
        }
    }
}
