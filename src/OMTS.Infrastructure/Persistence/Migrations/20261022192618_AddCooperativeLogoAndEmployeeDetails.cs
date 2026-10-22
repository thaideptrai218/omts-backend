using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCooperativeLogoAndEmployeeDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BankAccount_AccountHolderName",
                table: "Employees",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BankAccount_AccountNumber",
                table: "Employees",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BankAccount_BankName",
                table: "Employees",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "EmploymentEndDate",
                table: "Employees",
                type: "date",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "LegalRepresentative",
                table: "Cooperatives",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(200)",
                oldMaxLength: 200,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "FullName",
                table: "Cooperatives",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(200)",
                oldMaxLength: 200);

            migrationBuilder.AddColumn<string>(
                name: "LogoUrl",
                table: "Cooperatives",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "EmployeeWorkingZones",
                columns: table => new
                {
                    EmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ZoneId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmployeeWorkingZones", x => new { x.EmployeeId, x.ZoneId });
                    table.ForeignKey(
                        name: "FK_EmployeeWorkingZones_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EmployeeWorkingZones_FarmingZones_ZoneId",
                        column: x => x.ZoneId,
                        principalTable: "FarmingZones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeWorkingZones_ZoneId",
                table: "EmployeeWorkingZones",
                column: "ZoneId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmployeeWorkingZones");

            migrationBuilder.DropColumn(
                name: "BankAccount_AccountHolderName",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "BankAccount_AccountNumber",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "BankAccount_BankName",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "EmploymentEndDate",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "LogoUrl",
                table: "Cooperatives");

            migrationBuilder.AlterColumn<string>(
                name: "LegalRepresentative",
                table: "Cooperatives",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(255)",
                oldMaxLength: 255,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "FullName",
                table: "Cooperatives",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(255)",
                oldMaxLength: 255);
        }
    }
}
