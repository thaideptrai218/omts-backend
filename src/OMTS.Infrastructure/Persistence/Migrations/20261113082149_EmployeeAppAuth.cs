using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EmployeeAppAuth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EmployeeLoginCodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CodeHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    IssuedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IssuedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    FailedAttempts = table.Column<int>(type: "int", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancelledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmployeeLoginCodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmployeeLoginCodes_Accounts_IssuedByAccountId",
                        column: x => x.IssuedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EmployeeLoginCodes_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EmployeeDevices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceSecretHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    PreviousDeviceSecretHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Platform = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    DeviceName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PushToken = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PushPermissionGranted = table.Column<bool>(type: "bit", nullable: false),
                    LinkedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LinkedByLoginCodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UnlinkedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UnlinkReason = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastQrStep = table.Column<long>(type: "bigint", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmployeeDevices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmployeeDevices_EmployeeLoginCodes_LinkedByLoginCodeId",
                        column: x => x.LinkedByLoginCodeId,
                        principalTable: "EmployeeLoginCodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EmployeeDevices_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EmployeeSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EmployeeDeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RefreshTokenHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    PreviousRefreshTokenHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    PreviousRefreshTokenGraceEndsAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastRefreshedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RefreshTokenExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AbsoluteExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RevokeReason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RevokeCause = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmployeeSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmployeeSessions_EmployeeDevices_EmployeeDeviceId",
                        column: x => x.EmployeeDeviceId,
                        principalTable: "EmployeeDevices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EmployeeSessions_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeDevices_LinkedByLoginCodeId",
                table: "EmployeeDevices",
                column: "LinkedByLoginCodeId");

            migrationBuilder.CreateIndex(
                name: "UX_EmployeeDevices_OneLinkedPerEmployee",
                table: "EmployeeDevices",
                column: "EmployeeId",
                unique: true,
                filter: "[UnlinkedAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeLoginCodes_IssuedByAccountId",
                table: "EmployeeLoginCodes",
                column: "IssuedByAccountId");

            migrationBuilder.CreateIndex(
                name: "UX_EmployeeLoginCodes_OneOpenPerEmployee",
                table: "EmployeeLoginCodes",
                column: "EmployeeId",
                unique: true,
                filter: "[ConsumedAt] IS NULL AND [CancelledAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeSessions_EmployeeDeviceId",
                table: "EmployeeSessions",
                column: "EmployeeDeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeSessions_EmployeeId_RevokedAt",
                table: "EmployeeSessions",
                columns: new[] { "EmployeeId", "RevokedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeSessions_PreviousRefreshTokenHash",
                table: "EmployeeSessions",
                column: "PreviousRefreshTokenHash",
                filter: "[PreviousRefreshTokenHash] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeSessions_RefreshTokenHash",
                table: "EmployeeSessions",
                column: "RefreshTokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmployeeSessions");

            migrationBuilder.DropTable(
                name: "EmployeeDevices");

            migrationBuilder.DropTable(
                name: "EmployeeLoginCodes");
        }
    }
}
