using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Statuses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    Group = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Meaning = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IsInitial = table.Column<bool>(type: "bit", nullable: false),
                    IsFinal = table.Column<bool>(type: "bit", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    DisplayColor = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Statuses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ZoneComplianceDocuments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DocumentNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Holder = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IssuingAuthority = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    LegalScope = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    EffectiveDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ExpiryDate = table.Column<DateOnly>(type: "date", nullable: true),
                    EvidenceFileUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ReplacesDocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ZoneComplianceDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ZoneComplianceDocuments_ZoneComplianceDocuments_ReplacesDocumentId",
                        column: x => x.ReplacesDocumentId,
                        principalTable: "ZoneComplianceDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Accounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Username = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    PhoneNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    PhoneVerifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    EmailVerifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PasswordHash = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    TemporaryPasswordIssuedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    MustChangePassword = table.Column<bool>(type: "bit", nullable: false),
                    FailedLoginCount = table.Column<int>(type: "int", nullable: false),
                    LockoutEndAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastLoginAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SubjectType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    NotificationPreferences = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Accounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Accounts_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Contractors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PhoneNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    OtherContacts = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Contractors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Contractors_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Cooperatives",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FullName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TaxCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    OfficeAddress = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PhoneNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Website = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    LegalRepresentative = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    BusinessLines = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    BankAccount_BankName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    BankAccount_AccountNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    BankAccount_AccountHolderName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Cooperatives", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Cooperatives_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OysterSeedTypes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    IllustrationUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OysterSeedTypes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OysterSeedTypes_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Products",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    SalesUnit = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    ValidTo = table.Column<DateOnly>(type: "date", nullable: true),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Products", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Products_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Roles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Roles_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SeedSuppliers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PhoneNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    OtherContacts = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeedSuppliers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SeedSuppliers_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StatusTransitions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    FromStatusId = table.Column<int>(type: "int", nullable: false),
                    ToStatusId = table.Column<int>(type: "int", nullable: false),
                    UseCases = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    AllowedPerformers = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TriggerEvent = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    GuardCondition = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ReasonRequired = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StatusTransitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StatusTransitions_Statuses_FromStatusId",
                        column: x => x.FromStatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StatusTransitions_Statuses_ToStatusId",
                        column: x => x.ToStatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AuditEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Action = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RecordType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RecordCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Before = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    After = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ZoneId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ViewScope = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuditEvents_Accounts_ActorAccountId",
                        column: x => x.ActorAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LoginSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    RefreshTokenHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    PreviousRefreshTokenHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastRefreshedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RefreshTokenExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AbsoluteExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RevokeReason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DeviceName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    UserAgent = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IpAddress = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoginSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LoginSessions_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipientAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    RelatedRecordType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    RelatedRecordCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReadAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    IsMandatory = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Notifications_Accounts_RecipientAccountId",
                        column: x => x.RecipientAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VerificationCodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Purpose = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Target = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CodeHash = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastSentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SendCount = table.Column<int>(type: "int", nullable: false),
                    FailedAttempts = table.Column<int>(type: "int", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VerificationCodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VerificationCodes_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Customers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CooperativeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PhoneNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    OtherContacts = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ZaloAccountId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    DeliveryAddress = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ReceivingContact = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    MergedIntoCustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Customers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Customers_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Customers_Cooperatives_CooperativeId",
                        column: x => x.CooperativeId,
                        principalTable: "Cooperatives",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Customers_Customers_MergedIntoCustomerId",
                        column: x => x.MergedIntoCustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Customers_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Employees",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CooperativeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FullName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PhoneNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    AccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Birthday = table.Column<DateOnly>(type: "date", nullable: true),
                    Address = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Area = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Ward = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    OtherInformation = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Employees", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Employees_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Employees_Cooperatives_CooperativeId",
                        column: x => x.CooperativeId,
                        principalTable: "Cooperatives",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Employees_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FarmingZones",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CooperativeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Location = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Latitude = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: true),
                    Longitude = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: true),
                    Area = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    WaterMeasurementPoint = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PhMin = table.Column<decimal>(type: "decimal(4,2)", precision: 4, scale: 2, nullable: true),
                    PhMax = table.Column<decimal>(type: "decimal(4,2)", precision: 4, scale: 2, nullable: true),
                    SalinityMin = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: true),
                    SalinityMax = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: true),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FarmingZones", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FarmingZones_Cooperatives_CooperativeId",
                        column: x => x.CooperativeId,
                        principalTable: "Cooperatives",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FarmingZones_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RolePermissions",
                columns: table => new
                {
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FunctionCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CanCreate = table.Column<bool>(type: "bit", nullable: false),
                    CanView = table.Column<bool>(type: "bit", nullable: false),
                    CanEdit = table.Column<bool>(type: "bit", nullable: false),
                    CanDelete = table.Column<bool>(type: "bit", nullable: false),
                    OtherPermissions = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DataScope = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false)
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

            migrationBuilder.CreateTable(
                name: "SeedReceiptLots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SeedTypeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InternalCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    SupplierLotCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ActualSource = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Quantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    Unit = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EvidenceUrls = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeedReceiptLots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SeedReceiptLots_OysterSeedTypes_SeedTypeId",
                        column: x => x.SeedTypeId,
                        principalTable: "OysterSeedTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SeedReceiptLots_SeedSuppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "SeedSuppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    ReceiptDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Method = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ReferenceCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    EvidenceUrls = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DeduplicationKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerReceipts", x => x.Id);
                    table.CheckConstraint("CK_CustomerReceipt_Amount_Positive", "[Amount] > 0");
                    table.CheckConstraint("CK_CustomerReceipt_Amount_Thousands", "[Amount] % 1000 = 0");
                    table.ForeignKey(
                        name: "FK_CustomerReceipts_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LaborSettlements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    TotalObligation = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    PreparedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApprovedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LaborSettlements", x => x.Id);
                    table.CheckConstraint("CK_LaborSettlement_TotalObligation_Thousands", "[TotalObligation] % 1000 = 0");
                    table.ForeignKey(
                        name: "FK_LaborSettlements_Accounts_ApprovedByAccountId",
                        column: x => x.ApprovedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LaborSettlements_Accounts_PreparedByAccountId",
                        column: x => x.PreparedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LaborSettlements_Contractors_ContractorId",
                        column: x => x.ContractorId,
                        principalTable: "Contractors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LaborSettlements_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LaborSettlements_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PayRuleVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubjectType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MonthlySalary = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    DailyWageRate = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    ValidTo = table.Column<DateOnly>(type: "date", nullable: true),
                    EnteredByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApprovedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayRuleVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PayRuleVersions_Accounts_ApprovedByAccountId",
                        column: x => x.ApprovedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PayRuleVersions_Accounts_EnteredByAccountId",
                        column: x => x.EnteredByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PayRuleVersions_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PayRuleVersions_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SeasonalWorkers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FullName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PhoneNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    CitizenId = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeasonalWorkers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SeasonalWorkers_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SeasonalWorkers_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AccessGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ZoneId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ScopeType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    EffectiveTo = table.Column<DateOnly>(type: "date", nullable: true),
                    GrantedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RevokedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RevokeReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessGrants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccessGrants_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AccessGrants_Accounts_GrantedByAccountId",
                        column: x => x.GrantedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AccessGrants_Accounts_RevokedByAccountId",
                        column: x => x.RevokedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AccessGrants_FarmingZones_ZoneId",
                        column: x => x.ZoneId,
                        principalTable: "FarmingZones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AccessGrants_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AccessGrants_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DocumentZoneScopes",
                columns: table => new
                {
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ZoneId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    ValidTo = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentZoneScopes", x => new { x.DocumentId, x.ZoneId });
                    table.ForeignKey(
                        name: "FK_DocumentZoneScopes_FarmingZones_ZoneId",
                        column: x => x.ZoneId,
                        principalTable: "FarmingZones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DocumentZoneScopes_ZoneComplianceDocuments_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "ZoneComplianceDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Rafts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ZoneId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Location = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Size = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    PhotoUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ConditionNotes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Rafts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Rafts_FarmingZones_ZoneId",
                        column: x => x.ZoneId,
                        principalTable: "FarmingZones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Rafts_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WaterMeasurements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ZoneId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MeasurementPoint = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    MeasuredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Ph = table.Column<decimal>(type: "decimal(4,2)", precision: 4, scale: 2, nullable: true),
                    Salinity = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: true),
                    InputSource = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    PhotoUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    OcrPh = table.Column<decimal>(type: "decimal(4,2)", precision: 4, scale: 2, nullable: true),
                    OcrSalinity = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: true),
                    OcrMeasuredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    MeasuredByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ConfirmedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ThresholdExceeded = table.Column<bool>(type: "bit", nullable: false),
                    ViolatedValues = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PhMinUsed = table.Column<decimal>(type: "decimal(4,2)", precision: 4, scale: 2, nullable: true),
                    PhMaxUsed = table.Column<decimal>(type: "decimal(4,2)", precision: 4, scale: 2, nullable: true),
                    SalinityMinUsed = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: true),
                    SalinityMaxUsed = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: true),
                    AlertedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RecordVersion = table.Column<int>(type: "int", nullable: false),
                    CorrectedFromMeasurementId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CorrectionReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WaterMeasurements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WaterMeasurements_Accounts_ConfirmedByAccountId",
                        column: x => x.ConfirmedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WaterMeasurements_Accounts_MeasuredByAccountId",
                        column: x => x.MeasuredByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WaterMeasurements_FarmingZones_ZoneId",
                        column: x => x.ZoneId,
                        principalTable: "FarmingZones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WaterMeasurements_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WaterMeasurements_WaterMeasurements_CorrectedFromMeasurementId",
                        column: x => x.CorrectedFromMeasurementId,
                        principalTable: "WaterMeasurements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LaborPayments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SettlementId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Recipient = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AmountPaid = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    PaymentDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Method = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ReferenceCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    DocumentUrls = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OriginalPaymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AdjustmentReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LaborPayments", x => x.Id);
                    table.CheckConstraint("CK_LaborPayment_AmountPaid_Thousands", "[AmountPaid] % 1000 = 0");
                    table.ForeignKey(
                        name: "FK_LaborPayments_LaborPayments_OriginalPaymentId",
                        column: x => x.OriginalPaymentId,
                        principalTable: "LaborPayments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LaborPayments_LaborSettlements_SettlementId",
                        column: x => x.SettlementId,
                        principalTable: "LaborSettlements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AttendanceRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WorkerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ContractorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WorkDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Result = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RecordedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EvidenceUrls = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttendanceRecords", x => x.Id);
                    table.CheckConstraint("CK_AttendanceRecord_OnePerson", "(CASE WHEN [EmployeeId] IS NULL THEN 0 ELSE 1 END + CASE WHEN [WorkerId] IS NULL THEN 0 ELSE 1 END) = 1");
                    table.ForeignKey(
                        name: "FK_AttendanceRecords_Accounts_RecordedByAccountId",
                        column: x => x.RecordedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AttendanceRecords_Contractors_ContractorId",
                        column: x => x.ContractorId,
                        principalTable: "Contractors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AttendanceRecords_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AttendanceRecords_SeasonalWorkers_WorkerId",
                        column: x => x.WorkerId,
                        principalTable: "SeasonalWorkers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CultivationCycles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RaftId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlannedSeedTypeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OpenedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ClosedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SeedingCompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SeedingCompletionReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ClosingSummary = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CultivationCycles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CultivationCycles_OysterSeedTypes_PlannedSeedTypeId",
                        column: x => x.PlannedSeedTypeId,
                        principalTable: "OysterSeedTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CultivationCycles_Rafts_RaftId",
                        column: x => x.RaftId,
                        principalTable: "Rafts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CultivationCycles_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Lanes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RaftId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LaneNumber = table.Column<int>(type: "int", nullable: false),
                    Length = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: true),
                    MeasurementMarkers = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Lanes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Lanes_Rafts_RaftId",
                        column: x => x.RaftId,
                        principalTable: "Rafts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RaftLayoutVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RaftId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    LaneCount = table.Column<int>(type: "int", nullable: false),
                    LayoutMap = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    ValidTo = table.Column<DateOnly>(type: "date", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RaftLayoutVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RaftLayoutVersions_Rafts_RaftId",
                        column: x => x.RaftId,
                        principalTable: "Rafts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RaftMaterialRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RaftId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecordedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecordType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    RecordDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ReplacementReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    PurchasePlaceNotes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    InvoicePhotoUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    TotalAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RaftMaterialRecords", x => x.Id);
                    table.CheckConstraint("CK_RaftMaterialRecord_TotalAmount_Thousands", "[TotalAmount] % 1000 = 0");
                    table.ForeignKey(
                        name: "FK_RaftMaterialRecords_Accounts_RecordedByAccountId",
                        column: x => x.RecordedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RaftMaterialRecords_Rafts_RaftId",
                        column: x => x.RaftId,
                        principalTable: "Rafts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SeedingEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CycleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SeedingDate = table.Column<DateOnly>(type: "date", nullable: false),
                    SeedLotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecordedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Workers = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    EvidenceUrls = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SeedQuantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: true),
                    PlanDeviationReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeedingEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SeedingEvents_Accounts_RecordedByAccountId",
                        column: x => x.RecordedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SeedingEvents_CultivationCycles_CycleId",
                        column: x => x.CycleId,
                        principalTable: "CultivationCycles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SeedingEvents_SeedReceiptLots_SeedLotId",
                        column: x => x.SeedLotId,
                        principalTable: "SeedReceiptLots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ZoneId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RaftId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CycleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WorkerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ContractorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Task = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    WorkDate = table.Column<DateOnly>(type: "date", nullable: false),
                    PaymentType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ContractAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    ExpectedCompletionDate = table.Column<DateOnly>(type: "date", nullable: true),
                    AcceptanceDate = table.Column<DateOnly>(type: "date", nullable: true),
                    AcceptancePassed = table.Column<bool>(type: "bit", nullable: true),
                    AcceptanceEvidenceUrls = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CancelledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancelledByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CancellationReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkAssignments", x => x.Id);
                    table.CheckConstraint("CK_WorkAssignment_PaymentType", "([PaymentType] = N'DailyWage' AND (CASE WHEN [EmployeeId] IS NULL THEN 0 ELSE 1 END + CASE WHEN [WorkerId] IS NULL THEN 0 ELSE 1 END) = 1) OR ([PaymentType] = N'TaskContract' AND [ContractorId] IS NOT NULL AND [ContractAmount] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_WorkAssignments_Accounts_CancelledByAccountId",
                        column: x => x.CancelledByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkAssignments_Contractors_ContractorId",
                        column: x => x.ContractorId,
                        principalTable: "Contractors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkAssignments_CultivationCycles_CycleId",
                        column: x => x.CycleId,
                        principalTable: "CultivationCycles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkAssignments_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkAssignments_FarmingZones_ZoneId",
                        column: x => x.ZoneId,
                        principalTable: "FarmingZones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkAssignments_Rafts_RaftId",
                        column: x => x.RaftId,
                        principalTable: "Rafts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkAssignments_SeasonalWorkers_WorkerId",
                        column: x => x.WorkerId,
                        principalTable: "SeasonalWorkers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CultivationLogEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ZoneId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CycleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LaneId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LanePosition = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    EventType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Observation = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Observer = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RecordedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EvidenceUrls = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CultivationLogEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CultivationLogEntries_Accounts_RecordedByAccountId",
                        column: x => x.RecordedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CultivationLogEntries_CultivationCycles_CycleId",
                        column: x => x.CycleId,
                        principalTable: "CultivationCycles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CultivationLogEntries_FarmingZones_ZoneId",
                        column: x => x.ZoneId,
                        principalTable: "FarmingZones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CultivationLogEntries_Lanes_LaneId",
                        column: x => x.LaneId,
                        principalTable: "Lanes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RaftMaterialLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MaterialType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Position = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Specification = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    Unit = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    LineAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RaftMaterialLines", x => x.Id);
                    table.CheckConstraint("CK_RaftMaterialLine_LineAmount_Thousands", "[LineAmount] % 1000 = 0");
                    table.CheckConstraint("CK_RaftMaterialLine_Quantity", "[Quantity] > 0");
                    table.CheckConstraint("CK_RaftMaterialLine_UnitPrice", "[UnitPrice] >= 0");
                    table.ForeignKey(
                        name: "FK_RaftMaterialLines_RaftMaterialRecords_RecordId",
                        column: x => x.RecordId,
                        principalTable: "RaftMaterialRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PayLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WorkerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AttendanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AssignmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PayRuleVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PayDate = table.Column<DateOnly>(type: "date", nullable: false),
                    LineType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    SettlementId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayLines", x => x.Id);
                    table.CheckConstraint("CK_PayLine_Amount_Thousands", "[Amount] % 1000 = 0");
                    table.ForeignKey(
                        name: "FK_PayLines_AttendanceRecords_AttendanceId",
                        column: x => x.AttendanceId,
                        principalTable: "AttendanceRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PayLines_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PayLines_LaborSettlements_SettlementId",
                        column: x => x.SettlementId,
                        principalTable: "LaborSettlements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PayLines_PayRuleVersions_PayRuleVersionId",
                        column: x => x.PayRuleVersionId,
                        principalTable: "PayRuleVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PayLines_SeasonalWorkers_WorkerId",
                        column: x => x.WorkerId,
                        principalTable: "SeasonalWorkers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PayLines_WorkAssignments_AssignmentId",
                        column: x => x.AssignmentId,
                        principalTable: "WorkAssignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerCredits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceSaleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceAdjustmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceReceiptId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerCredits", x => x.Id);
                    table.CheckConstraint("CK_CustomerCredit_Amount_Thousands", "[Amount] % 1000 = 0");
                    table.ForeignKey(
                        name: "FK_CustomerCredits_CustomerReceipts_SourceReceiptId",
                        column: x => x.SourceReceiptId,
                        principalTable: "CustomerReceipts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerCredits_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerRefunds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreditId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Recipient = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AmountPaid = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    RefundDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Method = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    TransferReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    DocumentUrls = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DeduplicationKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerRefunds", x => x.Id);
                    table.CheckConstraint("CK_CustomerRefund_Amount_Positive", "[AmountPaid] > 0");
                    table.CheckConstraint("CK_CustomerRefund_AmountPaid_Thousands", "[AmountPaid] % 1000 = 0");
                    table.ForeignKey(
                        name: "FK_CustomerRefunds_CustomerCredits_CreditId",
                        column: x => x.CreditId,
                        principalTable: "CustomerCredits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerRefunds_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "HarvestEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ZoneId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HarvestDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EstimatedKg = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: true),
                    LotPhotoUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RecordedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HarvestEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HarvestEvents_Accounts_RecordedByAccountId",
                        column: x => x.RecordedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HarvestEvents_FarmingZones_ZoneId",
                        column: x => x.ZoneId,
                        principalTable: "FarmingZones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HarvestEvents_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LaneSegments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CycleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LaneId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SegmentType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    FromPosition = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    ToPosition = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    SegmentDate = table.Column<DateOnly>(type: "date", nullable: false),
                    RecordedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConfirmedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SeedingId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PlannedKg = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: true),
                    HarvestEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LotId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PlanDeviationReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    LogId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WriteOffReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    WriteOffPhotoUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LaneSegments", x => x.Id);
                    table.CheckConstraint("CK_LaneSegment_Positions", "[ToPosition] > [FromPosition] AND [FromPosition] >= 0");
                    table.ForeignKey(
                        name: "FK_LaneSegments_Accounts_ConfirmedByAccountId",
                        column: x => x.ConfirmedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LaneSegments_Accounts_RecordedByAccountId",
                        column: x => x.RecordedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LaneSegments_CultivationCycles_CycleId",
                        column: x => x.CycleId,
                        principalTable: "CultivationCycles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LaneSegments_CultivationLogEntries_LogId",
                        column: x => x.LogId,
                        principalTable: "CultivationLogEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LaneSegments_HarvestEvents_HarvestEventId",
                        column: x => x.HarvestEventId,
                        principalTable: "HarvestEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LaneSegments_Lanes_LaneId",
                        column: x => x.LaneId,
                        principalTable: "Lanes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LaneSegments_SeedingEvents_SeedingId",
                        column: x => x.SeedingId,
                        principalTable: "SeedingEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MessageDeliveryAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SentByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SaleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AdjustmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PublicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReceiptId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RetryOfAttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Recipient = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    DocumentType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DocumentVersion = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ContentSnapshot = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RespondedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ProviderMessageId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ProviderResponseCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    StatusId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageDeliveryAttempts", x => x.Id);
                    table.CheckConstraint("CK_MessageDeliveryAttempt_OneSource", "(CASE WHEN [OrderId] IS NULL THEN 0 ELSE 1 END + CASE WHEN [SaleId] IS NULL THEN 0 ELSE 1 END + CASE WHEN [AdjustmentId] IS NULL THEN 0 ELSE 1 END + CASE WHEN [PublicationId] IS NULL THEN 0 ELSE 1 END + CASE WHEN [ReceiptId] IS NULL THEN 0 ELSE 1 END) = 1");
                    table.ForeignKey(
                        name: "FK_MessageDeliveryAttempts_Accounts_SentByAccountId",
                        column: x => x.SentByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MessageDeliveryAttempts_CustomerReceipts_ReceiptId",
                        column: x => x.ReceiptId,
                        principalTable: "CustomerReceipts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MessageDeliveryAttempts_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MessageDeliveryAttempts_MessageDeliveryAttempts_RetryOfAttemptId",
                        column: x => x.RetryOfAttemptId,
                        principalTable: "MessageDeliveryAttempts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MessageDeliveryAttempts_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PhysicalReturns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SaleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReceivedQuantityKg = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    Condition = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    HoldingPlace = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    HoldingDeadline = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EvidenceUrls = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ResellableQuantityKg = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: true),
                    OtherDisposalQuantityKg = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: true),
                    AssessmentBasis = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    AssessedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhysicalReturns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PhysicalReturns_Accounts_AssessedByAccountId",
                        column: x => x.AssessedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PhysicalReturns_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PortHandovers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WeighingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PublicationIdAtHandover = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeliveredBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ReceivedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    HandedOverAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EvidenceUrls = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RecipientSnapshot = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsOffline = table.Column<bool>(type: "bit", nullable: false),
                    SynchronizedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PortHandovers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PortWeighings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Port = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    WeighedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ConfirmedKg = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    BuyerConfirmation = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    EvidenceUrls = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PortWeighings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QualityClaims",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SaleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AffectedPortion = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ComplaintType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    RequestedHandling = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ReceivingChannel = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    RecordedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EvidenceUrls = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Decision = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    DecidedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QualityClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QualityClaims_Accounts_DecidedByAccountId",
                        column: x => x.DecidedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_QualityClaims_Accounts_RecordedByAccountId",
                        column: x => x.RecordedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_QualityClaims_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReceiptAllocations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReceiptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SaleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AllocatedAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReceiptAllocations", x => x.Id);
                    table.CheckConstraint("CK_ReceiptAllocation_AllocatedAmount_Thousands", "[AllocatedAmount] % 1000 = 0");
                    table.CheckConstraint("CK_ReceiptAllocation_Amount_Positive", "[AllocatedAmount] > 0");
                    table.ForeignKey(
                        name: "FK_ReceiptAllocations_CustomerReceipts_ReceiptId",
                        column: x => x.ReceiptId,
                        principalTable: "CustomerReceipts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SaleAdjustments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SaleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClaimId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    NewWeighingId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AdjustmentType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Mode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Percentage = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    NegotiationBasis = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    EvidenceUrls = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Decision = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    DecidedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ApprovedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SaleAdjustments", x => x.Id);
                    table.CheckConstraint("CK_SaleAdjustment_Amount_Thousands", "[Amount] % 1000 = 0");
                    table.ForeignKey(
                        name: "FK_SaleAdjustments_Accounts_ApprovedByAccountId",
                        column: x => x.ApprovedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SaleAdjustments_Accounts_DecidedByAccountId",
                        column: x => x.DecidedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SaleAdjustments_PortWeighings_NewWeighingId",
                        column: x => x.NewWeighingId,
                        principalTable: "PortWeighings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SaleAdjustments_QualityClaims_ClaimId",
                        column: x => x.ClaimId,
                        principalTable: "QualityClaims",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SaleAdjustments_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SalesOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ZoneId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GoodsSource = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    SourceLotId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceReturnId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    QuantityAllocatedFromReturn = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: true),
                    RequestedQuantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    RequiredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    OriginalTerms = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    RequestChannel = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ChannelEvidence = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    RecordedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TraderProposedChanges = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    AgreementChannel = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    AgreedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AgreementBasis = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesOrders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SalesOrders_Accounts_RecordedByAccountId",
                        column: x => x.RecordedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrders_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrders_FarmingZones_ZoneId",
                        column: x => x.ZoneId,
                        principalTable: "FarmingZones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrders_PhysicalReturns_SourceReturnId",
                        column: x => x.SourceReturnId,
                        principalTable: "PhysicalReturns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrders_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrders_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TraceabilityLots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LotCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    QrCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    InitialOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DisposalMethod = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DisposalReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    DisposedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DisposedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TraceabilityLots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TraceabilityLots_Accounts_DisposedByAccountId",
                        column: x => x.DisposedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TraceabilityLots_SalesOrders_InitialOrderId",
                        column: x => x.InitialOrderId,
                        principalTable: "SalesOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SaleTransactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LotId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WeighingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    TotalAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    FinalizedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SalesReceiptUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    PriceAgreedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgreementBasis = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    DeduplicationKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IsOfflineFinalized = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SaleTransactions", x => x.Id);
                    table.CheckConstraint("CK_SaleTransaction_TotalAmount_Thousands", "[TotalAmount] % 1000 = 0");
                    table.ForeignKey(
                        name: "FK_SaleTransactions_Accounts_PriceAgreedByAccountId",
                        column: x => x.PriceAgreedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SaleTransactions_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SaleTransactions_PortWeighings_WeighingId",
                        column: x => x.WeighingId,
                        principalTable: "PortWeighings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SaleTransactions_SalesOrders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "SalesOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SaleTransactions_TraceabilityLots_LotId",
                        column: x => x.LotId,
                        principalTable: "TraceabilityLots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TraceCorrections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Field = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    OldValue = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    NewValue = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    EvidenceUrls = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CorrectedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApprovedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PublicationImpact = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TraceCorrections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TraceCorrections_Accounts_ApprovedByAccountId",
                        column: x => x.ApprovedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TraceCorrections_Accounts_CorrectedByAccountId",
                        column: x => x.CorrectedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TraceCorrections_TraceabilityLots_LotId",
                        column: x => x.LotId,
                        principalTable: "TraceabilityLots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TracePublicationVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    PublicContent = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CooperativeSnapshot = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ValidFrom = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ValidTo = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReviewPassed = table.Column<bool>(type: "bit", nullable: true),
                    ReviewedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReviewedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReviewErrors = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ReviewEvidenceUrls = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PublishedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PublishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    WithdrawnByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WithdrawnAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    WithdrawalReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TracePublicationVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TracePublicationVersions_Accounts_PublishedByAccountId",
                        column: x => x.PublishedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TracePublicationVersions_Accounts_ReviewedByAccountId",
                        column: x => x.ReviewedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TracePublicationVersions_Accounts_WithdrawnByAccountId",
                        column: x => x.WithdrawnByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TracePublicationVersions_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TracePublicationVersions_TraceabilityLots_LotId",
                        column: x => x.LotId,
                        principalTable: "TraceabilityLots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "Statuses",
                columns: new[] { "Id", "Code", "DisplayColor", "DisplayName", "Group", "IsFinal", "IsInitial", "Meaning", "SortOrder" },
                values: new object[,]
                {
                    { 101, "IN_USE", null, "In Use", "USAGE", false, true, "Selectable for new records.", 1 },
                    { 102, "OUT_OF_USE", null, "Discontinued", "USAGE", false, false, "Not shown when selecting new records; old data stays unchanged.", 2 },
                    { 201, "DRAFT", null, "Preliminary", "COOPERATIVE", false, true, "The System Administrator has just set up the cooperative; the profile lacks legal information.", 1 },
                    { 202, "COMPLETED", null, "Completed", "COOPERATIVE", false, false, "The Cooperative Owner has entered all mandatory cooperative profile information.", 2 },
                    { 301, "ACTIVE", null, "Active", "ACCOUNT", false, true, "Can log in according to effective zone–role pairs.", 1 },
                    { 302, "LOCKED", null, "Locked", "ACCOUNT", false, false, "Cannot log in; data and history stay unchanged.", 2 },
                    { 401, "WORKING", null, "Employed", "EMPLOYEE", false, true, "The employee is working and can be assigned work and recorded in attendance.", 1 },
                    { 402, "LEFT", null, "Left", "EMPLOYEE", false, false, "No new work or attendance; wage and salary history stays unchanged.", 2 },
                    { 501, "EFFECTIVE", null, "Effective", "ACCESS_GRANT", false, true, "The account has the role's permissions in the assigned zone.", 1 },
                    { 502, "REVOKED", null, "Revoked", "ACCESS_GRANT", true, false, "The grant no longer applies; re-granting creates a new grant.", 2 },
                    { 601, "OPEN", null, "Open", "CULTIVATION_CYCLE", false, true, "The cycle is in progress; seeding, logging and harvesting are allowed.", 1 },
                    { 602, "CLOSED", null, "Closed", "CULTIVATION_CYCLE", true, false, "The cycle has ended; the raft can open a new cycle.", 2 },
                    { 701, "DRAFT", null, "Draft", "WATER_MEASUREMENT", false, true, "OCR or manually entered values not yet confirmed; not used for reports or alerts.", 1 },
                    { 702, "CONFIRMED", null, "Confirmed", "WATER_MEASUREMENT", false, false, "Checked against the photo and confirmed; used for alerts and reports.", 2 },
                    { 703, "SUPERSEDED", null, "Corrected", "WATER_MEASUREMENT", true, false, "Replaced by a new correction version; the old record is kept for reference.", 3 },
                    { 801, "PENDING_REVIEW", null, "Pending Review", "SALES_ORDER", false, true, "The purchase request has just been sent and not yet reviewed by the cooperative.", 1 },
                    { 802, "AWAITING_CUSTOMER", null, "Awaiting Customer Agreement", "SALES_ORDER", false, false, "The cooperative proposes a change of quantity or time, awaiting the trader's agreement.", 2 },
                    { 803, "CONFIRMED", null, "Confirmed", "SALES_ORDER", false, false, "The cooperative has confirmed the product, quantity and order zone; the price is not yet agreed.", 3 },
                    { 804, "HARVESTING", null, "Harvesting", "SALES_ORDER", false, false, "A harvest plan or a first harvest exists for the order.", 4 },
                    { 805, "HARVESTED", null, "Harvested", "SALES_ORDER", false, false, "The actual harvest has been confirmed and the traceability lot created, or the order took over the lot of a cancelled order.", 5 },
                    { 806, "HANDED_OVER", null, "Handed Over", "SALES_ORDER", false, false, "The goods have been weighed and handed over at the port.", 6 },
                    { 807, "SOLD", null, "Sale Finalized", "SALES_ORDER", false, false, "The sale transaction has an agreed price and weight.", 7 },
                    { 808, "RETURN_REQUESTED", null, "Return Requested", "SALES_ORDER", false, false, "The trader has requested return of the whole received order, awaiting the Cooperative Owner's decision.", 8 },
                    { 809, "RETURN_ACCEPTED", null, "Return Accepted", "SALES_ORDER", false, false, "The Cooperative Owner accepted return of the whole order, awaiting the goods.", 9 },
                    { 810, "RETURNED", null, "Return Received", "SALES_ORDER", true, false, "The order's returned goods have arrived and been recorded.", 10 },
                    { 811, "REJECTED", null, "Rejected", "SALES_ORDER", true, false, "The cooperative rejected, or the trader did not agree to the proposed changes.", 11 },
                    { 812, "CANCELLED", null, "Cancelled", "SALES_ORDER", true, false, "The trader withdrew the request or cancelled the order before handover.", 12 },
                    { 901, "DRAFT", null, "Draft", "HARVEST_EVENT", false, true, "Lane portions and estimates are being recorded during the day.", 1 },
                    { 902, "CONFIRMED", null, "Confirmed", "HARVEST_EVENT", true, false, "Confirmed at the end of the day; later edits go through correction.", 2 },
                    { 1001, "DRAFT", null, "Draft", "TRACE_PUBLICATION", false, true, "The traceability record is being prepared or the review has not passed.", 1 },
                    { 1002, "PASSED", null, "Passed", "TRACE_PUBLICATION", false, false, "Review passed, awaiting publication.", 2 },
                    { 1003, "PUBLISHED", null, "Published", "TRACE_PUBLICATION", false, false, "Currently shown on the public QR page.", 3 },
                    { 1004, "NEEDS_REVIEW", null, "Needs Re-review", "TRACE_PUBLICATION", false, false, "Published data has just been corrected; no handover, resale or portal synchronization.", 4 },
                    { 1005, "SUPERSEDED", null, "Superseded", "TRACE_PUBLICATION", true, false, "Replaced by a newly published version.", 5 },
                    { 1006, "WITHDRAWN", null, "Withdrawn", "TRACE_PUBLICATION", true, false, "Publication withdrawn; the QR page shows the withdrawn status.", 6 },
                    { 1101, "PENDING", null, "Awaiting Decision", "QUALITY_CLAIM", false, true, "The claim has been submitted; the Cooperative Owner has not decided yet.", 1 },
                    { 1102, "IN_PROGRESS", null, "In Progress", "QUALITY_CLAIM", false, false, "A discount or return of the whole order has been decided and is being carried out.", 2 },
                    { 1103, "CLOSED", null, "Closed", "QUALITY_CLAIM", true, false, "Rejected, discount recorded, returned goods resolved, or return acceptance cancelled.", 3 },
                    { 1201, "HOLDING", null, "In Temporary Holding", "PHYSICAL_RETURN", false, true, "The returned goods have arrived and are held awaiting assessment.", 1 },
                    { 1202, "ASSESSED", null, "Assessed", "PHYSICAL_RETURN", false, false, "The resellable portion and the portion for other disposal have been assessed.", 2 },
                    { 1203, "DISPOSED", null, "Resolved", "PHYSICAL_RETURN", true, false, "The resellable portion is linked to new orders and the rest otherwise disposed of.", 3 },
                    { 1301, "DRAFT", null, "Draft", "SALE_ADJUSTMENT", false, true, "Awaiting the Cooperative Owner's approval; no effect on debt yet.", 1 },
                    { 1302, "APPROVED", null, "Approved", "SALE_ADJUSTMENT", true, false, "Approved; changes the receivable or records a credit/amount to refund.", 2 },
                    { 1303, "REJECTED", null, "Rejected", "SALE_ADJUSTMENT", true, false, "Not approved; debt unchanged.", 3 },
                    { 1401, "DRAFT", null, "Draft", "PAY_RULE", false, true, "A new salary or wage rate awaiting approval; not yet used for calculation.", 1 },
                    { 1402, "APPROVED", null, "Approved", "PAY_RULE", true, false, "Approved; used from the effective date.", 2 },
                    { 1403, "REJECTED", null, "Rejected", "PAY_RULE", true, false, "Not approved; the current rate stays unchanged.", 3 },
                    { 1501, "DRAFT", null, "Draft", "LABOR_SETTLEMENT", false, true, "The timesheet or payroll is being prepared, awaiting approval.", 1 },
                    { 1502, "APPROVED", null, "Approved", "LABOR_SETTLEMENT", false, false, "Approved by the Cooperative Owner; the basis for payment.", 2 },
                    { 1503, "PAID", null, "Fully Paid", "LABOR_SETTLEMENT", true, false, "Total paid equals the approved obligation.", 3 },
                    { 1601, "SENDING", null, "Sending", "MESSAGE_DELIVERY", false, true, "Handed to the channel provider, no response yet.", 1 },
                    { 1602, "SENT", null, "Sent", "MESSAGE_DELIVERY", true, false, "The provider reports successful delivery.", 2 },
                    { 1603, "FAILED", null, "Failed", "MESSAGE_DELIVERY", true, false, "The provider reports an error or the response timeout expires.", 3 }
                });

            migrationBuilder.InsertData(
                table: "StatusTransitions",
                columns: new[] { "Id", "AllowedPerformers", "FromStatusId", "GuardCondition", "ReasonRequired", "ToStatusId", "TriggerEvent", "UseCases" },
                values: new object[,]
                {
                    { 1, "Cooperative Owner; Zone Manager", 101, "Not blocked by the entity's Discontinued condition (section 3.4.3).", false, 102, "Action", "UC12, UC15, UC18, UC20, UC22, UC42, UC55" },
                    { 2, "Cooperative Owner; Zone Manager", 102, "Return the item to use.", false, 101, "Action", "UC12, UC15, UC18, UC20, UC22, UC42, UC55" },
                    { 3, "Cooperative Owner", 201, "All mandatory fields of the cooperative profile are filled.", false, 202, "Action", "UC09" },
                    { 4, "Cooperative Owner; System Administrator", 301, "Lock the account.", true, 302, "Action", "UC11, UC74" },
                    { 5, "Cooperative Owner; System Administrator", 302, "Unlock after verification; the System Administrator unlocks only accounts it locked itself.", true, 301, "Action", "UC11, UC74" },
                    { 6, "Cooperative Owner", 401, "The employee leaves.", false, 402, "Action", "UC10" },
                    { 7, "Cooperative Owner", 402, "The employee returns to work.", false, 401, "Action", "UC10" },
                    { 8, "Cooperative Owner", 501, "Revoke the zone–role pair; Cooperative Owner only.", true, 502, "Action", "UC11" },
                    { 9, "Cooperative Owner; Zone Manager", 601, "The seeded portion has been fully harvested or written off (BR-21).", false, 602, "Action", "UC27" },
                    { 10, "Cooperative Owner; Zone Manager", 701, "Check against the photo and confirm the values.", false, 702, "Action", "UC26" },
                    { 11, "Cooperative Owner; Zone Manager", 702, "Save a new correction version.", true, 703, "Action", "UC26" },
                    { 12, "Cooperative Owner", 801, "Confirm the product, quantity and order zone.", false, 803, "Action", "UC44" },
                    { 13, "Cooperative Owner", 801, "Propose a change of quantity or time.", false, 802, "Action", "UC44" },
                    { 14, "Cooperative Owner", 801, "The cooperative cannot fulfill.", true, 811, "Action", "UC44" },
                    { 15, "Cooperative Owner", 801, "The trader withdraws the purchase request before review.", true, 812, "Action", "UC44" },
                    { 16, "Cooperative Owner", 802, "The trader agrees; channel, time and basis of agreement recorded.", false, 803, "Action", "UC44" },
                    { 17, "Cooperative Owner", 802, "The trader does not agree.", true, 811, "Action", "UC44" },
                    { 18, "Cooperative Owner", 802, "The trader withdraws the request while changes await agreement.", true, 812, "Action", "UC44" },
                    { 19, "Cooperative Owner", 803, "The trader cancels the order.", true, 812, "Action", "UC44" },
                    { 20, "Cooperative Owner; Zone Manager", 803, "Plan the harvest, or record the first harvest when no plan is made.", false, 804, "Action", "UC30, UC31" },
                    { 21, "Cooperative Owner; Zone Manager", 804, "Harvest Done selected; every harvest of the order is confirmed.", false, 805, "Action", "UC31" },
                    { 22, "Cooperative Owner", 804, "The trader drops the order during harvest.", true, 812, "Action", "UC44" },
                    { 23, "Cooperative Owner", 805, "The trader drops the order after harvest, before handover.", true, 812, "Action", "UC44" },
                    { 24, "Cooperative Owner; Zone Manager", 805, "Weigh and hand over at the port.", false, 806, "Action", "UC45" },
                    { 25, "Cooperative Owner; Zone Manager", 803, "Resale order of returned goods: no harvest involved.", false, 806, "Action", "UC45" },
                    { 26, "Cooperative Owner", 803, "An order without a lot takes over the harvested lot of a cancelled order of the same product.", false, 805, "Action", "UC44" },
                    { 27, "Cooperative Owner; Zone Manager", 806, "Agree the price and weight.", false, 807, "Action", "UC48" },
                    { 28, "Trader; Cooperative Owner", 807, "Within 24 hours of sale finalization; no Draft/Approved discount and no return request yet.", false, 808, "Action", "UC51" },
                    { 29, "Cooperative Owner", 808, "Accept return of the whole order; the trader is notified.", false, 809, "Action", "UC52" },
                    { 30, "Cooperative Owner", 808, "Reject the return or decide on a discount instead.", true, 807, "Action", "UC52" },
                    { 31, "Cooperative Owner; Zone Manager", 809, "The returned goods have arrived and are recorded as received.", false, 810, "Action", "UC52" },
                    { 32, "Cooperative Owner", 809, "Trader unreachable or goods not returned: cancel the return acceptance.", true, 807, "Action", "UC52" },
                    { 33, "Cooperative Owner; Zone Manager", 901, "Confirm at the end of the harvest day.", false, 902, "Action", "UC31" },
                    { 34, "Cooperative Owner; Zone Manager", 1001, "Review passed.", false, 1002, "Action", "UC32" },
                    { 35, "Cooperative Owner; Zone Manager", 1002, "Publish the passed version.", false, 1003, "Action", "UC32" },
                    { 36, "System", 1002, "The lot's source data changed after review and before publication.", false, 1001, "System", "UC32" },
                    { 37, "System", 1003, "A correction of source data or weight affects published content.", false, 1004, "System", "UC39" },
                    { 38, "Cooperative Owner; Zone Manager", 1003, "Published weight or source proven wrong, or the lot can no longer be used.", true, 1006, "Action", "UC32" },
                    { 39, "System", 1004, "A new version of the same lot is published.", false, 1005, "System", "UC32" },
                    { 40, "Cooperative Owner; Zone Manager", 1004, "Published weight or source proven wrong, or the lot can no longer be used.", true, 1006, "Action", "UC32" },
                    { 41, "Cooperative Owner", 1101, "Reject the claim.", true, 1103, "Action", "UC52" },
                    { 42, "Cooperative Owner", 1101, "Decide on a discount, or accept return of the whole order.", false, 1102, "Action", "UC52" },
                    { 43, "System", 1102, "Discount approved/rejected, returned goods resolved, or return acceptance cancelled.", false, 1103, "System", "UC53, UC52" },
                    { 44, "Cooperative Owner", 1201, "Assess the resellable portion.", false, 1202, "Action", "UC52" },
                    { 45, "System", 1202, "All resold or other disposal recorded.", false, 1203, "System", "UC52" },
                    { 46, "Cooperative Owner", 1301, "The total decrease does not exceed the remaining adjustable amount.", false, 1302, "Action", "UC53, UC39" },
                    { 47, "Cooperative Owner", 1301, "The Cooperative Owner does not approve the adjustment.", true, 1303, "Action", "UC53, UC39" },
                    { 48, "Cooperative Owner", 1401, "The effective period does not overlap an approved rate of the same subject.", false, 1402, "Action", "UC58" },
                    { 49, "Cooperative Owner", 1401, "The Cooperative Owner does not approve the new rate.", true, 1403, "Action", "UC58" },
                    { 50, "Cooperative Owner", 1501, "Review and approve the sheet.", false, 1502, "Action", "UC60" },
                    { 51, "Cooperative Owner", 1502, "Reopen for editing; audit log written (before – after).", true, 1501, "Action", "UC60" },
                    { 52, "System", 1502, "Total paid equals the approved obligation.", false, 1503, "System", "UC61" },
                    { 53, "System", 1601, "Success response from Zalo OA, Email Service or SMS Service.", false, 1602, "System", "UC70" },
                    { 54, "System", 1601, "Error response or timeout.", false, 1603, "System; time", "UC70" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccessGrants_AccountId_StatusId",
                table: "AccessGrants",
                columns: new[] { "AccountId", "StatusId" });

            migrationBuilder.CreateIndex(
                name: "IX_AccessGrants_GrantedByAccountId",
                table: "AccessGrants",
                column: "GrantedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_AccessGrants_RevokedByAccountId",
                table: "AccessGrants",
                column: "RevokedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_AccessGrants_RoleId",
                table: "AccessGrants",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_AccessGrants_StatusId",
                table: "AccessGrants",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "IX_AccessGrants_ZoneId",
                table: "AccessGrants",
                column: "ZoneId");

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_Email",
                table: "Accounts",
                column: "Email",
                unique: true,
                filter: "[Email] IS NOT NULL AND [EmailVerifiedAt] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_PhoneNumber",
                table: "Accounts",
                column: "PhoneNumber",
                unique: true,
                filter: "[PhoneNumber] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_StatusId",
                table: "Accounts",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_Username",
                table: "Accounts",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceRecords_ContractorId",
                table: "AttendanceRecords",
                column: "ContractorId");

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceRecords_EmployeeId_WorkDate",
                table: "AttendanceRecords",
                columns: new[] { "EmployeeId", "WorkDate" },
                unique: true,
                filter: "[EmployeeId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceRecords_RecordedByAccountId",
                table: "AttendanceRecords",
                column: "RecordedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceRecords_WorkerId_WorkDate",
                table: "AttendanceRecords",
                columns: new[] { "WorkerId", "WorkDate" },
                unique: true,
                filter: "[WorkerId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_ActorAccountId",
                table: "AuditEvents",
                column: "ActorAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_OccurredAt",
                table: "AuditEvents",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_RecordType_RecordId",
                table: "AuditEvents",
                columns: new[] { "RecordType", "RecordId" });

            migrationBuilder.CreateIndex(
                name: "IX_Contractors_StatusId",
                table: "Contractors",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "IX_Cooperatives_StatusId",
                table: "Cooperatives",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "IX_Cooperatives_TaxCode",
                table: "Cooperatives",
                column: "TaxCode",
                unique: true,
                filter: "[TaxCode] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CultivationCycles_PlannedSeedTypeId",
                table: "CultivationCycles",
                column: "PlannedSeedTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_CultivationCycles_StatusId",
                table: "CultivationCycles",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "UX_CultivationCycles_RaftId_Open",
                table: "CultivationCycles",
                column: "RaftId",
                unique: true,
                filter: "[StatusId] = 601");

            migrationBuilder.CreateIndex(
                name: "IX_CultivationLogEntries_CycleId",
                table: "CultivationLogEntries",
                column: "CycleId");

            migrationBuilder.CreateIndex(
                name: "IX_CultivationLogEntries_LaneId",
                table: "CultivationLogEntries",
                column: "LaneId");

            migrationBuilder.CreateIndex(
                name: "IX_CultivationLogEntries_RecordedByAccountId",
                table: "CultivationLogEntries",
                column: "RecordedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_CultivationLogEntries_ZoneId_OccurredAt",
                table: "CultivationLogEntries",
                columns: new[] { "ZoneId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerCredits_CustomerId",
                table: "CustomerCredits",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerCredits_SourceAdjustmentId",
                table: "CustomerCredits",
                column: "SourceAdjustmentId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerCredits_SourceReceiptId",
                table: "CustomerCredits",
                column: "SourceReceiptId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerCredits_SourceSaleId",
                table: "CustomerCredits",
                column: "SourceSaleId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerReceipts_CustomerId",
                table: "CustomerReceipts",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerReceipts_DeduplicationKey",
                table: "CustomerReceipts",
                column: "DeduplicationKey",
                unique: true,
                filter: "[DeduplicationKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerReceipts_Method_ReferenceCode",
                table: "CustomerReceipts",
                columns: new[] { "Method", "ReferenceCode" },
                unique: true,
                filter: "[ReferenceCode] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerRefunds_CreditId",
                table: "CustomerRefunds",
                column: "CreditId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerRefunds_CustomerId",
                table: "CustomerRefunds",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerRefunds_DeduplicationKey",
                table: "CustomerRefunds",
                column: "DeduplicationKey",
                unique: true,
                filter: "[DeduplicationKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_AccountId",
                table: "Customers",
                column: "AccountId",
                unique: true,
                filter: "[AccountId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_CooperativeId",
                table: "Customers",
                column: "CooperativeId");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_MergedIntoCustomerId",
                table: "Customers",
                column: "MergedIntoCustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_PhoneNumber",
                table: "Customers",
                column: "PhoneNumber");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_StatusId",
                table: "Customers",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_ZaloAccountId",
                table: "Customers",
                column: "ZaloAccountId",
                unique: true,
                filter: "[ZaloAccountId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentZoneScopes_ZoneId",
                table: "DocumentZoneScopes",
                column: "ZoneId");

            migrationBuilder.CreateIndex(
                name: "IX_Employees_AccountId",
                table: "Employees",
                column: "AccountId",
                unique: true,
                filter: "[AccountId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Employees_CooperativeId",
                table: "Employees",
                column: "CooperativeId");

            migrationBuilder.CreateIndex(
                name: "IX_Employees_StatusId",
                table: "Employees",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "IX_FarmingZones_Code",
                table: "FarmingZones",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FarmingZones_CooperativeId",
                table: "FarmingZones",
                column: "CooperativeId");

            migrationBuilder.CreateIndex(
                name: "IX_FarmingZones_StatusId",
                table: "FarmingZones",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "IX_HarvestEvents_OrderId",
                table: "HarvestEvents",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_HarvestEvents_RecordedByAccountId",
                table: "HarvestEvents",
                column: "RecordedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_HarvestEvents_StatusId",
                table: "HarvestEvents",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "IX_HarvestEvents_ZoneId",
                table: "HarvestEvents",
                column: "ZoneId");

            migrationBuilder.CreateIndex(
                name: "IX_LaborPayments_OriginalPaymentId",
                table: "LaborPayments",
                column: "OriginalPaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_LaborPayments_SettlementId",
                table: "LaborPayments",
                column: "SettlementId");

            migrationBuilder.CreateIndex(
                name: "IX_LaborSettlements_ApprovedByAccountId",
                table: "LaborSettlements",
                column: "ApprovedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_LaborSettlements_ContractorId",
                table: "LaborSettlements",
                column: "ContractorId");

            migrationBuilder.CreateIndex(
                name: "IX_LaborSettlements_EmployeeId",
                table: "LaborSettlements",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_LaborSettlements_PreparedByAccountId",
                table: "LaborSettlements",
                column: "PreparedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_LaborSettlements_StatusId",
                table: "LaborSettlements",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "IX_Lanes_RaftId_LaneNumber",
                table: "Lanes",
                columns: new[] { "RaftId", "LaneNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LaneSegments_ConfirmedByAccountId",
                table: "LaneSegments",
                column: "ConfirmedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_LaneSegments_CycleId_LaneId_SegmentType",
                table: "LaneSegments",
                columns: new[] { "CycleId", "LaneId", "SegmentType" });

            migrationBuilder.CreateIndex(
                name: "IX_LaneSegments_HarvestEventId",
                table: "LaneSegments",
                column: "HarvestEventId");

            migrationBuilder.CreateIndex(
                name: "IX_LaneSegments_LaneId",
                table: "LaneSegments",
                column: "LaneId");

            migrationBuilder.CreateIndex(
                name: "IX_LaneSegments_LogId",
                table: "LaneSegments",
                column: "LogId");

            migrationBuilder.CreateIndex(
                name: "IX_LaneSegments_LotId",
                table: "LaneSegments",
                column: "LotId");

            migrationBuilder.CreateIndex(
                name: "IX_LaneSegments_OrderId",
                table: "LaneSegments",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_LaneSegments_RecordedByAccountId",
                table: "LaneSegments",
                column: "RecordedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_LaneSegments_SeedingId",
                table: "LaneSegments",
                column: "SeedingId");

            migrationBuilder.CreateIndex(
                name: "IX_LoginSessions_AccountId",
                table: "LoginSessions",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_LoginSessions_PreviousRefreshTokenHash",
                table: "LoginSessions",
                column: "PreviousRefreshTokenHash");

            migrationBuilder.CreateIndex(
                name: "IX_LoginSessions_RefreshTokenHash",
                table: "LoginSessions",
                column: "RefreshTokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MessageDeliveryAttempts_AdjustmentId",
                table: "MessageDeliveryAttempts",
                column: "AdjustmentId");

            migrationBuilder.CreateIndex(
                name: "IX_MessageDeliveryAttempts_CustomerId",
                table: "MessageDeliveryAttempts",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_MessageDeliveryAttempts_OrderId",
                table: "MessageDeliveryAttempts",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_MessageDeliveryAttempts_PublicationId",
                table: "MessageDeliveryAttempts",
                column: "PublicationId");

            migrationBuilder.CreateIndex(
                name: "IX_MessageDeliveryAttempts_ReceiptId",
                table: "MessageDeliveryAttempts",
                column: "ReceiptId");

            migrationBuilder.CreateIndex(
                name: "IX_MessageDeliveryAttempts_RetryOfAttemptId",
                table: "MessageDeliveryAttempts",
                column: "RetryOfAttemptId");

            migrationBuilder.CreateIndex(
                name: "IX_MessageDeliveryAttempts_SaleId",
                table: "MessageDeliveryAttempts",
                column: "SaleId");

            migrationBuilder.CreateIndex(
                name: "IX_MessageDeliveryAttempts_SentByAccountId",
                table: "MessageDeliveryAttempts",
                column: "SentByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_MessageDeliveryAttempts_StatusId",
                table: "MessageDeliveryAttempts",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_RecipientAccountId_ReadAt",
                table: "Notifications",
                columns: new[] { "RecipientAccountId", "ReadAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OysterSeedTypes_Code",
                table: "OysterSeedTypes",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OysterSeedTypes_StatusId",
                table: "OysterSeedTypes",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "IX_PayLines_AssignmentId",
                table: "PayLines",
                column: "AssignmentId");

            migrationBuilder.CreateIndex(
                name: "IX_PayLines_AttendanceId",
                table: "PayLines",
                column: "AttendanceId");

            migrationBuilder.CreateIndex(
                name: "IX_PayLines_EmployeeId",
                table: "PayLines",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_PayLines_PayRuleVersionId",
                table: "PayLines",
                column: "PayRuleVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_PayLines_SettlementId",
                table: "PayLines",
                column: "SettlementId");

            migrationBuilder.CreateIndex(
                name: "IX_PayLines_WorkerId",
                table: "PayLines",
                column: "WorkerId");

            migrationBuilder.CreateIndex(
                name: "IX_PayRuleVersions_ApprovedByAccountId",
                table: "PayRuleVersions",
                column: "ApprovedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_PayRuleVersions_EmployeeId",
                table: "PayRuleVersions",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_PayRuleVersions_EnteredByAccountId",
                table: "PayRuleVersions",
                column: "EnteredByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_PayRuleVersions_StatusId",
                table: "PayRuleVersions",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "IX_PayRuleVersions_SubjectType_EmployeeId_ValidFrom",
                table: "PayRuleVersions",
                columns: new[] { "SubjectType", "EmployeeId", "ValidFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_PhysicalReturns_AssessedByAccountId",
                table: "PhysicalReturns",
                column: "AssessedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_PhysicalReturns_LotId",
                table: "PhysicalReturns",
                column: "LotId");

            migrationBuilder.CreateIndex(
                name: "IX_PhysicalReturns_SaleId",
                table: "PhysicalReturns",
                column: "SaleId");

            migrationBuilder.CreateIndex(
                name: "IX_PhysicalReturns_StatusId",
                table: "PhysicalReturns",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "IX_PortHandovers_OrderId",
                table: "PortHandovers",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_PortHandovers_PublicationIdAtHandover",
                table: "PortHandovers",
                column: "PublicationIdAtHandover");

            migrationBuilder.CreateIndex(
                name: "IX_PortHandovers_WeighingId",
                table: "PortHandovers",
                column: "WeighingId");

            migrationBuilder.CreateIndex(
                name: "IX_PortWeighings_OrderId",
                table: "PortWeighings",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_Products_Code",
                table: "Products",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Products_StatusId",
                table: "Products",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityClaims_DecidedByAccountId",
                table: "QualityClaims",
                column: "DecidedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityClaims_RecordedByAccountId",
                table: "QualityClaims",
                column: "RecordedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityClaims_SaleId",
                table: "QualityClaims",
                column: "SaleId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityClaims_StatusId",
                table: "QualityClaims",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "IX_RaftLayoutVersions_RaftId_VersionNumber",
                table: "RaftLayoutVersions",
                columns: new[] { "RaftId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RaftMaterialLines_RecordId",
                table: "RaftMaterialLines",
                column: "RecordId");

            migrationBuilder.CreateIndex(
                name: "IX_RaftMaterialRecords_RecordedByAccountId",
                table: "RaftMaterialRecords",
                column: "RecordedByAccountId");

            migrationBuilder.CreateIndex(
                name: "UX_RaftMaterialRecords_RaftId_RaftCreation",
                table: "RaftMaterialRecords",
                column: "RaftId",
                unique: true,
                filter: "[RecordType] = N'RaftCreation'");

            migrationBuilder.CreateIndex(
                name: "IX_Rafts_StatusId",
                table: "Rafts",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "IX_Rafts_ZoneId_Code",
                table: "Rafts",
                columns: new[] { "ZoneId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReceiptAllocations_ReceiptId",
                table: "ReceiptAllocations",
                column: "ReceiptId");

            migrationBuilder.CreateIndex(
                name: "IX_ReceiptAllocations_SaleId",
                table: "ReceiptAllocations",
                column: "SaleId");

            migrationBuilder.CreateIndex(
                name: "IX_Roles_Name",
                table: "Roles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Roles_StatusId",
                table: "Roles",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "IX_SaleAdjustments_ApprovedByAccountId",
                table: "SaleAdjustments",
                column: "ApprovedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SaleAdjustments_ClaimId",
                table: "SaleAdjustments",
                column: "ClaimId");

            migrationBuilder.CreateIndex(
                name: "IX_SaleAdjustments_DecidedByAccountId",
                table: "SaleAdjustments",
                column: "DecidedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SaleAdjustments_NewWeighingId",
                table: "SaleAdjustments",
                column: "NewWeighingId");

            migrationBuilder.CreateIndex(
                name: "IX_SaleAdjustments_SaleId",
                table: "SaleAdjustments",
                column: "SaleId");

            migrationBuilder.CreateIndex(
                name: "IX_SaleAdjustments_StatusId",
                table: "SaleAdjustments",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_Code",
                table: "SalesOrders",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_CustomerId_StatusId",
                table: "SalesOrders",
                columns: new[] { "CustomerId", "StatusId" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_ProductId",
                table: "SalesOrders",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_RecordedByAccountId",
                table: "SalesOrders",
                column: "RecordedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_SourceLotId",
                table: "SalesOrders",
                column: "SourceLotId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_SourceReturnId",
                table: "SalesOrders",
                column: "SourceReturnId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_StatusId",
                table: "SalesOrders",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_ZoneId_StatusId",
                table: "SalesOrders",
                columns: new[] { "ZoneId", "StatusId" });

            migrationBuilder.CreateIndex(
                name: "IX_SaleTransactions_Code",
                table: "SaleTransactions",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SaleTransactions_CustomerId",
                table: "SaleTransactions",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_SaleTransactions_DeduplicationKey",
                table: "SaleTransactions",
                column: "DeduplicationKey",
                unique: true,
                filter: "[DeduplicationKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SaleTransactions_LotId",
                table: "SaleTransactions",
                column: "LotId");

            migrationBuilder.CreateIndex(
                name: "IX_SaleTransactions_OrderId",
                table: "SaleTransactions",
                column: "OrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SaleTransactions_PriceAgreedByAccountId",
                table: "SaleTransactions",
                column: "PriceAgreedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SaleTransactions_WeighingId",
                table: "SaleTransactions",
                column: "WeighingId");

            migrationBuilder.CreateIndex(
                name: "IX_SeasonalWorkers_CitizenId",
                table: "SeasonalWorkers",
                column: "CitizenId",
                unique: true,
                filter: "[CitizenId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SeasonalWorkers_EmployeeId",
                table: "SeasonalWorkers",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_SeasonalWorkers_FullName_PhoneNumber",
                table: "SeasonalWorkers",
                columns: new[] { "FullName", "PhoneNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_SeasonalWorkers_StatusId",
                table: "SeasonalWorkers",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "IX_SeedingEvents_CycleId",
                table: "SeedingEvents",
                column: "CycleId");

            migrationBuilder.CreateIndex(
                name: "IX_SeedingEvents_RecordedByAccountId",
                table: "SeedingEvents",
                column: "RecordedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SeedingEvents_SeedLotId",
                table: "SeedingEvents",
                column: "SeedLotId");

            migrationBuilder.CreateIndex(
                name: "IX_SeedReceiptLots_InternalCode",
                table: "SeedReceiptLots",
                column: "InternalCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SeedReceiptLots_SeedTypeId",
                table: "SeedReceiptLots",
                column: "SeedTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_SeedReceiptLots_SupplierId",
                table: "SeedReceiptLots",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_SeedSuppliers_StatusId",
                table: "SeedSuppliers",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "IX_Statuses_Group_Code",
                table: "Statuses",
                columns: new[] { "Group", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StatusTransitions_FromStatusId_ToStatusId",
                table: "StatusTransitions",
                columns: new[] { "FromStatusId", "ToStatusId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StatusTransitions_ToStatusId",
                table: "StatusTransitions",
                column: "ToStatusId");

            migrationBuilder.CreateIndex(
                name: "IX_TraceabilityLots_DisposedByAccountId",
                table: "TraceabilityLots",
                column: "DisposedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_TraceabilityLots_InitialOrderId",
                table: "TraceabilityLots",
                column: "InitialOrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TraceabilityLots_LotCode",
                table: "TraceabilityLots",
                column: "LotCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TraceabilityLots_QrCode",
                table: "TraceabilityLots",
                column: "QrCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TraceCorrections_ApprovedByAccountId",
                table: "TraceCorrections",
                column: "ApprovedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_TraceCorrections_CorrectedByAccountId",
                table: "TraceCorrections",
                column: "CorrectedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_TraceCorrections_LotId",
                table: "TraceCorrections",
                column: "LotId");

            migrationBuilder.CreateIndex(
                name: "IX_TracePublicationVersions_LotId_VersionNumber",
                table: "TracePublicationVersions",
                columns: new[] { "LotId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TracePublicationVersions_PublishedByAccountId",
                table: "TracePublicationVersions",
                column: "PublishedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_TracePublicationVersions_ReviewedByAccountId",
                table: "TracePublicationVersions",
                column: "ReviewedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_TracePublicationVersions_StatusId",
                table: "TracePublicationVersions",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "IX_TracePublicationVersions_WithdrawnByAccountId",
                table: "TracePublicationVersions",
                column: "WithdrawnByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_VerificationCodes_AccountId",
                table: "VerificationCodes",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_VerificationCodes_Target_Purpose_CreatedAt",
                table: "VerificationCodes",
                columns: new[] { "Target", "Purpose", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WaterMeasurements_ConfirmedByAccountId",
                table: "WaterMeasurements",
                column: "ConfirmedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_WaterMeasurements_CorrectedFromMeasurementId",
                table: "WaterMeasurements",
                column: "CorrectedFromMeasurementId");

            migrationBuilder.CreateIndex(
                name: "IX_WaterMeasurements_MeasuredByAccountId",
                table: "WaterMeasurements",
                column: "MeasuredByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_WaterMeasurements_StatusId",
                table: "WaterMeasurements",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "IX_WaterMeasurements_ZoneId_MeasuredAt",
                table: "WaterMeasurements",
                columns: new[] { "ZoneId", "MeasuredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkAssignments_CancelledByAccountId",
                table: "WorkAssignments",
                column: "CancelledByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkAssignments_ContractorId",
                table: "WorkAssignments",
                column: "ContractorId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkAssignments_CycleId",
                table: "WorkAssignments",
                column: "CycleId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkAssignments_EmployeeId",
                table: "WorkAssignments",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkAssignments_RaftId",
                table: "WorkAssignments",
                column: "RaftId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkAssignments_WorkerId",
                table: "WorkAssignments",
                column: "WorkerId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkAssignments_ZoneId_WorkDate",
                table: "WorkAssignments",
                columns: new[] { "ZoneId", "WorkDate" });

            migrationBuilder.CreateIndex(
                name: "IX_ZoneComplianceDocuments_ExpiryDate",
                table: "ZoneComplianceDocuments",
                column: "ExpiryDate");

            migrationBuilder.CreateIndex(
                name: "IX_ZoneComplianceDocuments_ReplacesDocumentId",
                table: "ZoneComplianceDocuments",
                column: "ReplacesDocumentId");

            migrationBuilder.AddForeignKey(
                name: "FK_CustomerCredits_SaleAdjustments_SourceAdjustmentId",
                table: "CustomerCredits",
                column: "SourceAdjustmentId",
                principalTable: "SaleAdjustments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CustomerCredits_SaleTransactions_SourceSaleId",
                table: "CustomerCredits",
                column: "SourceSaleId",
                principalTable: "SaleTransactions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_HarvestEvents_SalesOrders_OrderId",
                table: "HarvestEvents",
                column: "OrderId",
                principalTable: "SalesOrders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_LaneSegments_SalesOrders_OrderId",
                table: "LaneSegments",
                column: "OrderId",
                principalTable: "SalesOrders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_LaneSegments_TraceabilityLots_LotId",
                table: "LaneSegments",
                column: "LotId",
                principalTable: "TraceabilityLots",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_MessageDeliveryAttempts_SaleAdjustments_AdjustmentId",
                table: "MessageDeliveryAttempts",
                column: "AdjustmentId",
                principalTable: "SaleAdjustments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_MessageDeliveryAttempts_SaleTransactions_SaleId",
                table: "MessageDeliveryAttempts",
                column: "SaleId",
                principalTable: "SaleTransactions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_MessageDeliveryAttempts_SalesOrders_OrderId",
                table: "MessageDeliveryAttempts",
                column: "OrderId",
                principalTable: "SalesOrders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_MessageDeliveryAttempts_TracePublicationVersions_PublicationId",
                table: "MessageDeliveryAttempts",
                column: "PublicationId",
                principalTable: "TracePublicationVersions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PhysicalReturns_SaleTransactions_SaleId",
                table: "PhysicalReturns",
                column: "SaleId",
                principalTable: "SaleTransactions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PhysicalReturns_TraceabilityLots_LotId",
                table: "PhysicalReturns",
                column: "LotId",
                principalTable: "TraceabilityLots",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PortHandovers_PortWeighings_WeighingId",
                table: "PortHandovers",
                column: "WeighingId",
                principalTable: "PortWeighings",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PortHandovers_SalesOrders_OrderId",
                table: "PortHandovers",
                column: "OrderId",
                principalTable: "SalesOrders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PortHandovers_TracePublicationVersions_PublicationIdAtHandover",
                table: "PortHandovers",
                column: "PublicationIdAtHandover",
                principalTable: "TracePublicationVersions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PortWeighings_SalesOrders_OrderId",
                table: "PortWeighings",
                column: "OrderId",
                principalTable: "SalesOrders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_QualityClaims_SaleTransactions_SaleId",
                table: "QualityClaims",
                column: "SaleId",
                principalTable: "SaleTransactions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ReceiptAllocations_SaleTransactions_SaleId",
                table: "ReceiptAllocations",
                column: "SaleId",
                principalTable: "SaleTransactions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SaleAdjustments_SaleTransactions_SaleId",
                table: "SaleAdjustments",
                column: "SaleId",
                principalTable: "SaleTransactions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SalesOrders_TraceabilityLots_SourceLotId",
                table: "SalesOrders",
                column: "SourceLotId",
                principalTable: "TraceabilityLots",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Customers_Accounts_AccountId",
                table: "Customers");

            migrationBuilder.DropForeignKey(
                name: "FK_PhysicalReturns_Accounts_AssessedByAccountId",
                table: "PhysicalReturns");

            migrationBuilder.DropForeignKey(
                name: "FK_SalesOrders_Accounts_RecordedByAccountId",
                table: "SalesOrders");

            migrationBuilder.DropForeignKey(
                name: "FK_SaleTransactions_Accounts_PriceAgreedByAccountId",
                table: "SaleTransactions");

            migrationBuilder.DropForeignKey(
                name: "FK_TraceabilityLots_Accounts_DisposedByAccountId",
                table: "TraceabilityLots");

            migrationBuilder.DropForeignKey(
                name: "FK_SalesOrders_FarmingZones_ZoneId",
                table: "SalesOrders");

            migrationBuilder.DropForeignKey(
                name: "FK_Cooperatives_Statuses_StatusId",
                table: "Cooperatives");

            migrationBuilder.DropForeignKey(
                name: "FK_Customers_Statuses_StatusId",
                table: "Customers");

            migrationBuilder.DropForeignKey(
                name: "FK_PhysicalReturns_Statuses_StatusId",
                table: "PhysicalReturns");

            migrationBuilder.DropForeignKey(
                name: "FK_Products_Statuses_StatusId",
                table: "Products");

            migrationBuilder.DropForeignKey(
                name: "FK_SalesOrders_Statuses_StatusId",
                table: "SalesOrders");

            migrationBuilder.DropForeignKey(
                name: "FK_SalesOrders_Customers_CustomerId",
                table: "SalesOrders");

            migrationBuilder.DropForeignKey(
                name: "FK_SaleTransactions_Customers_CustomerId",
                table: "SaleTransactions");

            migrationBuilder.DropForeignKey(
                name: "FK_PhysicalReturns_SaleTransactions_SaleId",
                table: "PhysicalReturns");

            migrationBuilder.DropForeignKey(
                name: "FK_TraceabilityLots_SalesOrders_InitialOrderId",
                table: "TraceabilityLots");

            migrationBuilder.DropTable(
                name: "AccessGrants");

            migrationBuilder.DropTable(
                name: "AuditEvents");

            migrationBuilder.DropTable(
                name: "CustomerRefunds");

            migrationBuilder.DropTable(
                name: "DocumentZoneScopes");

            migrationBuilder.DropTable(
                name: "LaborPayments");

            migrationBuilder.DropTable(
                name: "LaneSegments");

            migrationBuilder.DropTable(
                name: "LoginSessions");

            migrationBuilder.DropTable(
                name: "MessageDeliveryAttempts");

            migrationBuilder.DropTable(
                name: "Notifications");

            migrationBuilder.DropTable(
                name: "PayLines");

            migrationBuilder.DropTable(
                name: "PortHandovers");

            migrationBuilder.DropTable(
                name: "RaftLayoutVersions");

            migrationBuilder.DropTable(
                name: "RaftMaterialLines");

            migrationBuilder.DropTable(
                name: "ReceiptAllocations");

            migrationBuilder.DropTable(
                name: "RolePermissions");

            migrationBuilder.DropTable(
                name: "StatusTransitions");

            migrationBuilder.DropTable(
                name: "TraceCorrections");

            migrationBuilder.DropTable(
                name: "VerificationCodes");

            migrationBuilder.DropTable(
                name: "WaterMeasurements");

            migrationBuilder.DropTable(
                name: "CustomerCredits");

            migrationBuilder.DropTable(
                name: "ZoneComplianceDocuments");

            migrationBuilder.DropTable(
                name: "CultivationLogEntries");

            migrationBuilder.DropTable(
                name: "HarvestEvents");

            migrationBuilder.DropTable(
                name: "SeedingEvents");

            migrationBuilder.DropTable(
                name: "AttendanceRecords");

            migrationBuilder.DropTable(
                name: "LaborSettlements");

            migrationBuilder.DropTable(
                name: "PayRuleVersions");

            migrationBuilder.DropTable(
                name: "WorkAssignments");

            migrationBuilder.DropTable(
                name: "TracePublicationVersions");

            migrationBuilder.DropTable(
                name: "RaftMaterialRecords");

            migrationBuilder.DropTable(
                name: "Roles");

            migrationBuilder.DropTable(
                name: "CustomerReceipts");

            migrationBuilder.DropTable(
                name: "SaleAdjustments");

            migrationBuilder.DropTable(
                name: "Lanes");

            migrationBuilder.DropTable(
                name: "SeedReceiptLots");

            migrationBuilder.DropTable(
                name: "Contractors");

            migrationBuilder.DropTable(
                name: "CultivationCycles");

            migrationBuilder.DropTable(
                name: "SeasonalWorkers");

            migrationBuilder.DropTable(
                name: "QualityClaims");

            migrationBuilder.DropTable(
                name: "SeedSuppliers");

            migrationBuilder.DropTable(
                name: "OysterSeedTypes");

            migrationBuilder.DropTable(
                name: "Rafts");

            migrationBuilder.DropTable(
                name: "Employees");

            migrationBuilder.DropTable(
                name: "Accounts");

            migrationBuilder.DropTable(
                name: "FarmingZones");

            migrationBuilder.DropTable(
                name: "Statuses");

            migrationBuilder.DropTable(
                name: "Customers");

            migrationBuilder.DropTable(
                name: "Cooperatives");

            migrationBuilder.DropTable(
                name: "SaleTransactions");

            migrationBuilder.DropTable(
                name: "PortWeighings");

            migrationBuilder.DropTable(
                name: "SalesOrders");

            migrationBuilder.DropTable(
                name: "PhysicalReturns");

            migrationBuilder.DropTable(
                name: "Products");

            migrationBuilder.DropTable(
                name: "TraceabilityLots");
        }
    }
}
