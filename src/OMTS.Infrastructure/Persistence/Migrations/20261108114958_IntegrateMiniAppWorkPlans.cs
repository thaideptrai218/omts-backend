using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IntegrateMiniAppWorkPlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""

                IF OBJECT_ID(N'dbo.JobType', N'U') IS NOT NULL
                BEGIN
                    UPDATE j SET [Name] = LEFT(j.[Name], 160) + N' (mobile ' + CONVERT(nvarchar(36), j.[Id]) + N')'
                    FROM [JobType] j WHERE EXISTS (SELECT 1 FROM [JobTypes] n WHERE n.[Name] = j.[Name] AND n.[Id] <> j.[Id]);
                    INSERT INTO [JobTypes] ([Id], [Name], [StatusId], [CreatedAt], [CreatedBy], [UpdatedAt], [UpdatedBy])
                    SELECT j.[Id], j.[Name], j.[StatusId], j.[CreatedAt], j.[CreatedBy], j.[UpdatedAt], j.[UpdatedBy]
                    FROM [JobType] j WHERE NOT EXISTS (SELECT 1 FROM [JobTypes] n WHERE n.[Id] = j.[Id]);
                END;
                """);

            migrationBuilder.Sql("""
                ALTER TABLE [PurchaseRequests] DROP CONSTRAINT [CK_PurchaseRequest_Accepted_Has_Response];
                """);

            migrationBuilder.Sql("""
                ALTER TABLE [PurchaseRequests] DROP CONSTRAINT [CK_PurchaseRequest_AcceptedKg_Range];
                """);

            migrationBuilder.Sql("""
                ALTER TABLE [PurchaseRequests] DROP CONSTRAINT [CK_PurchaseRequest_RequestedKg_Positive];
                """);

            migrationBuilder.Sql("""
                ALTER TABLE [Products] DROP CONSTRAINT [CK_Product_UnitPrice_Positive];
                """);

            migrationBuilder.Sql("""
                IF COL_LENGTH(N'dbo.PurchaseRequests',N'AcceptedCages') IS NULL
                ALTER TABLE [PurchaseRequests] ADD [AcceptedCages] int NULL;
                """);

            migrationBuilder.Sql("""
                IF COL_LENGTH(N'dbo.PurchaseRequests',N'AcceptedPickupDate') IS NULL
                ALTER TABLE [PurchaseRequests] ADD [AcceptedPickupDate] date NULL;
                """);

            migrationBuilder.Sql("""
                IF COL_LENGTH(N'dbo.PurchaseRequests',N'AllowPayLater') IS NULL
                ALTER TABLE [PurchaseRequests] ADD [AllowPayLater] bit NOT NULL DEFAULT CAST(0 AS bit);
                """);

            migrationBuilder.Sql("""
                IF COL_LENGTH(N'dbo.PurchaseRequests',N'CagesPerEmployeePerDay') IS NULL
                ALTER TABLE [PurchaseRequests] ADD [CagesPerEmployeePerDay] int NULL;
                """);

            migrationBuilder.Sql("""
                IF COL_LENGTH(N'dbo.PurchaseRequests',N'CancellationReason') IS NULL
                ALTER TABLE [PurchaseRequests] ADD [CancellationReason] nvarchar(1000) NULL;
                """);

            migrationBuilder.Sql("""
                IF COL_LENGTH(N'dbo.PurchaseRequests',N'ClientOperationId') IS NULL
                ALTER TABLE [PurchaseRequests] ADD [ClientOperationId] uniqueidentifier NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000';
                """);

            migrationBuilder.Sql("""
                IF COL_LENGTH(N'dbo.PurchaseRequests',N'Code') IS NULL
                ALTER TABLE [PurchaseRequests] ADD [Code] nvarchar(50) NOT NULL DEFAULT N'';
                """);

            migrationBuilder.Sql("""
                IF COL_LENGTH(N'dbo.PurchaseRequests',N'PlannedCages') IS NULL
                ALTER TABLE [PurchaseRequests] ADD [PlannedCages] int NULL;
                """);

            migrationBuilder.Sql("""
                IF COL_LENGTH(N'dbo.PurchaseRequests',N'PlannedPickupDate') IS NULL
                ALTER TABLE [PurchaseRequests] ADD [PlannedPickupDate] date NULL;
                """);

            migrationBuilder.Sql("""
                IF COL_LENGTH(N'dbo.PurchaseRequests',N'ProductName') IS NULL
                ALTER TABLE [PurchaseRequests] ADD [ProductName] nvarchar(200) NOT NULL DEFAULT N'';
                """);

            migrationBuilder.Sql("""
                IF COL_LENGTH(N'dbo.PurchaseRequests',N'RequestedCages') IS NULL
                ALTER TABLE [PurchaseRequests] ADD [RequestedCages] int NOT NULL DEFAULT 0;
                """);

            migrationBuilder.Sql("""
                IF COL_LENGTH(N'dbo.PurchaseRequests',N'StaffingNote') IS NULL
                ALTER TABLE [PurchaseRequests] ADD [StaffingNote] nvarchar(500) NULL;
                """);

            migrationBuilder.Sql("""
                IF COL_LENGTH(N'dbo.PurchaseRequests',N'WorkDate') IS NULL
                ALTER TABLE [PurchaseRequests] ADD [WorkDate] date NULL;
                """);

            migrationBuilder.Sql("""
                IF COL_LENGTH(N'dbo.Products',N'Code') IS NULL
                ALTER TABLE [Products] ADD [Code] nvarchar(50) NOT NULL DEFAULT N'';
                """);

            migrationBuilder.Sql("""
                IF COL_LENGTH(N'dbo.Products',N'Description') IS NULL
                ALTER TABLE [Products] ADD [Description] nvarchar(2000) NULL;
                """);

            migrationBuilder.Sql("""
                IF COL_LENGTH(N'dbo.Products',N'ValidFrom') IS NULL
                ALTER TABLE [Products] ADD [ValidFrom] date NULL;
                """);

            migrationBuilder.Sql("""
                IF COL_LENGTH(N'dbo.Products',N'ValidTo') IS NULL
                ALTER TABLE [Products] ADD [ValidTo] date NULL;
                """);

            migrationBuilder.Sql("""
                IF COL_LENGTH(N'dbo.Accounts',N'AccessRequestNote') IS NULL
                ALTER TABLE [Accounts] ADD [AccessRequestNote] nvarchar(500) NULL;
                """);

            migrationBuilder.Sql("""
                IF COL_LENGTH(N'dbo.Accounts',N'AccessRequestedAt') IS NULL
                ALTER TABLE [Accounts] ADD [AccessRequestedAt] datetimeoffset NULL;
                """);

            migrationBuilder.Sql("""
                IF COL_LENGTH(N'dbo.Accounts',N'RejectionReason') IS NULL
                ALTER TABLE [Accounts] ADD [RejectionReason] nvarchar(1000) NULL;
                """);

            migrationBuilder.Sql("""
                IF COL_LENGTH(N'dbo.Accounts',N'ZaloAccountId') IS NULL
                ALTER TABLE [Accounts] ADD [ZaloAccountId] nvarchar(100) NULL;
                """);

            migrationBuilder.Sql("""
                IF OBJECT_ID(N'dbo.AttendanceQr', N'U') IS NULL
                BEGIN
                CREATE TABLE [AttendanceQr] (
                    [Id] uniqueidentifier NOT NULL,
                    [EmployeeId] uniqueidentifier NOT NULL,
                    [TokenHash] nvarchar(64) NOT NULL,
                    [ExpiresAt] datetimeoffset NOT NULL,
                    CONSTRAINT [PK_AttendanceQr] PRIMARY KEY ([Id]),
                    CONSTRAINT [FK_AttendanceQr_Employees_EmployeeId] FOREIGN KEY ([EmployeeId]) REFERENCES [Employees] ([Id]) ON DELETE NO ACTION
                );
                END;
                """);

            migrationBuilder.Sql("""
                IF OBJECT_ID(N'dbo.AttendanceRecords', N'U') IS NULL
                BEGIN
                CREATE TABLE [AttendanceRecords] (
                    [Id] uniqueidentifier NOT NULL,
                    [EmployeeId] uniqueidentifier NULL,
                    [WorkerId] uniqueidentifier NULL,
                    [ContractorId] uniqueidentifier NULL,
                    [WorkDate] date NOT NULL,
                    [ZoneId] uniqueidentifier NULL,
                    [CheckedInAt] datetimeoffset NULL,
                    [CheckedOutAt] datetimeoffset NULL,
                    [CheckInMethod] nvarchar(30) NULL,
                    [CheckOutMethod] nvarchar(30) NULL,
                    [Result] nvarchar(50) NOT NULL,
                    [Notes] nvarchar(1000) NULL,
                    [RecordedByAccountId] uniqueidentifier NOT NULL,
                    [EvidenceUrls] nvarchar(max) NOT NULL,
                    [CreatedAt] datetimeoffset NOT NULL,
                    [CreatedBy] uniqueidentifier NULL,
                    [UpdatedAt] datetimeoffset NULL,
                    [UpdatedBy] uniqueidentifier NULL,
                    [RowVersion] rowversion NOT NULL,
                    CONSTRAINT [PK_AttendanceRecords] PRIMARY KEY ([Id]),
                    CONSTRAINT [CK_AttendanceRecord_OnePerson] CHECK (([EmployeeId] IS NOT NULL AND [WorkerId] IS NULL) OR ([EmployeeId] IS NULL AND [WorkerId] IS NOT NULL)),
                    CONSTRAINT [CK_AttendanceRecord_Times] CHECK ([CheckedInAt] IS NULL OR [CheckedOutAt] IS NULL OR [CheckedOutAt] > [CheckedInAt]),
                    CONSTRAINT [FK_AttendanceRecords_Accounts_RecordedByAccountId] FOREIGN KEY ([RecordedByAccountId]) REFERENCES [Accounts] ([Id]) ON DELETE NO ACTION,
                    CONSTRAINT [FK_AttendanceRecords_Employees_EmployeeId] FOREIGN KEY ([EmployeeId]) REFERENCES [Employees] ([Id]) ON DELETE NO ACTION,
                    CONSTRAINT [FK_AttendanceRecords_FarmingZones_ZoneId] FOREIGN KEY ([ZoneId]) REFERENCES [FarmingZones] ([Id]) ON DELETE NO ACTION
                );
                END;
                """);

            migrationBuilder.Sql("""
                IF OBJECT_ID(N'dbo.EmployeeLeaveRequest', N'U') IS NULL
                BEGIN
                CREATE TABLE [EmployeeLeaveRequest] (
                    [Id] uniqueidentifier NOT NULL,
                    [EmployeeId] uniqueidentifier NOT NULL,
                    [From] date NOT NULL,
                    [To] date NOT NULL,
                    [Reason] nvarchar(500) NOT NULL,
                    [Approved] bit NULL,
                    [ReviewedAt] datetimeoffset NULL,
                    [CancelledAt] datetimeoffset NULL,
                    [ReviewedByAccountId] uniqueidentifier NULL,
                    [Response] nvarchar(500) NULL,
                    [ClientOperationId] uniqueidentifier NOT NULL,
                    [CreatedAt] datetimeoffset NOT NULL,
                    [CreatedBy] uniqueidentifier NULL,
                    [UpdatedAt] datetimeoffset NULL,
                    [UpdatedBy] uniqueidentifier NULL,
                    [RowVersion] rowversion NOT NULL,
                    CONSTRAINT [PK_EmployeeLeaveRequest] PRIMARY KEY ([Id]),
                    CONSTRAINT [CK_EmployeeLeaveRequest_Dates] CHECK ([To] >= [From]),
                    CONSTRAINT [FK_EmployeeLeaveRequest_Accounts_ReviewedByAccountId] FOREIGN KEY ([ReviewedByAccountId]) REFERENCES [Accounts] ([Id]) ON DELETE NO ACTION,
                    CONSTRAINT [FK_EmployeeLeaveRequest_Employees_EmployeeId] FOREIGN KEY ([EmployeeId]) REFERENCES [Employees] ([Id]) ON DELETE NO ACTION
                );
                END;
                """);

            migrationBuilder.Sql("""
                IF OBJECT_ID(N'dbo.PurchaseShiftInvitation', N'U') IS NULL
                BEGIN
                CREATE TABLE [PurchaseShiftInvitation] (
                    [Id] uniqueidentifier NOT NULL,
                    [PurchaseRequestId] uniqueidentifier NOT NULL,
                    [EmployeeId] uniqueidentifier NOT NULL,
                    [Approved] bit NULL,
                    [Reason] nvarchar(500) NULL,
                    [RespondedAt] datetimeoffset NULL,
                    [ReservedWorkDate] date NULL,
                    [CreatedAt] datetimeoffset NOT NULL,
                    [CreatedBy] uniqueidentifier NULL,
                    [UpdatedAt] datetimeoffset NULL,
                    [UpdatedBy] uniqueidentifier NULL,
                    [RowVersion] rowversion NOT NULL,
                    CONSTRAINT [PK_PurchaseShiftInvitation] PRIMARY KEY ([Id]),
                    CONSTRAINT [CK_PurchaseShiftInvitation_Reservation] CHECK ([ReservedWorkDate] IS NULL OR ([Approved] IS NOT NULL AND [Approved] = 1 AND [RespondedAt] IS NOT NULL)),
                    CONSTRAINT [FK_PurchaseShiftInvitation_Employees_EmployeeId] FOREIGN KEY ([EmployeeId]) REFERENCES [Employees] ([Id]) ON DELETE NO ACTION,
                    CONSTRAINT [FK_PurchaseShiftInvitation_PurchaseRequests_PurchaseRequestId] FOREIGN KEY ([PurchaseRequestId]) REFERENCES [PurchaseRequests] ([Id]) ON DELETE NO ACTION
                );
                END;
                """);

            migrationBuilder.Sql("""
                IF OBJECT_ID(N'dbo.WorkDayPlan', N'U') IS NULL
                BEGIN
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
                    CONSTRAINT [FK_WorkDayPlan_JobTypes_JobTypeId] FOREIGN KEY ([JobTypeId]) REFERENCES [JobTypes] ([Id]) ON DELETE NO ACTION,
                    CONSTRAINT [FK_WorkDayPlan_PurchaseRequests_PurchaseRequestId] FOREIGN KEY ([PurchaseRequestId]) REFERENCES [PurchaseRequests] ([Id]) ON DELETE NO ACTION,
                    CONSTRAINT [FK_WorkDayPlan_Rafts_RaftId] FOREIGN KEY ([RaftId]) REFERENCES [Rafts] ([Id]) ON DELETE NO ACTION
                );
                END;
                """);

            migrationBuilder.Sql("""
                IF OBJECT_ID(N'dbo.AttendanceJobPeriod', N'U') IS NULL
                BEGIN
                CREATE TABLE [AttendanceJobPeriod] (
                    [Id] uniqueidentifier NOT NULL,
                    [AttendanceId] uniqueidentifier NOT NULL,
                    [JobTypeId] uniqueidentifier NOT NULL,
                    [RaftId] uniqueidentifier NOT NULL,
                    [StartsAt] datetimeoffset NOT NULL,
                    CONSTRAINT [PK_AttendanceJobPeriod] PRIMARY KEY ([Id]),
                    CONSTRAINT [FK_AttendanceJobPeriod_AttendanceRecords_AttendanceId] FOREIGN KEY ([AttendanceId]) REFERENCES [AttendanceRecords] ([Id]) ON DELETE NO ACTION,
                    CONSTRAINT [FK_AttendanceJobPeriod_JobTypes_JobTypeId] FOREIGN KEY ([JobTypeId]) REFERENCES [JobTypes] ([Id]) ON DELETE NO ACTION,
                    CONSTRAINT [FK_AttendanceJobPeriod_Rafts_RaftId] FOREIGN KEY ([RaftId]) REFERENCES [Rafts] ([Id]) ON DELETE NO ACTION
                );
                END;
                """);

            migrationBuilder.Sql("""
                IF OBJECT_ID(N'dbo.WorkAssignments', N'U') IS NULL
                BEGIN
                CREATE TABLE [WorkAssignments] (
                    [Id] uniqueidentifier NOT NULL,
                    [WorkDayPlanId] uniqueidentifier NULL,
                    [ZoneId] uniqueidentifier NOT NULL,
                    [RaftId] uniqueidentifier NULL,
                    [CycleId] uniqueidentifier NULL,
                    [EmployeeId] uniqueidentifier NULL,
                    [WorkerId] uniqueidentifier NULL,
                    [ContractorId] uniqueidentifier NULL,
                    [Task] nvarchar(500) NOT NULL,
                    [JobTypeId] uniqueidentifier NULL,
                    [WorkDate] date NOT NULL,
                    [PaymentType] nvarchar(50) NOT NULL,
                    [ContractAmount] decimal(18,2) NULL,
                    [ExpectedCompletionDate] date NULL,
                    [AcceptanceDate] date NULL,
                    [AcceptancePassed] bit NULL,
                    [AcceptanceEvidenceUrls] nvarchar(max) NOT NULL,
                    [CancelledAt] datetimeoffset NULL,
                    [CancelledByAccountId] uniqueidentifier NULL,
                    [CancellationReason] nvarchar(1000) NULL,
                    [CreatedAt] datetimeoffset NOT NULL,
                    [CreatedBy] uniqueidentifier NULL,
                    [UpdatedAt] datetimeoffset NULL,
                    [UpdatedBy] uniqueidentifier NULL,
                    [RowVersion] rowversion NOT NULL,
                    CONSTRAINT [PK_WorkAssignments] PRIMARY KEY ([Id]),
                    CONSTRAINT [CK_WorkAssignment_PaymentType] CHECK (([PaymentType] = N'DailyWage' AND (([EmployeeId] IS NOT NULL AND [WorkerId] IS NULL) OR ([EmployeeId] IS NULL AND [WorkerId] IS NOT NULL))) OR ([PaymentType] = N'TaskContract' AND [ContractorId] IS NOT NULL AND [ContractAmount] IS NOT NULL)),
                    CONSTRAINT [FK_WorkAssignments_Accounts_CancelledByAccountId] FOREIGN KEY ([CancelledByAccountId]) REFERENCES [Accounts] ([Id]) ON DELETE NO ACTION,
                    CONSTRAINT [FK_WorkAssignments_CultivationCycles_CycleId] FOREIGN KEY ([CycleId]) REFERENCES [CultivationCycles] ([Id]) ON DELETE NO ACTION,
                    CONSTRAINT [FK_WorkAssignments_Employees_EmployeeId] FOREIGN KEY ([EmployeeId]) REFERENCES [Employees] ([Id]) ON DELETE NO ACTION,
                    CONSTRAINT [FK_WorkAssignments_FarmingZones_ZoneId] FOREIGN KEY ([ZoneId]) REFERENCES [FarmingZones] ([Id]) ON DELETE NO ACTION,
                    CONSTRAINT [FK_WorkAssignments_JobTypes_JobTypeId] FOREIGN KEY ([JobTypeId]) REFERENCES [JobTypes] ([Id]) ON DELETE NO ACTION,
                    CONSTRAINT [FK_WorkAssignments_Rafts_RaftId] FOREIGN KEY ([RaftId]) REFERENCES [Rafts] ([Id]) ON DELETE NO ACTION,
                    CONSTRAINT [FK_WorkAssignments_WorkDayPlan_WorkDayPlanId] FOREIGN KEY ([WorkDayPlanId]) REFERENCES [WorkDayPlan] ([Id]) ON DELETE NO ACTION
                );
                END;
                """);

            migrationBuilder.Sql("""
                IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'Id', N'Code', N'DisplayColor', N'DisplayName', N'Group', N'IsFinal', N'IsInitial', N'Meaning', N'SortOrder') AND [object_id] = OBJECT_ID(N'[Statuses]'))
                    SET IDENTITY_INSERT [Statuses] ON;
                IF NOT EXISTS (SELECT 1 FROM [Statuses] WHERE [Id] = 303)
                INSERT INTO [Statuses] ([Id], [Code], [DisplayColor], [DisplayName], [Group], [IsFinal], [IsInitial], [Meaning], [SortOrder]) SELECT 303, N'PENDING_APPROVAL', NULL, N'Pending approval', N'ACCOUNT', CAST(0 AS bit), CAST(1 AS bit), N'A trader asked for an account from the Zalo Mini App; cannot log in until the Cooperative Owner approves.', 3;
                IF NOT EXISTS (SELECT 1 FROM [Statuses] WHERE [Id] = 304)
                INSERT INTO [Statuses] ([Id], [Code], [DisplayColor], [DisplayName], [Group], [IsFinal], [IsInitial], [Meaning], [SortOrder]) SELECT 304, N'REJECTED', NULL, N'Rejected', N'ACCOUNT', CAST(0 AS bit), CAST(0 AS bit), N'The Cooperative Owner refused the request; the trader may ask again.', 4;
                IF NOT EXISTS (SELECT 1 FROM [Statuses] WHERE [Id] = 3205)
                INSERT INTO [Statuses] ([Id], [Code], [DisplayColor], [DisplayName], [Group], [IsFinal], [IsInitial], [Meaning], [SortOrder]) SELECT 3205, N'AWAITING_STAFF', NULL, N'Awaiting staff confirmation', N'PURCHASE_REQUEST', CAST(0 AS bit), CAST(0 AS bit), N'User decision 31/10/2026: employees approve their shift before the Owner accepts the request.', 5;
                IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'Id', N'Code', N'DisplayColor', N'DisplayName', N'Group', N'IsFinal', N'IsInitial', N'Meaning', N'SortOrder') AND [object_id] = OBJECT_ID(N'[Statuses]'))
                    SET IDENTITY_INSERT [Statuses] OFF;
                """);

            migrationBuilder.Sql("""
                IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'Id', N'AllowedPerformers', N'FromStatusId', N'GuardCondition', N'ReasonRequired', N'ToStatusId', N'TriggerEvent', N'UseCases') AND [object_id] = OBJECT_ID(N'[StatusTransitions]'))
                    SET IDENTITY_INSERT [StatusTransitions] ON;
                IF NOT EXISTS (SELECT 1 FROM [StatusTransitions] WHERE [Id] = 30074)
                INSERT INTO [StatusTransitions] ([Id], [AllowedPerformers], [FromStatusId], [GuardCondition], [ReasonRequired], [ToStatusId], [TriggerEvent], [UseCases]) SELECT 30074, N'Cooperative Owner', 303, N'Approve the trader''s request.', CAST(0 AS bit), 301, N'Action', N'ACS-01 (Zalo)';
                IF NOT EXISTS (SELECT 1 FROM [StatusTransitions] WHERE [Id] = 30075)
                INSERT INTO [StatusTransitions] ([Id], [AllowedPerformers], [FromStatusId], [GuardCondition], [ReasonRequired], [ToStatusId], [TriggerEvent], [UseCases]) SELECT 30075, N'Cooperative Owner', 303, N'Refuse the trader''s request.', CAST(1 AS bit), 304, N'Action', N'ACS-01 (Zalo)';
                IF NOT EXISTS (SELECT 1 FROM [StatusTransitions] WHERE [Id] = 30076)
                INSERT INTO [StatusTransitions] ([Id], [AllowedPerformers], [FromStatusId], [GuardCondition], [ReasonRequired], [ToStatusId], [TriggerEvent], [UseCases]) SELECT 30076, N'Trader', 304, N'The trader asks again from the Zalo Mini App.', CAST(0 AS bit), 303, N'Action', N'ACS-01 (Zalo)';
                IF NOT EXISTS (SELECT 1 FROM [StatusTransitions] WHERE [Id] = 30081)
                INSERT INTO [StatusTransitions] ([Id], [AllowedPerformers], [FromStatusId], [GuardCondition], [ReasonRequired], [ToStatusId], [TriggerEvent], [UseCases]) SELECT 30081, N'Cooperative Owner', 3201, N'The Owner sends dated shift invitations with a cage capacity.', CAST(0 AS bit), 3205, N'Action', N'SALE-03';
                IF NOT EXISTS (SELECT 1 FROM [StatusTransitions] WHERE [Id] = 30082)
                INSERT INTO [StatusTransitions] ([Id], [AllowedPerformers], [FromStatusId], [GuardCondition], [ReasonRequired], [ToStatusId], [TriggerEvent], [UseCases]) SELECT 30082, N'Cooperative Owner', 3205, N'The Owner declines with a reason.', CAST(1 AS bit), 3203, N'Action', N'SALE-03';
                IF NOT EXISTS (SELECT 1 FROM [StatusTransitions] WHERE [Id] = 30083)
                INSERT INTO [StatusTransitions] ([Id], [AllowedPerformers], [FromStatusId], [GuardCondition], [ReasonRequired], [ToStatusId], [TriggerEvent], [UseCases]) SELECT 30083, N'Trader', 3205, N'Only the requesting customer may cancel; release shift reservations.', CAST(0 AS bit), 3204, N'Action', N'SALE-02';
                IF NOT EXISTS (SELECT 1 FROM [StatusTransitions] WHERE [Id] = 30084)
                INSERT INTO [StatusTransitions] ([Id], [AllowedPerformers], [FromStatusId], [GuardCondition], [ReasonRequired], [ToStatusId], [TriggerEvent], [UseCases]) SELECT 30084, N'Cooperative Owner', 3205, N'Enough distinct employees have approved the dated shift; cages and pickup match the staffing plan.', CAST(0 AS bit), 3202, N'Action', N'SALE-03';
                IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'Id', N'AllowedPerformers', N'FromStatusId', N'GuardCondition', N'ReasonRequired', N'ToStatusId', N'TriggerEvent', N'UseCases') AND [object_id] = OBJECT_ID(N'[StatusTransitions]'))
                    SET IDENTITY_INSERT [StatusTransitions] OFF;
                """);

            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.PurchaseRequests') AND [name] = N'IX_PurchaseRequests_Code')
                CREATE UNIQUE INDEX [IX_PurchaseRequests_Code] ON [PurchaseRequests] ([Code]) WHERE [Code] <> N'';
                """);

            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.PurchaseRequests') AND [name] = N'IX_PurchaseRequests_CustomerId_ClientOperationId')
                CREATE UNIQUE INDEX [IX_PurchaseRequests_CustomerId_ClientOperationId] ON [PurchaseRequests] ([CustomerId], [ClientOperationId]) WHERE [ClientOperationId] <> '00000000-0000-0000-0000-000000000000';
                """);

            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.PurchaseRequests') AND [name] = N'IX_PurchaseRequests_CustomerId_CreatedAt')
                CREATE INDEX [IX_PurchaseRequests_CustomerId_CreatedAt] ON [PurchaseRequests] ([CustomerId], [CreatedAt]);
                """);

            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.PurchaseRequests') AND [name] = N'IX_PurchaseRequests_StatusId_CreatedAt')
                CREATE INDEX [IX_PurchaseRequests_StatusId_CreatedAt] ON [PurchaseRequests] ([StatusId], [CreatedAt]);
                """);

            migrationBuilder.Sql("""
                IF OBJECT_ID(N'dbo.CK_PurchaseRequest_Accepted_Has_Response') IS NULL
                ALTER TABLE [PurchaseRequests] ADD CONSTRAINT [CK_PurchaseRequest_Accepted_Has_Response] CHECK ([StatusId] <> 3202 OR ([RequestedCages] > 0 AND [AcceptedCages] IS NOT NULL AND [AcceptedPickupDate] IS NOT NULL) OR ([RequestedCages] = 0 AND [AcceptedKg] IS NOT NULL AND [AgreedPickupDate] IS NOT NULL AND [PayLaterAgreed] IS NOT NULL));
                """);

            migrationBuilder.Sql("""
                IF OBJECT_ID(N'dbo.CK_PurchaseRequest_AcceptedKg_Range') IS NULL
                ALTER TABLE [PurchaseRequests] ADD CONSTRAINT [CK_PurchaseRequest_AcceptedKg_Range] CHECK ([AcceptedKg] IS NULL OR ([RequestedCages] = 0 AND [AcceptedKg] > 0 AND [AcceptedKg] <= [RequestedKg]));
                """);

            migrationBuilder.Sql("""
                IF OBJECT_ID(N'dbo.CK_PurchaseRequest_Cages') IS NULL
                ALTER TABLE [PurchaseRequests] ADD CONSTRAINT [CK_PurchaseRequest_Cages] CHECK ((([RequestedCages] > 0 AND [RequestedKg] = 0) OR ([RequestedCages] = 0 AND [RequestedKg] > 0)) AND ([AcceptedCages] IS NULL OR ([AcceptedCages] > 0 AND [AcceptedCages] <= [RequestedCages])));
                """);

            migrationBuilder.Sql("""
                IF OBJECT_ID(N'dbo.CK_PurchaseRequest_RequestedKg_Positive') IS NULL
                ALTER TABLE [PurchaseRequests] ADD CONSTRAINT [CK_PurchaseRequest_RequestedKg_Positive] CHECK ([RequestedCages] > 0 OR [RequestedKg] > 0);
                """);

            migrationBuilder.Sql("""
                IF OBJECT_ID(N'dbo.CK_PurchaseRequest_Staffing') IS NULL
                ALTER TABLE [PurchaseRequests] ADD CONSTRAINT [CK_PurchaseRequest_Staffing] CHECK (([PlannedCages] IS NULL AND [WorkDate] IS NULL AND [PlannedPickupDate] IS NULL AND [CagesPerEmployeePerDay] IS NULL) OR ([PlannedCages] IS NOT NULL AND [PlannedCages] > 0 AND [PlannedCages] <= [RequestedCages] AND [WorkDate] IS NOT NULL AND [PlannedPickupDate] IS NOT NULL AND [WorkDate] <= [PlannedPickupDate] AND [CagesPerEmployeePerDay] IS NOT NULL AND [CagesPerEmployeePerDay] > 0));
                """);

            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.Products') AND [name] = N'IX_Products_Code')
                CREATE UNIQUE INDEX [IX_Products_Code] ON [Products] ([Code]) WHERE [Code] <> N'';
                """);

            migrationBuilder.Sql("""
                IF OBJECT_ID(N'dbo.CK_Product_UnitPrice_Positive') IS NULL
                ALTER TABLE [Products] ADD CONSTRAINT [CK_Product_UnitPrice_Positive] CHECK ([UnitPrice] > 0 OR ([Code] <> N'' AND [UnitPrice] = 0));
                """);

            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.Accounts') AND [name] = N'IX_Accounts_ZaloAccountId')
                CREATE UNIQUE INDEX [IX_Accounts_ZaloAccountId] ON [Accounts] ([ZaloAccountId]) WHERE [ZaloAccountId] IS NOT NULL;
                """);

            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.AttendanceJobPeriod') AND [name] = N'IX_AttendanceJobPeriod_AttendanceId_StartsAt')
                CREATE UNIQUE INDEX [IX_AttendanceJobPeriod_AttendanceId_StartsAt] ON [AttendanceJobPeriod] ([AttendanceId], [StartsAt]);
                """);

            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.AttendanceJobPeriod') AND [name] = N'IX_AttendanceJobPeriod_JobTypeId')
                CREATE INDEX [IX_AttendanceJobPeriod_JobTypeId] ON [AttendanceJobPeriod] ([JobTypeId]);
                """);

            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.AttendanceJobPeriod') AND [name] = N'IX_AttendanceJobPeriod_RaftId')
                CREATE INDEX [IX_AttendanceJobPeriod_RaftId] ON [AttendanceJobPeriod] ([RaftId]);
                """);

            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.AttendanceQr') AND [name] = N'IX_AttendanceQr_EmployeeId')
                CREATE UNIQUE INDEX [IX_AttendanceQr_EmployeeId] ON [AttendanceQr] ([EmployeeId]);
                """);

            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.AttendanceQr') AND [name] = N'IX_AttendanceQr_ExpiresAt')
                CREATE INDEX [IX_AttendanceQr_ExpiresAt] ON [AttendanceQr] ([ExpiresAt]);
                """);

            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.AttendanceQr') AND [name] = N'IX_AttendanceQr_TokenHash')
                CREATE UNIQUE INDEX [IX_AttendanceQr_TokenHash] ON [AttendanceQr] ([TokenHash]);
                """);

            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.AttendanceRecords') AND [name] = N'IX_AttendanceRecords_EmployeeId_WorkDate')
                CREATE UNIQUE INDEX [IX_AttendanceRecords_EmployeeId_WorkDate] ON [AttendanceRecords] ([EmployeeId], [WorkDate]) WHERE [EmployeeId] IS NOT NULL;
                """);

            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.AttendanceRecords') AND [name] = N'IX_AttendanceRecords_RecordedByAccountId')
                CREATE INDEX [IX_AttendanceRecords_RecordedByAccountId] ON [AttendanceRecords] ([RecordedByAccountId]);
                """);

            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.AttendanceRecords') AND [name] = N'IX_AttendanceRecords_WorkerId_WorkDate')
                CREATE UNIQUE INDEX [IX_AttendanceRecords_WorkerId_WorkDate] ON [AttendanceRecords] ([WorkerId], [WorkDate]) WHERE [WorkerId] IS NOT NULL;
                """);

            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.AttendanceRecords') AND [name] = N'IX_AttendanceRecords_ZoneId')
                CREATE INDEX [IX_AttendanceRecords_ZoneId] ON [AttendanceRecords] ([ZoneId]);
                """);

            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.EmployeeLeaveRequest') AND [name] = N'IX_EmployeeLeaveRequest_EmployeeId_ClientOperationId')
                CREATE UNIQUE INDEX [IX_EmployeeLeaveRequest_EmployeeId_ClientOperationId] ON [EmployeeLeaveRequest] ([EmployeeId], [ClientOperationId]);
                """);

            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.EmployeeLeaveRequest') AND [name] = N'IX_EmployeeLeaveRequest_ReviewedByAccountId')
                CREATE INDEX [IX_EmployeeLeaveRequest_ReviewedByAccountId] ON [EmployeeLeaveRequest] ([ReviewedByAccountId]);
                """);

            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.PurchaseShiftInvitation') AND [name] = N'IX_PurchaseShiftInvitation_EmployeeId_ReservedWorkDate')
                CREATE UNIQUE INDEX [IX_PurchaseShiftInvitation_EmployeeId_ReservedWorkDate] ON [PurchaseShiftInvitation] ([EmployeeId], [ReservedWorkDate]) WHERE [ReservedWorkDate] IS NOT NULL;
                """);

            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.PurchaseShiftInvitation') AND [name] = N'IX_PurchaseShiftInvitation_PurchaseRequestId_EmployeeId')
                CREATE UNIQUE INDEX [IX_PurchaseShiftInvitation_PurchaseRequestId_EmployeeId] ON [PurchaseShiftInvitation] ([PurchaseRequestId], [EmployeeId]);
                """);

            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.WorkAssignments') AND [name] = N'IX_WorkAssignments_CancelledByAccountId')
                CREATE INDEX [IX_WorkAssignments_CancelledByAccountId] ON [WorkAssignments] ([CancelledByAccountId]);
                """);

            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.WorkAssignments') AND [name] = N'IX_WorkAssignments_CycleId')
                CREATE INDEX [IX_WorkAssignments_CycleId] ON [WorkAssignments] ([CycleId]);
                """);

            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.WorkAssignments') AND [name] = N'IX_WorkAssignments_EmployeeId_WorkDate')
                CREATE UNIQUE INDEX [IX_WorkAssignments_EmployeeId_WorkDate] ON [WorkAssignments] ([EmployeeId], [WorkDate]) WHERE [EmployeeId] IS NOT NULL AND [CancelledAt] IS NULL AND [JobTypeId] IS NOT NULL;
                """);

            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.WorkAssignments') AND [name] = N'IX_WorkAssignments_JobTypeId')
                CREATE INDEX [IX_WorkAssignments_JobTypeId] ON [WorkAssignments] ([JobTypeId]);
                """);

            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.WorkAssignments') AND [name] = N'IX_WorkAssignments_RaftId')
                CREATE INDEX [IX_WorkAssignments_RaftId] ON [WorkAssignments] ([RaftId]);
                """);

            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.WorkAssignments') AND [name] = N'IX_WorkAssignments_WorkDayPlanId')
                CREATE INDEX [IX_WorkAssignments_WorkDayPlanId] ON [WorkAssignments] ([WorkDayPlanId]);
                """);

            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.WorkAssignments') AND [name] = N'IX_WorkAssignments_ZoneId_WorkDate')
                CREATE INDEX [IX_WorkAssignments_ZoneId_WorkDate] ON [WorkAssignments] ([ZoneId], [WorkDate]);
                """);

            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.WorkDayPlan') AND [name] = N'IX_WorkDayPlan_CreatorAccountId_ClientOperationId')
                CREATE UNIQUE INDEX [IX_WorkDayPlan_CreatorAccountId_ClientOperationId] ON [WorkDayPlan] ([CreatorAccountId], [ClientOperationId]);
                """);

            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.WorkDayPlan') AND [name] = N'IX_WorkDayPlan_JobTypeId')
                CREATE INDEX [IX_WorkDayPlan_JobTypeId] ON [WorkDayPlan] ([JobTypeId]);
                """);

            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.WorkDayPlan') AND [name] = N'IX_WorkDayPlan_PurchaseRequestId')
                CREATE UNIQUE INDEX [IX_WorkDayPlan_PurchaseRequestId] ON [WorkDayPlan] ([PurchaseRequestId]) WHERE [PurchaseRequestId] IS NOT NULL;
                """);

            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.WorkDayPlan') AND [name] = N'IX_WorkDayPlan_RaftId')
                CREATE INDEX [IX_WorkDayPlan_RaftId] ON [WorkDayPlan] ([RaftId]);
                """);

            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.WorkDayPlan') AND [name] = N'IX_WorkDayPlan_ResponsibleAccountId_WorkDate')
                CREATE INDEX [IX_WorkDayPlan_ResponsibleAccountId_WorkDate] ON [WorkDayPlan] ([ResponsibleAccountId], [WorkDate]);
                """);

            migrationBuilder.Sql("""

                -- The legacy mobile transitions now point at the shared purchase-request states, never purchase-return IDs.
                DELETE FROM [StatusTransitions] WHERE [Id] BETWEEN 30077 AND 30080;
                UPDATE [StatusTransitions] SET [FromStatusId] = [FromStatusId]-700
                WHERE [Id] BETWEEN 30074 AND 30084 AND [FromStatusId] BETWEEN 3901 AND 3905;
                UPDATE [StatusTransitions] SET [ToStatusId] = [ToStatusId]-700
                WHERE [Id] BETWEEN 30074 AND 30084 AND [ToStatusId] BETWEEN 3901 AND 3905;

                -- Restore optional product metadata after SalesCatalogueV2; weight prices keep their own meaning.
                IF OBJECT_ID(N'dbo.MiniAppLegacyProductMetadata', N'U') IS NOT NULL
                    EXEC(N'UPDATE p SET [Code] = m.[Code], [Description] = m.[Description], [ValidFrom] = m.[ValidFrom], [ValidTo] = m.[ValidTo] FROM [Products] p JOIN [MiniAppLegacyProductMetadata] m ON m.[Id] = p.[Id]');

                -- Unify the explicit units in one request store. No conversion from cages to kilograms is attempted.
                IF OBJECT_ID(N'dbo.PurchaseRequest', N'U') IS NOT NULL
                BEGIN
                    INSERT INTO [PurchaseRequests] ([Id],[Code],[CustomerId],[ProductId],[ProductName],[RequestedCages],[RequestedKg],[PickupDate],[Note],[ClientOperationId],[StatusId],
                        [AcceptedCages],[AcceptedPickupDate],[AllowPayLater],[ResponseNote],[RespondedAt],[RespondedByAccountId],[CancelledAt],[CancellationReason],
                        [PlannedCages],[WorkDate],[PlannedPickupDate],[CagesPerEmployeePerDay],[StaffingNote],[CreatedAt],[CreatedBy],[UpdatedAt],[UpdatedBy],[SentAt])
                    SELECT r.[Id],r.[Code],r.[CustomerId],r.[ProductId],r.[ProductName],r.[RequestedCages],0,r.[PickupDate],r.[Note],r.[ClientOperationId],
                        CASE WHEN r.[StatusId] BETWEEN 3901 AND 3905 THEN r.[StatusId] - 700 ELSE r.[StatusId] END,
                        r.[AcceptedCages],r.[AcceptedPickupDate],r.[AllowPayLater],r.[ResponseNote],r.[RespondedAt],r.[RespondedByAccountId],r.[CancelledAt],r.[CancellationReason],
                        r.[PlannedCages],r.[WorkDate],r.[PlannedPickupDate],r.[CagesPerEmployeePerDay],r.[StaffingNote],r.[CreatedAt],r.[CreatedBy],r.[UpdatedAt],r.[UpdatedBy],r.[CreatedAt]
                    FROM [PurchaseRequest] r WHERE NOT EXISTS (SELECT 1 FROM [PurchaseRequests] n WHERE n.[Id] = r.[Id]);
                END;

                -- Copy the four archived workforce tables in parent-first order; SQL Server generates new rowversions.
                DECLARE @tables TABLE ([Position] int, [Name] sysname);
                INSERT INTO @tables VALUES (1,N'WorkDayPlan'),(2,N'AttendanceRecords'),(3,N'WorkAssignments'),(4,N'AttendanceJobPeriod');
                DECLARE @table sysname, @columns nvarchar(max), @sql nvarchar(max);
                DECLARE restore_rows CURSOR LOCAL FAST_FORWARD FOR SELECT [Name] FROM @tables ORDER BY [Position];
                OPEN restore_rows; FETCH NEXT FROM restore_rows INTO @table;
                WHILE @@FETCH_STATUS = 0
                BEGIN
                    IF OBJECT_ID(N'dbo.MiniAppLegacy' + @table,N'U') IS NOT NULL
                    BEGIN
                        SELECT @columns = STRING_AGG(CONVERT(nvarchar(max),QUOTENAME(c.[name])),N',') WITHIN GROUP (ORDER BY c.[column_id])
                        FROM sys.columns c WHERE c.[object_id] = OBJECT_ID(N'dbo.' + @table) AND c.[system_type_id] <> 189 AND c.[is_identity] = 0
                          AND EXISTS (SELECT 1 FROM sys.columns a WHERE a.[object_id] = OBJECT_ID(N'dbo.MiniAppLegacy' + @table) AND a.[name] = c.[name]);
                        SET @sql = N'INSERT INTO ' + QUOTENAME(@table) + N' (' + @columns + N') SELECT ' + @columns + N' FROM ' + QUOTENAME(N'MiniAppLegacy' + @table) + N' a WHERE NOT EXISTS (SELECT 1 FROM ' + QUOTENAME(@table) + N' n WHERE n.[Id] = a.[Id]);';
                        EXEC sp_executesql @sql;
                    END;
                    FETCH NEXT FROM restore_rows INTO @table;
                END;
                CLOSE restore_rows; DEALLOCATE restore_rows;

                -- Invitations existed before the merged request table. Move their FK after request IDs have been copied.
                DECLARE @drop nvarchar(max) = N'';
                SELECT @drop += N'ALTER TABLE [PurchaseShiftInvitation] DROP CONSTRAINT ' + QUOTENAME([name]) + N';'
                FROM sys.foreign_keys WHERE [parent_object_id] = OBJECT_ID(N'dbo.PurchaseShiftInvitation') AND [referenced_object_id] = OBJECT_ID(N'dbo.PurchaseRequest');
                EXEC sp_executesql @drop;
                IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE [name] = N'FK_PurchaseShiftInvitation_PurchaseRequests_PurchaseRequestId')
                    ALTER TABLE [PurchaseShiftInvitation] ADD CONSTRAINT [FK_PurchaseShiftInvitation_PurchaseRequests_PurchaseRequestId] FOREIGN KEY ([PurchaseRequestId]) REFERENCES [PurchaseRequests]([Id]);

                -- Existing Mini App settings become the same dated versions the web uses; keep the source archive.
                IF OBJECT_ID(N'dbo.AttendanceSettings', N'U') IS NOT NULL
                BEGIN
                    DECLARE @owner uniqueidentifier = (SELECT TOP(1) [Id] FROM [Accounts] WHERE [SubjectType] = N'CooperativeOwner' ORDER BY [CreatedAt]);
                    IF @owner IS NOT NULL
                        INSERT INTO [AttendanceSettingsVersions] ([Id],[EffectiveFrom],[BreakStart],[BreakEnd],[RoundingUnit],[EnteredByAccountId],[CreatedAt],[CreatedBy],[UpdatedAt],[UpdatedBy])
                        SELECT s.[Id],s.[EffectiveFrom],s.[BreakFrom],s.[BreakTo],CASE s.[RoundingMinutes] WHEN 15 THEN N'Minutes15' WHEN 30 THEN N'Minutes30' WHEN 60 THEN N'Hour1' ELSE N'None' END,
                            @owner,s.[CreatedAt],s.[CreatedBy],s.[UpdatedAt],s.[UpdatedBy]
                        FROM [AttendanceSettings] s WHERE NOT EXISTS (SELECT 1 FROM [AttendanceSettingsVersions] n WHERE n.[EffectiveFrom] = s.[EffectiveFrom]);
                END;

                -- Preserve every closed legacy payroll period as a lock in the current payroll header table.
                IF OBJECT_ID(N'dbo.MiniAppLegacyLaborSettlements',N'U') IS NOT NULL
                BEGIN
                    ;WITH locked AS (SELECT [PeriodStart],[PeriodEnd],MAX([CreatedAt]) AS [CreatedAt],MAX(CASE WHEN [StatusId] = 1503 THEN 1 ELSE 0 END) AS [Finalized]
                        FROM [MiniAppLegacyLaborSettlements] WHERE [StatusId] IN (1502,1503) GROUP BY [PeriodStart],[PeriodEnd])
                    INSERT INTO [Payrolls] ([Id],[PeriodStart],[PeriodEnd],[StatusId],[CreatedBySystem],[CreatedAt])
                    SELECT NEWID(),l.[PeriodStart],l.[PeriodEnd],CASE WHEN l.[Finalized] = 1 THEN 3404 ELSE 3402 END,0,l.[CreatedAt]
                    FROM locked l WHERE NOT EXISTS (SELECT 1 FROM [Payrolls] p WHERE p.[PeriodStart] = l.[PeriodStart] OR p.[PeriodEnd] = l.[PeriodEnd]);
                END;

                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AttendanceJobPeriod");

            migrationBuilder.DropTable(
                name: "AttendanceQr");

            migrationBuilder.DropTable(
                name: "EmployeeLeaveRequest");

            migrationBuilder.DropTable(
                name: "PurchaseShiftInvitation");

            migrationBuilder.DropTable(
                name: "WorkAssignments");

            migrationBuilder.DropTable(
                name: "AttendanceRecords");

            migrationBuilder.DropTable(
                name: "WorkDayPlan");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseRequests_Code",
                table: "PurchaseRequests");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseRequests_CustomerId_ClientOperationId",
                table: "PurchaseRequests");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseRequests_CustomerId_CreatedAt",
                table: "PurchaseRequests");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseRequests_StatusId_CreatedAt",
                table: "PurchaseRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PurchaseRequest_Accepted_Has_Response",
                table: "PurchaseRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PurchaseRequest_AcceptedKg_Range",
                table: "PurchaseRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PurchaseRequest_Cages",
                table: "PurchaseRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PurchaseRequest_RequestedKg_Positive",
                table: "PurchaseRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PurchaseRequest_Staffing",
                table: "PurchaseRequests");

            migrationBuilder.DropIndex(
                name: "IX_Products_Code",
                table: "Products");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Product_UnitPrice_Positive",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_Accounts_ZaloAccountId",
                table: "Accounts");

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30074);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30075);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30076);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30081);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30082);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30083);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30084);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 303);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 304);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3205);

            migrationBuilder.DropColumn(
                name: "AcceptedCages",
                table: "PurchaseRequests");

            migrationBuilder.DropColumn(
                name: "AcceptedPickupDate",
                table: "PurchaseRequests");

            migrationBuilder.DropColumn(
                name: "AllowPayLater",
                table: "PurchaseRequests");

            migrationBuilder.DropColumn(
                name: "CagesPerEmployeePerDay",
                table: "PurchaseRequests");

            migrationBuilder.DropColumn(
                name: "CancellationReason",
                table: "PurchaseRequests");

            migrationBuilder.DropColumn(
                name: "ClientOperationId",
                table: "PurchaseRequests");

            migrationBuilder.DropColumn(
                name: "Code",
                table: "PurchaseRequests");

            migrationBuilder.DropColumn(
                name: "PlannedCages",
                table: "PurchaseRequests");

            migrationBuilder.DropColumn(
                name: "PlannedPickupDate",
                table: "PurchaseRequests");

            migrationBuilder.DropColumn(
                name: "ProductName",
                table: "PurchaseRequests");

            migrationBuilder.DropColumn(
                name: "RequestedCages",
                table: "PurchaseRequests");

            migrationBuilder.DropColumn(
                name: "StaffingNote",
                table: "PurchaseRequests");

            migrationBuilder.DropColumn(
                name: "WorkDate",
                table: "PurchaseRequests");

            migrationBuilder.DropColumn(
                name: "Code",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "ValidFrom",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "ValidTo",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "AccessRequestNote",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "AccessRequestedAt",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "RejectionReason",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "ZaloAccountId",
                table: "Accounts");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PurchaseRequest_Accepted_Has_Response",
                table: "PurchaseRequests",
                sql: "[StatusId] <> 3202 OR ([AcceptedKg] IS NOT NULL AND [AgreedPickupDate] IS NOT NULL AND [PayLaterAgreed] IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PurchaseRequest_AcceptedKg_Range",
                table: "PurchaseRequests",
                sql: "[AcceptedKg] IS NULL OR ([AcceptedKg] > 0 AND [AcceptedKg] <= [RequestedKg])");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PurchaseRequest_RequestedKg_Positive",
                table: "PurchaseRequests",
                sql: "[RequestedKg] > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Product_UnitPrice_Positive",
                table: "Products",
                sql: "[UnitPrice] > 0");
        }
    }
}
