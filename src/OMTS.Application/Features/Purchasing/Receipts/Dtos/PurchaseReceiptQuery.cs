using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Reports;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Common;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Employees;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Receipts.Dtos;

public sealed record PurchaseReceiptQuery(
    InputItemGroup? ItemGroup = null, Guid? ItemId = null, Guid? ZoneId = null, Guid? RaftId = null, Guid? SupplierId = null,
    int? StatusId = null, DateOnly? From = null, DateOnly? To = null, string? Code = null, int Page = 1, int PageSize = 20);
