using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Authorization;
using OMTS.Application.Authorization;

namespace OMTS.Api.IntegrationTests;

/// <summary>Test-only endpoints exercising the authorization attributes; loaded only by <see cref="AuthApiFactory"/>.</summary>
[ApiController]
[Route("test-only")]
public sealed class TestOnlyAuthorizationController : ControllerBase
{
    [HttpGet("zones/{zoneId:guid}/rafts")]
    [RequirePermission(PermissionFunctions.Rafts, PermissionAction.View)]
    public IActionResult ViewRafts(Guid zoneId) => Ok(new { zoneId });

    [HttpPost("zones/{zoneId:guid}/rafts")]
    [RequirePermission(PermissionFunctions.Rafts, PermissionAction.Create)]
    public IActionResult CreateRaft(Guid zoneId) => Ok(new { zoneId });

    [HttpGet("seed-suppliers")]
    [RequirePermission(PermissionFunctions.SeedSuppliers, PermissionAction.View, PermissionScope.AllZones)]
    public IActionResult ViewSuppliers() => Ok();

    [HttpPost("change-password")]
    [Authorize]
    [AllowWhenPasswordChangeRequired]
    public IActionResult ChangePassword() => Ok();

    [HttpPost("verify-phone")]
    [Authorize]
    [AllowWhenPhoneUnverified]
    public IActionResult VerifyPhone() => Ok();
}
