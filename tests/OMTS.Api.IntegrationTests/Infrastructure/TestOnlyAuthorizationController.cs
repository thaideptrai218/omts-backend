using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Filters;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;

namespace OMTS.Api.IntegrationTests.Infrastructure;

/// <summary>Test-only endpoints exercising the authorization attributes; loaded only by <see cref="AuthApiFactory"/>.</summary>
[ApiController]
[Route("test-only")]
public sealed class TestOnlyAuthorizationController : ControllerBase
{
    [HttpGet("zones/{zoneId:guid}/water-measurements")]
    [RequirePermission(PermissionFunctions.WaterMeasurements, PermissionAction.View)]
    public IActionResult ViewWaterMeasurements(Guid zoneId) => Ok(new { zoneId });

    [HttpPost("zones/{zoneId:guid}/water-measurements")]
    [RequirePermission(PermissionFunctions.WaterMeasurements, PermissionAction.Create)]
    public IActionResult CreateWaterMeasurement(Guid zoneId) => Ok(new { zoneId });

    [HttpGet("supplier-payments")]
    [RequirePermission(PermissionFunctions.SupplierPayments, PermissionAction.View, PermissionScope.AllZones)]
    public IActionResult ViewSupplierPayments() => Ok();

    [HttpPost("change-password")]
    [Authorize]
    [AllowWhenPasswordChangeRequired]
    public IActionResult ChangePassword() => Ok();

    [HttpPost("verify-phone")]
    [Authorize]
    [AllowWhenPhoneUnverified]
    public IActionResult VerifyPhone() => Ok();
}
