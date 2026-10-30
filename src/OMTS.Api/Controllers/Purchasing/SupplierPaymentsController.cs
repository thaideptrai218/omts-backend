using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Purchasing.Payments.Dtos;
using OMTS.Application.Features.Purchasing.Payments;
using OMTS.Domain.Enums;

namespace OMTS.Api.Controllers.Purchasing;

/// <summary>INP-06 – supplier payments; cooperative-wide data, so only an All zones SUPPLIER_PAYMENTS grant (or the Owner) is accepted.</summary>
[ApiController]
[Route("api/supplier-payments")]
[Authorize]
public sealed class SupplierPaymentsController(SupplierPaymentService payments) : ControllerBase
{
    [HttpGet]
    [RequirePermission(PermissionFunctions.SupplierPayments, PermissionAction.View, PermissionScope.AllZones)]
    [ProducesResponseType<SupplierPaymentPage>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search(
        Guid? supplierId, Guid? receiptId, Guid? zoneId, PaymentMethod? method, int? statusId, DateOnly? from, DateOnly? to,
        int page = 1, int pageSize = 20, CancellationToken cancellationToken = default) =>
        Ok(await payments.SearchAsync(supplierId, receiptId, zoneId, method, statusId, from, to, page, pageSize, cancellationToken));

    /// <summary>A repeated <c>deduplicationKey</c> returns the first payment and creates nothing.</summary>
    [HttpPost]
    [RequirePermission(PermissionFunctions.SupplierPayments, PermissionAction.Create, PermissionScope.AllZones)]
    [ProducesResponseType<SupplierPaymentView>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Record(RecordSupplierPaymentRequest request, CancellationToken cancellationToken) =>
        (await payments.RecordAsync(request, cancellationToken)).ToActionResult(p => StatusCode(StatusCodes.Status201Created, p));

    [HttpPost("{id:guid}/allocate")]
    [RequirePermission(PermissionFunctions.SupplierPayments, PermissionAction.Create, PermissionScope.AllZones)]
    [ProducesResponseType<SupplierPaymentView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Allocate(Guid id, AllocatePaymentRequest request, CancellationToken cancellationToken) =>
        (await payments.AllocateAsync(id, request, cancellationToken)).ToActionResult(Ok);
}
