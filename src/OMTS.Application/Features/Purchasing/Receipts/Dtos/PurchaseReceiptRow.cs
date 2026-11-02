using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Reports;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Common;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Employees.Dtos;
using OMTS.Application.Features.Employees;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Receipts.Dtos;

public sealed record PurchaseReceiptRow(
    Guid Id, string Code, DateOnly? ReceiptDate, SupplierRef Supplier, ZoneRef Zone, RaftRef? Raft, int StatusId, string Status,
    decimal Subtotal, decimal Discount, decimal Payable, decimal Returned, decimal Paid, decimal Outstanding);
