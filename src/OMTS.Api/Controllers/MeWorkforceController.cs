using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Labor.Work;

namespace OMTS.Api.Controllers;

/// <summary>
/// LABOR-07, LABOR-08, LABOR-10 – the own work of the employee whose profile is linked to the caller's account: attendance QR code, own
/// assignments, attendance and requests, and sending or cancelling a request. Only the caller's own records are ever read or changed
/// (BR-LABOR-09). The employee app login with a login code (LABOR-03, LABOR-06) is not here yet.
/// </summary>
[ApiController]
[Route("api/me")]
[Authorize]
public sealed class MeWorkforceController(WorkQueryService queries, EmployeeRequestService requests) : ControllerBase
{
    /// <summary>LABOR-08: own assignments, attendance days with the hours of each job type, and requests from <c>from</c> to <c>to</c> (at most 62 days).</summary>
    [HttpGet("workforce")]
    [ProducesResponseType<MyWork>(StatusCodes.Status200OK)]
    public async Task<IActionResult> MyWork(DateOnly? from, DateOnly? to, CancellationToken cancellationToken) =>
        from is { } start && to is { } end
            ? (await queries.MyWorkAsync(start, end, cancellationToken)).ToActionResult(Ok)
            : AppError.Invalid("Period").ToProblem();

    /// <summary>LABOR-07: the attendance QR code, valid for 60 seconds; the app asks again for the next one.</summary>
    [HttpGet("attendance-qr")]
    [ProducesResponseType<MyAttendanceQr>(StatusCodes.Status200OK)]
    public async Task<IActionResult> QrCode(CancellationToken cancellationToken) =>
        (await queries.MyQrCodeAsync(cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// LABOR-10.0: a request for leave (<c>from</c>, <c>to</c>) or for a missed check-in or check-out (<c>type</c>, <c>day</c>,
    /// <c>requestedTime</c>), with a reason. 409 MSG-GEN-02 names a request already awaiting review for the same days; 409 MSG-LABOR-25 a day
    /// of a finalized payroll. A retry with the same <c>clientOperationId</c> returns the request saved.
    /// </summary>
    [HttpPost("leave-requests")]
    [ProducesResponseType<EmployeeRequestView>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Submit(SubmitEmployeeRequest request, CancellationToken cancellationToken) =>
        (await requests.SubmitAsync(request, cancellationToken)).ToActionResult(r => StatusCode(StatusCodes.Status201Created, r));

    /// <summary>LABOR-10.2: cancels the own request while it is Awaiting review; 409 MSG-LABOR-26 once reviewed. Ask MSG-LABOR-24 first.</summary>
    [HttpDelete("leave-requests/{id:guid}")]
    [ProducesResponseType<EmployeeRequestView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken cancellationToken) =>
        (await requests.CancelAsync(id, cancellationToken)).ToActionResult(Ok);
}
