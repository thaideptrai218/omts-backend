using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkDayPlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // EmployeesV2 retires the legacy parent on fresh installs; the final integration recreates the plan there.
            migrationBuilder.Sql("""
                IF OBJECT_ID(N'dbo.WorkAssignments', N'U') IS NOT NULL
                BEGIN
                ALTER TABLE [WorkAssignments] ADD [WorkDayPlanId] uniqueidentifier NULL;

                CREATE TABLE [WorkDayPlan] (
                    [Id] uniqueidentifier NOT NULL,
                    [CreatorAccountId] uniqueidentifier NOT NULL,
                    [ResponsibleAccountId] uniqueidentifier NOT NULL,
                    [RaftId] uniqueidentifier NOT NULL,
                    [JobTypeId] uniqueidentifier NOT NULL,
                    [WorkDate] date NOT NULL,
                    [PurchaseRequestId] uniqueidentifier NULL,
                    [ClientOperationId] uniqueidentifier NOT NULL,
                    [Note] nvarchar(500) NULL,
                    [Approved] bit NULL,
                    [RespondedAt] datetimeoffset NULL,
                    [ResponseReason] nvarchar(500) NULL,
                    [CreatedAt] datetimeoffset NOT NULL,
                    [CreatedBy] uniqueidentifier NULL,
                    [UpdatedAt] datetimeoffset NULL,
                    [UpdatedBy] uniqueidentifier NULL,
                    [RowVersion] rowversion NOT NULL,
                    CONSTRAINT [PK_WorkDayPlan] PRIMARY KEY ([Id]),
                    CONSTRAINT [CK_WorkDayPlan_Response] CHECK (([Approved] IS NULL AND [RespondedAt] IS NULL) OR ([Approved] IS NOT NULL AND [RespondedAt] IS NOT NULL AND ([Approved] = 1 OR ([ResponseReason] IS NOT NULL AND LEN(LTRIM(RTRIM([ResponseReason]))) > 0)))),
                    CONSTRAINT [FK_WorkDayPlan_Accounts_CreatorAccountId] FOREIGN KEY ([CreatorAccountId]) REFERENCES [Accounts] ([Id]) ON DELETE NO ACTION,
                    CONSTRAINT [FK_WorkDayPlan_Accounts_ResponsibleAccountId] FOREIGN KEY ([ResponsibleAccountId]) REFERENCES [Accounts] ([Id]) ON DELETE NO ACTION,
                    CONSTRAINT [FK_WorkDayPlan_JobType_JobTypeId] FOREIGN KEY ([JobTypeId]) REFERENCES [JobType] ([Id]) ON DELETE NO ACTION,
                    CONSTRAINT [FK_WorkDayPlan_PurchaseRequest_PurchaseRequestId] FOREIGN KEY ([PurchaseRequestId]) REFERENCES [PurchaseRequest] ([Id]) ON DELETE NO ACTION,
                    CONSTRAINT [FK_WorkDayPlan_Rafts_RaftId] FOREIGN KEY ([RaftId]) REFERENCES [Rafts] ([Id]) ON DELETE NO ACTION
                );

                CREATE INDEX [IX_WorkAssignments_WorkDayPlanId] ON [WorkAssignments] ([WorkDayPlanId]);

                CREATE UNIQUE INDEX [IX_WorkDayPlan_CreatorAccountId_ClientOperationId] ON [WorkDayPlan] ([CreatorAccountId], [ClientOperationId]);

                CREATE INDEX [IX_WorkDayPlan_JobTypeId] ON [WorkDayPlan] ([JobTypeId]);

                CREATE UNIQUE INDEX [IX_WorkDayPlan_PurchaseRequestId] ON [WorkDayPlan] ([PurchaseRequestId]) WHERE [PurchaseRequestId] IS NOT NULL;

                CREATE INDEX [IX_WorkDayPlan_RaftId] ON [WorkDayPlan] ([RaftId]);

                CREATE INDEX [IX_WorkDayPlan_ResponsibleAccountId_WorkDate] ON [WorkDayPlan] ([ResponsibleAccountId], [WorkDate]);

                ALTER TABLE [WorkAssignments] ADD CONSTRAINT [FK_WorkAssignments_WorkDayPlan_WorkDayPlanId] FOREIGN KEY ([WorkDayPlanId]) REFERENCES [WorkDayPlan] ([Id]) ON DELETE NO ACTION;
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WorkAssignments_WorkDayPlan_WorkDayPlanId",
                table: "WorkAssignments");

            migrationBuilder.DropTable(
                name: "WorkDayPlan");

            migrationBuilder.DropIndex(
                name: "IX_WorkAssignments_WorkDayPlanId",
                table: "WorkAssignments");

            migrationBuilder.DropColumn(
                name: "WorkDayPlanId",
                table: "WorkAssignments");
        }
    }
}
