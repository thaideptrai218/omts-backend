using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Dtos;

namespace OMTS.Api.Controllers;

/// <summary>
/// ACCOUNT-10 – search the audit log; Cooperative Owner only (BR-ACCOUNT-13), anyone else gets 403 MSG-GEN-08. Read
/// only: an event is never edited or deleted.
/// </summary>
[ApiController]
[Route("api/audit-events")]
[Authorize]
[RequireCooperativeOwner]
public sealed class AuditEventsController(AuditEventSearchService audit) : ControllerBase
{
    /// <summary>
    /// Events newest first. <c>from</c>/<c>to</c> are Vietnam dates, both included: the last 30 days by default, at most
    /// 5 years (MSG-GEN-01 otherwise). One person: <c>actorAccountId</c>, <c>actorEmployeeId</c> or <c>system=true</c>.
    /// <c>action</c> and <c>recordType</c> are exact values (see facets); <c>recordCode</c> matches codes that start with
    /// it. No match: 200 with an empty page and MSG-GEN-10 in <c>message</c>.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<AuditEventSearchResult>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search(
        DateOnly? from,
        DateOnly? to,
        Guid? actorAccountId,
        Guid? actorEmployeeId,
        bool? system,
        string? action,
        string? recordType,
        string? recordCode,
        Guid? zoneId,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        (await audit.SearchAsync(
            new AuditEventQuery(from, to, actorAccountId, actorEmployeeId, system == true, action, recordType, recordCode, zoneId, page, pageSize),
            cancellationToken)).ToActionResult(Ok);

    /// <summary>The values of the filters: actions, record types, and the accounts and employees that acted.</summary>
    [HttpGet("facets")]
    [ProducesResponseType<AuditEventFacets>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Facets(CancellationToken cancellationToken) =>
        Ok(await audit.FacetsAsync(cancellationToken));

    /// <summary>The event with its values before and after the change.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<AuditEventDetail>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        (await audit.GetAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>The record of the event, to open it as it is now; 404 MSG-ACCOUNT-22 when it no longer exists.</summary>
    [HttpGet("{id:guid}/record")]
    [ProducesResponseType<AuditEventRecord>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Record(Guid id, CancellationToken cancellationToken) =>
        (await audit.OpenRecordAsync(id, cancellationToken)).ToActionResult(Ok);
}
