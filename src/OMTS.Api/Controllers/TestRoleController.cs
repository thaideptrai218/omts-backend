using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Testing;

namespace OMTS.Api.Controllers;

[ApiController]
[Authorize]
[DevelopmentOnly]
[Route("api/me/test-role")]
public sealed class TestRoleController(TestRoleService service, IWebHostEnvironment environment) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct) => !environment.IsDevelopment() ? NotFound()
        : (await service.AvailabilityAsync(ct)).ToActionResult(Ok);

    [HttpPost]
    public async Task<IActionResult> Switch(TestRoleRequest request, CancellationToken ct) => !environment.IsDevelopment() ? NotFound()
        : (await service.SwitchAsync(request, HttpContext.ClientInfo(), ct)).ToActionResult(Ok);
}
