using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Features.AccessManagement;
using OMTS.Application.Features.Accounts;
using OMTS.Application.Features.Accounts.Dtos;
using OMTS.Application.Features.AdministrativeUnits;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.CashBook;
using OMTS.Application.Features.Cooperatives;
using OMTS.Application.Features.EmployeeApp;
using OMTS.Application.Features.Employees;
using OMTS.Application.Features.Cultivation.Closing;
using OMTS.Application.Features.Cultivation.Cycles;
using OMTS.Application.Features.Cultivation.CultivationLogs;
using OMTS.Application.Features.Cultivation.MaterialUsages;
using OMTS.Application.Features.Cultivation.Nursery;
using OMTS.Application.Features.Cultivation.Seeding;
using OMTS.Application.Features.Cultivation.Progress;
using OMTS.Application.Features.Dashboard;
using OMTS.Application.Features.Farming;
using OMTS.Application.Features.Farming.Licenses;
using OMTS.Application.Features.Farming.Rafts;
using OMTS.Application.Features.Farming.Zones;
using OMTS.Application.Features.Files;
using OMTS.Application.Features.Harvest;
using OMTS.Application.Features.Harvest.Plans;
using OMTS.Application.Features.Traceability.Corrections;
using OMTS.Application.Features.Traceability.Origin;
using OMTS.Application.Features.Traceability.Publishing;
using OMTS.Application.Features.Traceability.Tracing;
using OMTS.Application.Features.Traceability.Withdrawal;
using OMTS.Application.Features.Labor;
using OMTS.Application.Features.Labor.Costs;
using OMTS.Application.Features.Labor.Payrolls;
using OMTS.Application.Features.Labor.Work;
using OMTS.Application.Features.Purchasing;
using OMTS.Application.Features.Purchasing.Issues;
using OMTS.Application.Features.Purchasing.Items;
using OMTS.Application.Features.Purchasing.SeedLots;
using OMTS.Application.Features.Purchasing.Payments;
using OMTS.Application.Features.Purchasing.Receipts;
using OMTS.Application.Features.Purchasing.Suppliers;
using OMTS.Application.Features.ReceivingBankAccounts;
using OMTS.Application.Features.Reports;
using OMTS.Application.Features.Reports.PostSaleIssues;
using OMTS.Application.Features.Reports.PriceTrends;
using OMTS.Application.Features.Reports.EndOfDay;
using OMTS.Application.Features.Reports.Financial;
using OMTS.Application.Features.Reports.ProductStock;
using OMTS.Application.Features.Reports.Purchases;
using OMTS.Application.Features.Reports.SalesPerformance;
using OMTS.Application.Features.Sales.Checkout;
using OMTS.Application.Features.Sales.Customers;
using OMTS.Application.Features.Sales.Debts;
using OMTS.Application.Features.Sales.Disposals;
using OMTS.Application.Features.Sales.Payments;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Application.Features.Sales.Orders;
using OMTS.Application.Features.Sales.Products;
using OMTS.Application.Features.Sales.PurchaseRequests;
using OMTS.Application.Features.Sales.QualityClaims;
using OMTS.Application.Features.Sales.Returns;
using OMTS.Application.Features.Verification;
using OMTS.Application.Features.Auth;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.Parties;
using OMTS.Application.Features.TaxLookup;
using OMTS.Application.Features.WaterQuality;
using OMTS.Application.Features.WaterQuality.Dtos;

namespace OMTS.Application;

public static class DependencyInjection
{
    /// <summary>Registers application-layer services (use cases, validators).</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddMemoryCache();

        services.AddSingleton(provider => new UnknownLoginCounter(timeProvider: provider.GetRequiredService<TimeProvider>()));
        services.AddScoped<AuthService>();
        services.AddScoped<SessionManagementService>();
        services.AddScoped<ZaloLinkService>();
        services.AddScoped<ZaloLocationService>();
        services.AddScoped<AdministrativeUnitService>();
        services.AddScoped<TaxLookupService>();
        services.AddScoped<PartyAddressVerifier>();
        services.AddScoped<OMTS.Application.Features.Sales.Requests.PurchaseStaffingService>();
        services.AddScoped<OMTS.Application.Features.Sales.Products.SalesProductService>();
        services.AddOptions<OMTS.Application.Features.Testing.Options.TestRoleOptions>();
        services.AddScoped<OMTS.Application.Features.Testing.TestRoleService>();
        services.AddScoped<OMTS.Application.Features.Workforce.WorkPlanAccessService>();
        services.AddScoped<OMTS.Application.Features.Workforce.WorkDayPlanService>();

        services.AddSingleton<PermissionCache>();
        services.AddSingleton<IPermissionCacheInvalidator>(provider => provider.GetRequiredService<PermissionCache>());
        services.AddScoped<PermissionService>();

        services.AddScoped<AmbientActor>();
        services.AddScoped<AuditLogService>();
        services.AddScoped<AuditEventSearchService>();
        services.AddScoped<StatusChangeService>();
        services.AddScoped<INotificationDispatcher, NotificationDispatcher>();
        services.AddScoped<MandatorySmsQueue>();
        services.AddScoped<OwnerActorRecipientFilter>();
        services.AddScoped<NotificationInboxService>();
        services.AddScoped<RowVersionGuard>();

        // ACS – personal accounts
        services.AddScoped<VerificationThrottle>();
        services.AddSingleton<VerificationStartGate>();
        services.AddScoped<VerificationService>();
        services.AddScoped<TraderCustomerLinker>();
        services.AddScoped<UsernameGenerator>();
        services.AddScoped<PasswordRecoveryService>();
        services.AddScoped<CurrentPasswordProof>();
        services.AddScoped<ContactChangeNotifier>();
        services.AddScoped<PasswordChangeService>();
        services.AddScoped<PhoneVerificationService>();
        services.AddScoped<ProfileService>();
        services.AddScoped<NotificationPreferenceService>();
        services.AddScoped<PushDeviceService>();

        // ACS – Owner's management of staff accounts, zone access and roles
        services.AddScoped<AccessGrantService>();
        services.AddScoped<StaffAccountService>();
        services.AddScoped<RoleService>();
        services.AddScoped<TraderRequestService>();

        // FARM-01 cooperative profile, LABOR-01 employees
        services.AddScoped<CooperativeProfileViews>();
        services.AddScoped<CooperativeProfileService>();
        services.AddScoped<CooperativeCoverPhotoService>();
        services.AddScoped<CooperativeCertificationService>();
        services.AddScoped<EmployeeCallerAccess>();
        services.AddScoped<EmployeeViewBuilder>();
        services.AddScoped<EmployeeProfileValidator>();
        services.AddScoped<EmployeeCodeAllocator>();
        services.AddScoped<EmployeeQueryService>();
        services.AddScoped<EmployeeFileService>();
        services.AddScoped<EmployeeImportService>();
        services.AddScoped<EmployeeService>();
        services.AddScoped<EmployeeWorkingZoneService>();
        services.AddScoped<EmploymentService>();
        services.AddScoped<EmploymentConsequencesService>();
        services.AddScoped<EmploymentEndService>();

        // LABOR-03 login codes, LABOR-06 log in to the employee app (a second kind of caller, apart from the staff accounts), employee push
        services.AddScoped<EmployeeAppAccess>();
        services.AddScoped<EmployeeAppTokenIssuer>();
        services.AddScoped<EmployeeAppLoginService>();
        services.AddScoped<EmployeeAppSessionService>();
        services.AddScoped<EmployeeLoginCodeService>();
        services.AddScoped<EmployeeDeviceService>();
        services.AddScoped<IEmployeeNotifier, EmployeeNotifier>();

        // DEBT-01 receiving bank account
        services.AddScoped<ReceivingAccountAcceptance>();
        services.AddScoped<ReceivingBankAccountService>();

        // LABOR-11 job types and rates, LABOR-15 attendance settings, the pay period lock (BR-LABOR-13)
        services.AddScoped<PayPeriodLock>();

        // LABOR-02 work assignment, LABOR-04 attendance, LABOR-05 and LABOR-10 employee requests, LABOR-08 my work
        services.AddScoped<WorkAssignmentService>();
        services.AddScoped<AttendanceRecordingService>();
        services.AddScoped<AttendanceCorrectionService>();
        services.AddScoped<EmployeeRequestService>();
        services.AddScoped<WorkQueryService>();
        services.AddScoped<PayrollLineSync>();
        services.AddScoped<PayrollQueryService>();
        services.AddScoped<PayrollService>();
        services.AddScoped<PayrollPaymentService>();
        services.AddScoped<PayrollExportService>();
        services.AddScoped<LaborCostService>();
        services.AddScoped<LaborCostExportService>();
        services.AddScoped<JobTypeQueryService>();
        services.AddScoped<JobTypeService>();
        services.AddScoped<PayRateService>();
        services.AddScoped<EmployeeSalaryService>();
        services.AddScoped<AttendanceSettingsService>();

        // FARM-02 zones, FARM-03 zone licenses, FARM-04 rafts
        services.AddScoped<FarmingLookupAccess>();
        services.AddScoped<FarmingZoneService>();
        services.AddScoped<ZoneLicenseService>();
        // GFR-03 expiry reminders of zone licenses and cooperative certifications
        services.AddScoped<ZoneLicenseReminderService>();
        services.AddScoped<CertificationReminderService>();
        services.AddScoped<RaftService>();
        // Asynchronous photo scans (licence map, raft form): the shared start/poll gateway and one service per scan.
        services.AddScoped<OMTS.Application.Features.Scans.PhotoScanGateway>();
        services.AddScoped<OMTS.Application.Features.Farming.Zones.BoundaryScan.ZoneBoundaryScanService>();
        services.AddScoped<OMTS.Application.Features.Farming.Rafts.FormScan.RaftFormScanService>();
        services.AddScoped<RaftReadService>();
        services.AddScoped<RaftViews>();
        services.AddScoped<RaftLiveReader>();
        services.AddScoped<RaftPhotoService>();
        services.AddScoped<LaneIncidentViews>();
        services.AddScoped<LaneIncidentNotifier>();
        services.AddScoped<LaneIncidentService>();
        services.AddScoped<LaneIncidentReadService>();
        services.AddScoped<LaneIncidentPhotoService>();
        services.AddScoped<RaftStatusService>();

        // CYCLE – cultivation cycles and the lane states derived from their records
        services.AddScoped<LaneStateReader>();
        services.AddScoped<LaneProgressViews>();
        services.AddScoped<LanePercentGuard>();
        services.AddScoped<LaneProgressService>();
        services.AddScoped<CultivationCycleViews>();
        services.AddScoped<CycleSeedLabels>();
        services.AddScoped<CycleWriteGuard>();
        services.AddScoped<CultivationLogViews>();
        services.AddScoped<RecordPhotoStore>();
        services.AddScoped<StepPhotoService>();
        services.AddScoped<CultivationLogService>();
        services.AddScoped<CultivationLogCorrectionService>();
        services.AddScoped<CultivationLogReadService>();
        services.AddScoped<SeedingSessionReader>();
        services.AddScoped<SeedingViews>();
        services.AddScoped<SeedingReadService>();
        services.AddScoped<SeedingRecordService>();
        services.AddScoped<SeedingStockSync>();
        services.AddScoped<SeedingRecordCorrectionService>();
        services.AddScoped<SeedingCompletionService>();

        // NURSERY – ươm giống: batches of one seed lot per zone, completed into a pool the cycles seed from (v2)
        services.AddScoped<Features.Cultivation.ResponsiblePersonService>();
        services.AddScoped<NurseryBatchViews>();
        services.AddScoped<NurseryNotifier>();
        services.AddScoped<NurseryStockSync>();
        services.AddScoped<NurseryBatchService>();
        services.AddScoped<NurseryBatchEditService>();
        services.AddScoped<NurseryCompletionService>();
        services.AddScoped<NurseryPoolAutoClose>();
        services.AddScoped<NurseryReadService>();
        services.AddScoped<MaterialUsageCycleGuard>();
        services.AddScoped<MaterialUsageViews>();
        services.AddScoped<MaterialUsageService>();
        services.AddScoped<MaterialUsageStockSync>();
        services.AddScoped<MaterialUsageEditService>();
        services.AddScoped<MaterialUsageRemoveService>();
        services.AddScoped<RecordHistoryService>();
        services.AddScoped<MaterialUsageReadService>();
        services.AddScoped<CultivationCycleService>();
        services.AddScoped<CultivationCycleReadService>();
        services.AddScoped<CultivationProgressService>();
        services.AddScoped<CycleSummaryReader>();
        services.AddScoped<CycleCloseService>();
        // HARVEST-01, HARVEST-03 and what SALE and CLAIM call on a lot
        services.AddScoped<HarvestLotReferenceReader>();
        services.AddScoped<HarvestLotViews>();
        services.AddScoped<HarvestLotNotifier>();
        services.AddScoped<HarvestCrewService>();
        services.AddScoped<OMTS.Application.Features.Workforce.RecordCrewService>();
        services.AddScoped<OMTS.Application.Features.Workforce.RecordCrewAttendance>();
        services.AddScoped<OMTS.Application.Features.Workforce.RecordCrewAssignmentService>();
        services.AddScoped<HarvestLotRecordingService>();
        services.AddScoped<HarvestLaneFinishService>();
        services.AddScoped<HarvestLanesFinishService>();
        services.AddScoped<HarvestLotEditReader>();
        services.AddScoped<HarvestLotCorrectionService>();
        services.AddScoped<HarvestLotRemovalService>();
        services.AddScoped<HarvestLotReadService>();
        services.AddScoped<HarvestLotForSaleService>();
        services.AddScoped<HarvestLotStatusService>();
        services.AddScoped<HarvestLotWeighingService>();
        services.AddScoped<HarvestLotCageService>();
        services.AddScoped<LotWeighingSheetBuilder>();
        services.AddScoped<TraderWeighingService>();
        services.AddScoped<HarvestLotAgreementGuard>();
        services.AddScoped<HarvestLotReopeningService>();
        services.AddScoped<HarvestLotRegisterService>();
        services.AddScoped<HarvestLotRegisterFileService>();
        // HARVEST-02 close by hand (also the CLAIM-03 extension point) and GFR-05 the daily close check
        services.AddScoped<HarvestLotClosingService>();
        services.AddScoped<HarvestLotAutoCloseService>();
        services.AddScoped<HarvestSettingsService>();
        // HARVEST-04 the yield report and its export
        services.AddScoped<HarvestYieldService>();
        // TRACE-03 – correct traceability data: its own guard and services, as the cultivation screens stay locked after the close
        services.AddScoped<TraceCorrectionGuard>();
        services.AddScoped<TraceSeedingReader>();
        services.AddScoped<TraceSeedingCorrectionService>();
        services.AddScoped<TraceCultivationLogCorrectionService>();
        services.AddScoped<TraceHarvestLotCorrectionService>();
        services.AddScoped<TraceCorrectionReadService>();
        // TRACE-01 – publish the origin information of a lot: at the end of a sale (SALE-05.9) and from the Trace lot screen
        services.AddScoped<ProvenanceReadiness>();
        services.AddScoped<ProvenancePublisher>();
        services.AddScoped<IProvenancePublisher>(provider => provider.GetRequiredService<ProvenancePublisher>());
        services.AddScoped<LotPublishService>();
        // TRACE-02 – trace a lot backward and forward, and export the trace
        services.AddScoped<LotTraceService>();
        // ORIGIN-01 – the public provenance page of an invoice QR code (anonymous)
        services.AddScoped<ProvenancePageService>();
        // TRACE-04 – withdraw the origin information of a lot and tell its traders
        services.AddScoped<WithdrawalNotices>();
        services.AddScoped<LotWithdrawalService>();

        services.AddScoped<UploadedFileSaver>();
        services.AddScoped<OrphanFileCleanupService>();

        // INP – suppliers, farming inputs, purchase receipts, returns and supplier payments
        services.AddScoped<ZoneAccessGuard>();
        services.AddScoped<SupplierService>();
        services.AddScoped<SupplierFileService>();
        services.AddScoped<FarmingInputItemService>();
        services.AddScoped<FarmingInputItemCodeService>();
        services.AddScoped<FarmingInputItemFileService>();
        services.AddScoped<InputItemAccess>();
        services.AddScoped<InputStockLedger>();
        services.AddScoped<InputUnitService>();
        services.AddScoped<InputItemGroupService>();
        services.AddScoped<InputInventorySettingsService>();
        services.AddScoped<ReceiptPriceSetupService>();
        services.AddScoped<InputIssueAccess>();
        services.AddScoped<InputIssueCodeService>();
        services.AddScoped<InputIssueService>();
        services.AddScoped<FarmingStockIssuer>();
        services.AddScoped<InputIssueTypeService>();
        services.AddScoped<InputMovementService>();
        services.AddScoped<FarmingInputItemDeletionService>();
        services.AddScoped<ProductService>();
        services.AddScoped<ProductReadService>();
        services.AddScoped<ProductAccess>();
        services.AddScoped<ProductCodeService>();
        services.AddScoped<ProductViews>();
        services.AddScoped<ProductStockLedger>();
        services.AddScoped<ProductMovementService>();
        services.AddScoped<ProductCategoryService>();
        services.AddScoped<ProductFileService>();
        services.AddScoped<ProductDisposalAccess>();
        services.AddScoped<ProductDisposalCodeService>();
        services.AddScoped<ProductDisposalService>();
        services.AddScoped<SalesOrderAccess>();
        services.AddScoped<SalesOrderCodeService>();
        services.AddScoped<SalesOrderProgress>();
        services.AddScoped<SalesOrderViews>();
        services.AddScoped<SalesOrderService>();
        services.AddScoped<SalesOrderFromRequestService>();
        services.AddScoped<SalesOrderReadService>();
        services.AddScoped<HarvestLotProductLineService>();
        services.AddScoped<HarvestPlanAccess>();
        services.AddScoped<HarvestPlanCodeService>();
        services.AddScoped<HarvestPlanViews>();
        services.AddScoped<HarvestPlanProgress>();
        services.AddScoped<HarvestPlanHandoverService>();
        services.AddScoped<HarvestPlanNotifier>();
        services.AddScoped<HarvestPlanStaffingService>();
        services.AddScoped<HarvestPlanRosterService>();
        services.AddScoped<HarvestPlanMyShiftService>();
        services.AddScoped<HarvestPlanRosterViews>();
        services.AddScoped<HarvestPlanService>();
        services.AddScoped<HarvestPlanLaneSkipService>();
        services.AddScoped<HarvestPlanReadService>();
        services.AddScoped<CustomerService>();
        services.AddScoped<CustomerSettingsService>();
        services.AddScoped<CustomerDebtStandings>();
        services.AddScoped<CustomerCodeService>();
        services.AddScoped<CustomerFileService>();
        services.AddScoped<CustomerMergeService>();
        services.AddScoped<PurchaseRequestViews>();
        services.AddScoped<PurchaseRequestNotifications>();
        services.AddScoped<PurchaseRequestReadService>();
        services.AddScoped<TraderPurchaseRequestService>();
        services.AddScoped<PurchaseRequestResponseService>();
        services.AddScoped<TraderMessageService>();
        // CLAIM-01 and CLAIM-02
        services.AddScoped<QualityClaimViews>();
        services.AddScoped<QualityClaimNotifications>();
        services.AddScoped<QualityClaimReadService>();
        services.AddScoped<QualityClaimSubmissionService>();
        services.AddScoped<QualityClaimResponseService>();
        services.AddScoped<QualityClaimAnswerService>();
        // CLAIM-03 sales returns
        services.AddScoped<SalesReturnGuard>();
        services.AddScoped<SalesReturnLots>();
        services.AddScoped<SalesReturnNotifications>();
        services.AddScoped<SalesReturnViews>();
        services.AddScoped<SalesReturnListService>();
        services.AddScoped<SalesReturnListFileService>();
        services.AddScoped<SalesReturnService>();
        services.AddScoped<SalesReturnCancellationService>();
        // SALE-05 sales invoices and the customer debt (BR-DEBT-02)
        services.AddScoped<CustomerDebtReader>();
        // DEBT-03 customer debts, statement and export (read only)
        services.AddScoped<CustomerDebtService>();
        services.AddScoped<CustomerDebtExportService>();
        // REPORT-01 sales performance (read only); the filter resolver is shared with REPORT-02 and REPORT-03
        services.AddScoped<ReportFilterResolver>();
        services.AddScoped<SalesPerformanceService>();
        services.AddScoped<SalesPerformanceExportService>();
        // REPORT-02 price trends and REPORT-03 post-sale issues (read only)
        services.AddScoped<PriceTrendsService>();
        services.AddScoped<PriceTrendsExportService>();
        services.AddScoped<PostSaleIssuesService>();
        services.AddScoped<PostSaleIssuesExportService>();
        // KiotViet-style reports (OMTS-227): product stock, purchases, end of day and the financial statement (read only)
        services.AddScoped<ProductStockService>();
        services.AddScoped<ProductStockExportService>();
        services.AddScoped<PurchaseReportService>();
        services.AddScoped<PurchaseReportExportService>();
        services.AddScoped<EndOfDayService>();
        services.AddScoped<EndOfDayExportService>();
        services.AddScoped<OMTS.Application.Features.Reports.Documents.ReportDocumentExport>();
        services.AddScoped<FinancialReportService>();
        services.AddScoped<FinancialReportExportService>();
        // DEBT-02 customer payments, money paid back, confirmations and cancellations
        services.AddScoped<CustomerPaymentViews>();
        services.AddScoped<CustomerPaymentStatusChanges>();
        services.AddScoped<CustomerPaymentSettlements>();
        services.AddScoped<CustomerPaymentNotifications>();
        services.AddScoped<CustomerPaymentService>();
        services.AddScoped<CustomerPaymentCodeService>();
        services.AddScoped<CustomerDebtAdjustmentService>();
        services.AddScoped<CustomerPaymentStatusService>();
        // Trader payments from the Zalo Mini App: Checkout SDK + payOS (decision of 21/11/2026)
        services.AddScoped<TraderCheckoutReporter>();
        services.AddScoped<TraderCheckoutService>();
        services.AddScoped<PayOsNoticeService>();
        services.AddScoped<SalesInvoiceFactsReader>();
        services.AddScoped<SalesInvoiceWriter>();
        services.AddScoped<SalesInvoiceNotifications>();
        services.AddScoped<SalesInvoiceViews>();
        services.AddScoped<SalesInvoiceService>();
        services.AddScoped<SalesInvoiceReadService>();
        // SALE-05 steps 7-8: the sale awaiting its VietQR transfer, and GFR-08 / API-13 the transfers the bank reports
        services.AddScoped<PendingSaleWriter>();
        services.AddScoped<PendingSaleViews>();
        services.AddScoped<PendingSaleService>();
        services.AddScoped<PendingSaleEndOfDayService>();
        services.AddScoped<PendingSaleCompletion>();
        services.AddScoped<PendingSaleLotQuery>();
        services.AddScoped<TransferPaymentWriter>();
        services.AddScoped<BankTransferNoticeService>();
        services.AddScoped<SalesInvoiceSearchService>();
        services.AddScoped<SalesInvoiceExportService>();
        services.AddScoped<MyInvoiceService>();
        // SALE-06 sending the invoice, and the end of the sale with the QR code
        services.AddScoped<SalesInvoiceMessages>();
        services.AddScoped<SalesInvoiceSendingService>();
        services.AddScoped<SalesInvoiceChangeGuard>();
        services.AddScoped<SalesInvoiceChangeSending>();
        services.AddScoped<SalesInvoiceCodeService>();
        services.AddScoped<SalesInvoiceAdvanceTrimmer>();
        services.AddScoped<SalesInvoiceEditPlanner>();
        services.AddScoped<SalesInvoiceEditWriter>();
        services.AddScoped<SalesInvoiceEditService>();
        services.AddScoped<SalesInvoiceDraftService>();
        services.AddScoped<SalesInvoiceCorrectionPlanner>();
        services.AddScoped<SalesInvoiceCorrectionWriter>();
        services.AddScoped<SalesInvoiceCorrectionService>();
        services.AddScoped<SalesInvoiceCorrectionLog>();
        services.AddScoped<SalesInvoiceOwedBackNotifier>();
        services.AddScoped<SalesInvoiceCancellationService>();
        services.AddScoped<PurchaseReceiptAccess>();
        services.AddScoped<PurchaseReceiptValidator>();
        services.AddScoped<PurchaseReceiptViews>();
        services.AddScoped<SeedLotService>();
        services.AddScoped<SeedLotFileService>();
        services.AddScoped<PurchaseReceiptNotifications>();
        services.AddScoped<PurchaseDocumentCodeService>();
        services.AddScoped<PurchaseReceiptService>();
        services.AddScoped<PurchaseReceiptCancellationService>();
        services.AddScoped<PurchaseReceiptEditService>();
        services.AddScoped<PurchaseReceiptCopyService>();
        services.AddScoped<PurchaseReturnService>();
        services.AddScoped<PurchaseReturnQueryService>();
        services.AddScoped<SupplierPaymentAllocationService>();
        services.AddScoped<PurchaseReceiptQueryService>();
        services.AddScoped<SupplierPaymentService>();
        services.AddScoped<SupplierPaymentNotifications>();
        services.AddScoped<SupplierDebtService>();
        services.AddScoped<SupplierDebtExportService>();
        services.AddScoped<CashAccountService>();
        services.AddScoped<CashVoucherTypeService>();
        services.AddScoped<CashPartyService>();
        services.AddScoped<CashVoucherCodeService>();
        services.AddScoped<CashBookLookupService>();
        services.AddScoped<CashVoucherService>();
        services.AddScoped<CashBookService>();
        services.AddScoped<CashBookExportService>();

        // ENV-01…ENV-04 water quality monitoring and the overdue measurement reminder
        services.AddSingleton<EditingPresenceTracker>();
        services.AddScoped<WaterMeterRecognizer>();
        services.AddScoped<WaterMeterPhotoStore>();
        services.AddScoped<WaterAlertService>();
        services.AddScoped<WaterCorrectionNotifier>();
        services.AddScoped<WaterCorrectionPreconditions>();
        services.AddScoped<WaterMeasurementViewBuilder>();
        services.AddScoped<WaterMeasurementRecordingService>();
        services.AddScoped<WaterMeasurementCorrectionService>();
        services.AddScoped<WaterMeasurementQueryService>();
        services.AddScoped<WaterThresholdService>();
        services.AddScoped<WaterMeasurementOverdueReminder>();
        services.AddScoped<WaterZoneOverviewService>();
        services.AddScoped<OperationsDashboardService>();

        return services;
    }
}


