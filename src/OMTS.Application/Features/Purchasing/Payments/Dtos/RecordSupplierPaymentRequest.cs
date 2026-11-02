using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Common;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Purchasing.Receipts;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Payments.Dtos;

public sealed record RecordSupplierPaymentRequest(
    Guid? SupplierId, Guid? ReceiptId, decimal? Amount, DateOnly? PaymentDate, PaymentMethod? Method, string? ReferenceCode, string? DeduplicationKey);
