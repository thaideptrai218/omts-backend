using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Application.Features.Sales.Requests;
using OMTS.Application.Features.Sales.Requests.Dtos;

namespace OMTS.Api.Controllers.Sales;

/// <summary>Nhân viên phản hồi bằng danh tính của phiên; không nhận employeeId do client chọn.</summary>
[ApiController]
[Authorize]
[Route("api/me/purchase-shifts")]
public sealed class MyPurchaseShiftsController(PurchaseStaffingService staffing) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Mine(DateOnly from, DateOnly to, CancellationToken ct) =>
        (await staffing.MineAsync(from, to, ct)).ToActionResult(Ok);

    [HttpPost("{id:guid}/response")]
    public async Task<IActionResult> Respond(Guid id, RespondPurchaseShift input, CancellationToken ct) =>
        (await staffing.RespondAsync(id, input, ct)).ToActionResult(Ok);
}
