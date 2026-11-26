using Microsoft.Extensions.DependencyInjection;
using OMTS.Application.Common.Interfaces.Ocr;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Files;
using OMTS.Application.Features.Scans;

namespace OMTS.Application.Features.Farming.Rafts.FormScan;

/// <summary>
/// Reads the fields of a photographed raft form (FARM-04) into suggestions for the create-raft screen, which replaces the
/// on-device Tesseract reading. Same permission as creating a raft (Cooperative Owner, checked by the API filter). Nothing is
/// saved: the Owner reviews the fields and creates the raft with <c>POST api/zones/{zoneId}/rafts</c>.
/// </summary>
public sealed class RaftFormScanService(PhotoScanGateway scans)
{
    private const string Scope = "raft-form";

    public Result<ScanStarted> Start(IncomingFile? photo) => scans.Start(photo, Scope, ReadAsync);

    public Result<ScanStatusView<RaftFormReading>> Get(Guid scanId) => scans.Get<RaftFormReading>(scanId, Scope);

    /// <summary>Runs on a queue worker with its own service scope; never throws for a provider problem.</summary>
    private static async Task<ScanOutcome> ReadAsync(IServiceProvider services, ScannedPhoto photo, CancellationToken cancellationToken)
    {
        var read = await services.GetRequiredService<IRaftFormReader>().ReadAsync(photo.Bytes, photo.MimeType, cancellationToken);
        return read.Value is { } raw
            ? ScanOutcome.Done(RaftFormNormaliser.Normalise(raw))
            : ScanOutcome.Failed(read.ErrorCode ?? OcrErrorCodes.BadResponse);
    }
}
