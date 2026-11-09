using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>A sent claim with its world and the row version the Owner read.</summary>
public sealed record QaClaim(ClaimWorld World, JsonElement Claim, string RowVersion)
{
    public HttpClient Owner => World.Sale.Owner.Client;

    public Guid Id => Claim.Id();
}

/// <summary>One part of a multipart body, with what a client controls: the part name, the file name and the content type.</summary>
public sealed record ClaimPart(byte[] Content, string Name = "photos", string? FileName = "photo.jpg", string? ContentType = "image/jpeg");

/// <summary>Set-up of the QA tests of CLAIM-01 and CLAIM-02 (OMTS-225).</summary>
public static class QualityClaimQaKit
{
    public static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01];

    public static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52];

    public static async Task<QaClaim> QaClaimedAsync(this AuthApiFactory api, IReadOnlyList<byte[]>? photos = null)
    {
        var world = await api.ClaimWorldAsync();
        var claim = await world.Trader.Client.SubmitClaimAsync(world.InvoiceId, photos);
        var rowVersion = (await world.Sale.Owner.Client.ClaimAsStaffAsync(claim.Id())).Text("rowVersion")!;
        return new QaClaim(world, claim, rowVersion);
    }

    /// <summary>The multipart body as a client builds it, part by part.</summary>
    public static Task<HttpResponseMessage> PostPartsAsync(this HttpClient trader, Guid invoiceId, string? description, params ClaimPart[] parts)
    {
        var form = new MultipartFormDataContent();
        if (description is not null)
        {
            form.Add(new StringContent(description), "description");
        }

        foreach (var part in parts)
        {
            var content = new ByteArrayContent(part.Content);
            if (part.ContentType is not null)
            {
                content.Headers.ContentType = new MediaTypeHeaderValue(part.ContentType);
            }

            if (part.FileName is null)
            {
                form.Add(content, part.Name);
            }
            else
            {
                form.Add(content, part.Name, part.FileName);
            }
        }

        return trader.PostAsync($"/api/me/sales-invoices/{invoiceId}/quality-claims", form);
    }

    public static Task<HttpResponseMessage> PostRawClaimAsync(this HttpClient trader, Guid invoiceId, HttpContent content) =>
        trader.PostAsync($"/api/me/sales-invoices/{invoiceId}/quality-claims", content);

    public static async Task<int?> CodeOfAsync(this HttpResponseMessage response)
    {
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        await response.Content.ReadAsStringAsync();
        return (int)response.StatusCode;
    }

    public static IEnumerable<JsonElement> Items(this JsonElement page) => page.GetProperty("items").EnumerateArray();
}
