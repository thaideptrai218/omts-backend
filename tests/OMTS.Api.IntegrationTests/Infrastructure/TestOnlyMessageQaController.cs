using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Filters;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;

namespace OMTS.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Test-only endpoints of the message-code QA (OMTS-216 PR 0.2): the paths of the exception handler that no real
/// endpoint reaches on purpose, and a zone-scoped permission check that reads the zone from the query string.
/// </summary>
[ApiController]
[Route("test-only/qa-messages")]
public sealed class TestOnlyMessageQaController : ControllerBase
{
    /// <summary>A zone-scoped check whose zone comes from <c>?zoneId=</c>; without it the filter answers 400.</summary>
    [HttpGet("zone-from-query")]
    [RequirePermission(PermissionFunctions.WaterMeasurements, PermissionAction.View)]
    public IActionResult ZoneFromQuery() => Ok();

    /// <summary>A unique index broke after the pre-check: the handler answers 409.</summary>
    [HttpGet("duplicate")]
    public IActionResult Duplicate() => throw new DuplicateRecordException(new InvalidOperationException("unique index"));

    /// <summary>An optimistic concurrency conflict nobody handled: the handler answers 409.</summary>
    [HttpGet("concurrency")]
    public IActionResult Concurrency() => throw new ConcurrencyConflictException(new InvalidOperationException("row version"));

    /// <summary>A programming error: the handler answers 500 without details.</summary>
    [HttpGet("crash")]
    public IActionResult Crash() => throw new InvalidOperationException("secret internal detail");
}
