using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Storage;

namespace OMTS.Application.Features.Traceability.Origin;

/// <summary>A product as a public page introduces it, from the catalogue: name, description, shell size and pictures; never the price or the stock.</summary>
public sealed record ProvenanceProduct(string Name, string? Description, decimal? SizeMinCm, decimal? SizeMaxCm, IReadOnlyList<string> PhotoUrls);

public static class ShowcaseProducts
{
    private static readonly TimeSpan LinkLifetime = TimeSpan.FromHours(1);

    public static async Task<IReadOnlyList<ProvenanceProduct>> ViewsAsync(IReadOnlyList<ShowcaseProductRow> rows, IFileStorage storage, CancellationToken cancellationToken)
    {
        var views = new List<ProvenanceProduct>(rows.Count);
        foreach (var row in rows)
        {
            var urls = new List<string>(row.PhotoKeys.Count);
            foreach (var key in row.PhotoKeys)
            {
                urls.Add(await storage.GetReadUrlAsync(key, LinkLifetime, cancellationToken));
            }

            views.Add(new ProvenanceProduct(row.Name, row.Description, row.SizeMinCm, row.SizeMaxCm, urls));
        }

        return views;
    }
}
