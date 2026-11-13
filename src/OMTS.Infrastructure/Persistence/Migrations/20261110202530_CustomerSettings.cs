using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CustomerSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CustomerAllowMoreDebtOverLimit",
                table: "Cooperatives",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "CustomerDebtWarningEnabled",
                table: "Cooperatives",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "CustomerMaxDebtAmount",
                table: "Cooperatives",
                type: "decimal(18,0)",
                precision: 18,
                scale: 0,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CustomerMaxDebtDays",
                table: "Cooperatives",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CustomerNoDebtForWalkIn",
                table: "Cooperatives",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "CustomerRequireAddress",
                table: "Cooperatives",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CustomerRequireBank",
                table: "Cooperatives",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CustomerRequireBirthDate",
                table: "Cooperatives",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CustomerRequireEmail",
                table: "Cooperatives",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CustomerRequireFacebook",
                table: "Cooperatives",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CustomerRequireGender",
                table: "Cooperatives",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CustomerRequirePhoneNumber2",
                table: "Cooperatives",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CustomerRequireTaxCode",
                table: "Cooperatives",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Cooperative_CustomerMaxDebtAmount",
                table: "Cooperatives",
                sql: "[CustomerMaxDebtAmount] IS NULL OR [CustomerMaxDebtAmount] > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Cooperative_CustomerMaxDebtDays",
                table: "Cooperatives",
                sql: "[CustomerMaxDebtDays] IS NULL OR [CustomerMaxDebtDays] >= 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Cooperative_CustomerMaxDebtAmount",
                table: "Cooperatives");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Cooperative_CustomerMaxDebtDays",
                table: "Cooperatives");

            migrationBuilder.DropColumn(
                name: "CustomerAllowMoreDebtOverLimit",
                table: "Cooperatives");

            migrationBuilder.DropColumn(
                name: "CustomerDebtWarningEnabled",
                table: "Cooperatives");

            migrationBuilder.DropColumn(
                name: "CustomerMaxDebtAmount",
                table: "Cooperatives");

            migrationBuilder.DropColumn(
                name: "CustomerMaxDebtDays",
                table: "Cooperatives");

            migrationBuilder.DropColumn(
                name: "CustomerNoDebtForWalkIn",
                table: "Cooperatives");

            migrationBuilder.DropColumn(
                name: "CustomerRequireAddress",
                table: "Cooperatives");

            migrationBuilder.DropColumn(
                name: "CustomerRequireBank",
                table: "Cooperatives");

            migrationBuilder.DropColumn(
                name: "CustomerRequireBirthDate",
                table: "Cooperatives");

            migrationBuilder.DropColumn(
                name: "CustomerRequireEmail",
                table: "Cooperatives");

            migrationBuilder.DropColumn(
                name: "CustomerRequireFacebook",
                table: "Cooperatives");

            migrationBuilder.DropColumn(
                name: "CustomerRequireGender",
                table: "Cooperatives");

            migrationBuilder.DropColumn(
                name: "CustomerRequirePhoneNumber2",
                table: "Cooperatives");

            migrationBuilder.DropColumn(
                name: "CustomerRequireTaxCode",
                table: "Cooperatives");
        }
    }
}
