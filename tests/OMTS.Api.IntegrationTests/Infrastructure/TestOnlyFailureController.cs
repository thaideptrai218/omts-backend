using Microsoft.AspNetCore.Mvc;
using OMTS.Application.Common.Exceptions;

namespace OMTS.Api.IntegrationTests.Infrastructure;

/// <summary>Test-only endpoints that fail on purpose, to check the API's exception mapping.</summary>
[ApiController]
[Route("test-only")]
public sealed class TestOnlyFailureController : ControllerBase
{
    [HttpGet("storage-read-failure")]
    public IActionResult StorageReadFailure() =>
        throw new FileStorageUnavailableException("evidence/2026/10/00000000000000000000000000000000.png", new IOException("disk offline"));
}
