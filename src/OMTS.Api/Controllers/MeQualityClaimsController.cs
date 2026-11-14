using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Features.Sales.QualityClaims;

namespace OMTS.Api.Controllers;

/// <summary>
/// CLAIM-01 and the trader's view of their claims: only the customer profile linked to the logged-in trader. An invoice or a claim of another trader answers 404 (BR-ACCOUNT-04).
/// </summary>
[ApiController]
[Route("api/me")]
[Authorize]
[RequireTrader]
public sealed class MeQualityClaimsController(
    QualityClaimSubmissionService submissions,
    QualityClaimReadService reads,
    QualityClaimAnswerService answers,
    ICurrentUser currentUser) : ControllerBase
{
    private const string PhotosField = "Photos";

    // Ten photos of 10 MB plus the multipart envelope; a larger body answers MSG-GEN-01 on the photos.
    private const long MaxUploadBytes = 105 * 1024 * 1024;

    private Guid AccountId => currentUser.AccountId!.Value;

    /// <summary>The trader's own claims, newest first; MSG-GEN-11 when there is none yet.</summary>
    [HttpGet("quality-claims")]
    [ProducesResponseType<QualityClaimsPage>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Mine(int page = 1, int pageSize = 20, CancellationToken cancellationToken = default) =>
        Ok(await reads.MineAsync(AccountId, page, pageSize, cancellationToken));

    /// <summary>One claim with its photos (links valid for one hour) and the response, the record the response notification opens.</summary>
    [HttpGet("quality-claims/{claimId:guid}")]
    [ProducesResponseType<TraderQualityClaimView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMine(Guid claimId, CancellationToken cancellationToken) =>
        (await reads.GetMineAsync(AccountId, claimId, cancellationToken)).ToActionResult(Ok);

    /// <summary>The trader accepts the offer of the Owner (status Offer made), with an optional <c>note</c>. 200 with the claim and MSG-GEN-05; 404 for a claim that is not theirs; 409 MSG-GEN-09 when it is not waiting for their answer.</summary>
    [HttpPost("quality-claims/{claimId:guid}/accept-offer")]
    [ProducesResponseType<QualityClaimSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> AcceptOffer(Guid claimId, AnswerQualityClaimOffer? request, CancellationToken cancellationToken) =>
        (await answers.AcceptAsync(AccountId, claimId, request ?? new AnswerQualityClaimOffer(null), cancellationToken)).ToActionResult(Ok);

    /// <summary>The trader declines the offer of the Owner, with an optional <c>note</c>. Same answers as accepting.</summary>
    [HttpPost("quality-claims/{claimId:guid}/decline-offer")]
    [ProducesResponseType<QualityClaimSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> DeclineOffer(Guid claimId, AnswerQualityClaimOffer? request, CancellationToken cancellationToken) =>
        (await answers.DeclineAsync(AccountId, claimId, request ?? new AnswerQualityClaimOffer(null), cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// Sends a quality claim on one of the trader's invoices. Multipart: <c>description</c> and one <c>photos</c> part per photo. 201 with the claim and MSG-GEN-05; 400 MSG-GEN-01 for what is missing;
    /// 404 for an invoice that is not theirs; 409 MSG-GEN-09 when it is cancelled, returned or already claimed; 502 MSG-GEN-03 when a photo cannot be uploaded (nothing is saved).
    /// </summary>
    [HttpPost("sales-invoices/{invoiceId:guid}/quality-claims")]
    [RequestSizeLimit(MaxUploadBytes)]
    [UploadField(PhotosField)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<QualityClaimSaved>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Submit(Guid invoiceId, [FromForm] QualityClaimForm form, CancellationToken cancellationToken) =>
        (await submissions.SubmitAsync(
            AccountId,
            invoiceId,
            new SubmitQualityClaim(form.Description, Request.Form.Files.HasPartOtherThan(nameof(QualityClaimForm.Photos))),
            await form.Photos.ToIncomingFilesAsync(cancellationToken),
            cancellationToken)).ToActionResult(r => StatusCode(StatusCodes.Status201Created, r));
}
