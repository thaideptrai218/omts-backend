using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PurchaseRequestsInCages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The Mini App's own table was copied into PurchaseRequests by the integration step. It stays as an archive, without the
            // constraint names the model needs on PurchaseRequests.
            migrationBuilder.Sql("""
                IF OBJECT_ID(N'dbo.PurchaseRequest', N'U') IS NOT NULL
                BEGIN
                    IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE [name] = N'CK_PurchaseRequest_Cages' AND [parent_object_id] = OBJECT_ID(N'dbo.PurchaseRequest'))
                        ALTER TABLE [PurchaseRequest] DROP CONSTRAINT [CK_PurchaseRequest_Cages];
                    IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE [name] = N'CK_PurchaseRequest_Staffing' AND [parent_object_id] = OBJECT_ID(N'dbo.PurchaseRequest'))
                        ALTER TABLE [PurchaseRequest] DROP CONSTRAINT [CK_PurchaseRequest_Staffing];
                    EXEC sp_rename N'dbo.PurchaseRequest', N'MiniAppLegacyPurchaseRequest';
                END;
                """);

            migrationBuilder.Sql("IF OBJECT_ID(N'dbo.CK_PurchaseRequest_Accepted_Has_Response', N'C') IS NOT NULL ALTER TABLE [PurchaseRequests] DROP CONSTRAINT [CK_PurchaseRequest_Accepted_Has_Response];");

            migrationBuilder.Sql("IF OBJECT_ID(N'dbo.CK_PurchaseRequest_AcceptedKg_Range', N'C') IS NOT NULL ALTER TABLE [PurchaseRequests] DROP CONSTRAINT [CK_PurchaseRequest_AcceptedKg_Range];");

            migrationBuilder.Sql("IF OBJECT_ID(N'dbo.CK_PurchaseRequest_Cages', N'C') IS NOT NULL ALTER TABLE [PurchaseRequests] DROP CONSTRAINT [CK_PurchaseRequest_Cages];");

            migrationBuilder.Sql("IF OBJECT_ID(N'dbo.CK_PurchaseRequest_RequestedKg_Positive', N'C') IS NOT NULL ALTER TABLE [PurchaseRequests] DROP CONSTRAINT [CK_PurchaseRequest_RequestedKg_Positive];");

            // Requests entered in kg become whole cages at the cooperative's own rate, rounded up so no order shrinks. The two sets of
            // response columns (web and Mini App) become one.
            migrationBuilder.Sql("""
                DECLARE @kgPerCage decimal(9,2) = (SELECT TOP 1 NULLIF([AverageKgPerCage], 0) FROM [Cooperatives]);
                SET @kgPerCage = ISNULL(@kgPerCage, 50);
                UPDATE r SET
                    [RequestedCages] = CASE WHEN r.[RequestedCages] = 0 THEN CAST(CEILING(r.[RequestedKg] / @kgPerCage) AS int) ELSE r.[RequestedCages] END,
                    [AcceptedCages] = CASE WHEN r.[AcceptedCages] IS NULL AND r.[AcceptedKg] IS NOT NULL THEN CAST(CEILING(r.[AcceptedKg] / @kgPerCage) AS int) ELSE r.[AcceptedCages] END,
                    [AgreedPickupDate] = COALESCE(r.[AgreedPickupDate], r.[AcceptedPickupDate]),
                    [PayLaterAgreed] = CASE WHEN r.[StatusId] = 3202 THEN COALESCE(r.[PayLaterAgreed], r.[AllowPayLater]) ELSE r.[PayLaterAgreed] END,
                    [CancelReason] = COALESCE(r.[CancelReason], r.[CancellationReason]),
                    [SentAt] = CASE WHEN r.[SentAt] < '2000-01-01' THEN r.[CreatedAt] ELSE r.[SentAt] END,
                    [ProductName] = CASE WHEN r.[ProductName] = N'' THEN p.[Name] ELSE r.[ProductName] END,
                    [Code] = CASE WHEN r.[Code] = N'' THEN N'YC-' + CONVERT(nchar(8), r.[CreatedAt], 112) + N'-' + UPPER(LEFT(REPLACE(CONVERT(nvarchar(36), r.[Id]), N'-', N''), 12)) ELSE r.[Code] END
                FROM [PurchaseRequests] r
                JOIN [Products] p ON p.[Id] = r.[ProductId];
                """);

            migrationBuilder.DropColumn(
                name: "AcceptedKg",
                table: "PurchaseRequests");

            migrationBuilder.DropColumn(
                name: "AcceptedPickupDate",
                table: "PurchaseRequests");

            migrationBuilder.DropColumn(
                name: "AllowPayLater",
                table: "PurchaseRequests");

            migrationBuilder.DropColumn(
                name: "CancellationReason",
                table: "PurchaseRequests");

            migrationBuilder.DropColumn(
                name: "RequestedKg",
                table: "PurchaseRequests");

            migrationBuilder.UpdateData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 180,
                column: "GuardCondition",
                value: "The accepted cages are above 0 and not more than the requested cages; the pickup date is not before today.");

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 303,
                column: "IsInitial",
                value: false);

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3202,
                column: "Meaning",
                value: "Accepted by the Cooperative Owner, possibly for fewer cages; invoices may be linked to it.");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PurchaseRequest_Accepted_Has_Response",
                table: "PurchaseRequests",
                sql: "[StatusId] <> 3202 OR ([AcceptedCages] IS NOT NULL AND [AgreedPickupDate] IS NOT NULL AND [PayLaterAgreed] IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PurchaseRequest_Cages",
                table: "PurchaseRequests",
                sql: "[RequestedCages] > 0 AND ([AcceptedCages] IS NULL OR ([AcceptedCages] > 0 AND [AcceptedCages] <= [RequestedCages]))");

            migrationBuilder.Sql("IF OBJECT_ID(N'dbo.CK_PurchaseRequest_Declined_Has_Note', N'C') IS NULL ALTER TABLE [PurchaseRequests] ADD CONSTRAINT [CK_PurchaseRequest_Declined_Has_Note] CHECK ([StatusId] <> 3203 OR [ResponseNote] IS NOT NULL);");
            migrationBuilder.Sql("IF OBJECT_ID(N'dbo.CK_PurchaseRequest_Staffing', N'C') IS NULL ALTER TABLE [PurchaseRequests] ADD CONSTRAINT [CK_PurchaseRequest_Staffing] CHECK (([PlannedCages] IS NULL AND [WorkDate] IS NULL AND [PlannedPickupDate] IS NULL AND [CagesPerEmployeePerDay] IS NULL) OR ([PlannedCages] IS NOT NULL AND [PlannedCages] > 0 AND [PlannedCages] <= [RequestedCages] AND [WorkDate] IS NOT NULL AND [PlannedPickupDate] IS NOT NULL AND [WorkDate] <= [PlannedPickupDate] AND [CagesPerEmployeePerDay] IS NOT NULL AND [CagesPerEmployeePerDay] > 0));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF OBJECT_ID(N'dbo.CK_PurchaseRequest_Accepted_Has_Response', N'C') IS NOT NULL ALTER TABLE [PurchaseRequests] DROP CONSTRAINT [CK_PurchaseRequest_Accepted_Has_Response];");

            migrationBuilder.Sql("IF OBJECT_ID(N'dbo.CK_PurchaseRequest_Cages', N'C') IS NOT NULL ALTER TABLE [PurchaseRequests] DROP CONSTRAINT [CK_PurchaseRequest_Cages];");

            migrationBuilder.AddColumn<decimal>(
                name: "AcceptedKg",
                table: "PurchaseRequests",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "AcceptedPickupDate",
                table: "PurchaseRequests",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AllowPayLater",
                table: "PurchaseRequests",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "CancellationReason",
                table: "PurchaseRequests",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RequestedKg",
                table: "PurchaseRequests",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.UpdateData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 180,
                column: "GuardCondition",
                value: "The accepted kg is above 0 and not more than the requested kg; the pickup date is not before today.");

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 303,
                column: "IsInitial",
                value: true);

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3202,
                column: "Meaning",
                value: "Accepted by the Cooperative Owner, possibly for fewer kg; invoices may be linked to it.");

            // Whole cages stay in the cage columns; only the response columns of the Mini App are filled again.
            migrationBuilder.Sql("""
                UPDATE [PurchaseRequests] SET
                    [AcceptedPickupDate] = [AgreedPickupDate],
                    [AllowPayLater] = ISNULL([PayLaterAgreed], 0),
                    [CancellationReason] = [CancelReason];
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_PurchaseRequest_Accepted_Has_Response",
                table: "PurchaseRequests",
                sql: "[StatusId] <> 3202 OR ([RequestedCages] > 0 AND [AcceptedCages] IS NOT NULL AND [AcceptedPickupDate] IS NOT NULL) OR ([RequestedCages] = 0 AND [AcceptedKg] IS NOT NULL AND [AgreedPickupDate] IS NOT NULL AND [PayLaterAgreed] IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PurchaseRequest_AcceptedKg_Range",
                table: "PurchaseRequests",
                sql: "[AcceptedKg] IS NULL OR ([RequestedCages] = 0 AND [AcceptedKg] > 0 AND [AcceptedKg] <= [RequestedKg])");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PurchaseRequest_Cages",
                table: "PurchaseRequests",
                sql: "(([RequestedCages] > 0 AND [RequestedKg] = 0) OR ([RequestedCages] = 0 AND [RequestedKg] > 0)) AND ([AcceptedCages] IS NULL OR ([AcceptedCages] > 0 AND [AcceptedCages] <= [RequestedCages]))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PurchaseRequest_RequestedKg_Positive",
                table: "PurchaseRequests",
                sql: "[RequestedCages] > 0 OR [RequestedKg] > 0");
        }
    }
}
