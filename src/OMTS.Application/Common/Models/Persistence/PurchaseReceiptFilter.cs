using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.Common.Models.Persistence;

/// <summary>INP-07 filters; null means "any". Zones are limited to <see cref="Zones"/> as well.</summary>
public sealed record PurchaseReceiptFilter(
    PermittedZones Zones,
    InputItemGroup? ItemGroup = null,
    Guid? ItemId = null,
    Guid? ZoneId = null,
    Guid? RaftId = null,
    Guid? SupplierId = null,
    int? StatusId = null,
    DateOnly? From = null,
    DateOnly? To = null,
    string? Code = null);
