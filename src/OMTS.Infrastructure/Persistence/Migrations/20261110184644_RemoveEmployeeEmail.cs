using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveEmployeeEmail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Email",
                table: "Employees");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Email",
                table: "Employees",
                type: "nvarchar(254)",
                maxLength: 254,
                nullable: true);
        }
    }
}
