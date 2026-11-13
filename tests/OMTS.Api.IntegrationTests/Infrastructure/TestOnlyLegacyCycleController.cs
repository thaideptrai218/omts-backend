using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Controllers;
using OMTS.Api.Filters;
using OMTS.Application.Features.Cultivation.Cycles;

namespace OMTS.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Test seam for the OLD way of starting a cycle, with no nursery batch (v2, 13/11/2026 closed it for every public route): the hundreds of tests of harvest, sales, traceability and the old cycle screens need a cycle
/// on a raft, which the old start gives them with the very rules they were written for (plan estimates, photos). It calls the same service method the demo bootstrap calls. Loaded only by <see cref="AuthApiFactory"/>.
/// </summary>
[ApiController]
[Authorize]
public sealed class TestOnlyLegacyCycleController(CultivationCycleService cycles) : ControllerBase
{
    private const string PhotosField = "Photos";

    [HttpPost("test-only/rafts/{raftId:guid}/cultivation-cycles")]
    [RequestSizeLimit(35 * 1024 * 1024)]
    [UploadField(PhotosField)]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> Start(Guid raftId, [FromForm] CyclePlanForm form, CancellationToken cancellationToken) =>
        !Request.HasFormContentType
            ? StatusCode(StatusCodes.Status415UnsupportedMediaType)
            : (await cycles.StartAsync(raftId, form.ToStart(Request.Form.Files.HasPartOtherThan(PhotosField)), await form.Photos.ToIncomingFilesAsync(cancellationToken), cancellationToken))
                .ToActionResult(r => StatusCode(StatusCodes.Status201Created, r));
}
