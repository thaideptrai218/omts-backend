using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>PURCHASE-07 certificates: real file type, 10 MB, 10 files, storage failure (503 MSG-GEN-03) and audit; receipts take no photo.</summary>
public class PurchasingFileTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static readonly byte[] Pdf = "%PDF-1.4\n%certificate\n"u8.ToArray();
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01];
    private static readonly byte[] WebP = "RIFF\x10\0\0\0WEBPVP8 "u8.ToArray();
    private static readonly byte[] Text = "MZ pretending to be a pdf"u8.ToArray();

    private static MultipartFormDataContent Form(params (byte[] Bytes, string Name)[] files)
    {
        // A plain field keeps the multipart body valid when no file is sent.
        var form = new MultipartFormDataContent { { new StringContent("1"), "source" } };
        foreach (var (bytes, name) in files)
        {
            var content = new ByteArrayContent(bytes);
            // The client's content type is ignored: the format comes from the bytes.
            content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            form.Add(content, "files", name);
        }

        return form;
    }

    private static byte[] Sized(byte[] header, int size)
    {
        var bytes = new byte[size];
        header.CopyTo(bytes, 0);
        return bytes;
    }

    [Fact]
    public async Task Certificates_are_added_as_pdf_png_or_jpeg_and_removing_one_keeps_the_stored_file()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var supplierId = await owner.CreateSupplierAsync();

        var added = await (await owner.PostAsync($"/api/suppliers/{supplierId}/certificates", Form((Pdf, "vietgap.pdf"), (Png, "scan.png"), (Jpeg, "photo.jpg"))))
            .ReadAsync(HttpStatusCode.OK);
        var certificates = added.GetProperty("certificates").EnumerateArray().ToList();
        Assert.Equal([0, 1, 2], certificates.Select(c => c.GetProperty("index").GetInt32()));
        Assert.All(certificates, c => Assert.False(string.IsNullOrEmpty(c.GetProperty("url").GetString())));
        var keys = await factory.QueryAsync(db => db.Suppliers.Where(s => s.Id == supplierId).Select(s => s.CertificateUrls).SingleAsync());
        Assert.Equal([".pdf", ".png", ".jpg"], keys.Select(Path.GetExtension));
        Assert.All(keys, k => Assert.StartsWith("supplier-certificates/", k));

        var removed = await (await owner.DeleteAsync($"/api/suppliers/{supplierId}/certificates/1")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(2, removed.GetProperty("certificates").GetArrayLength());
        Assert.Equal([keys[0], keys[2]], await factory.QueryAsync(db => db.Suppliers.Where(s => s.Id == supplierId).Select(s => s.CertificateUrls).SingleAsync()));
        Assert.True(File.Exists(Path.Combine(factory.StorageRoot, keys[1])));
        Assert.Equal("Certificate", await (await owner.DeleteAsync($"/api/suppliers/{supplierId}/certificates/5")).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));

        Assert.Equal(2, await factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == supplierId && e.Action == "Supplier.Updated")));
    }

    [Fact]
    public async Task A_wrong_type_a_file_over_10_MB_or_more_than_10_files_answer_MSG01_naming_the_file_and_save_nothing()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var supplierId = await owner.CreateSupplierAsync();
        var url = $"/api/suppliers/{supplierId}/certificates";

        Assert.Equal("fake.pdf", await (await owner.PostAsync(url, Form((Pdf, "ok.pdf"), (Text, "fake.pdf")))).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal("logo.webp", await (await owner.PostAsync(url, Form((WebP, "logo.webp")))).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal("big.pdf", await (await owner.PostAsync(url, Form((Sized(Pdf, 10 * 1024 * 1024 + 1), "big.pdf")))).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        var eleven = Enumerable.Range(1, 11).Select(i => (Pdf, $"c{i}.pdf")).ToArray();
        Assert.Equal("Certificates", await (await owner.PostAsync(url, Form(eleven))).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal("Certificates", await (await owner.PostAsync(url, Form())).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Empty(await factory.QueryAsync(db => db.Suppliers.Where(s => s.Id == supplierId).Select(s => s.CertificateUrls).SingleAsync()));

        // Exactly 10 MB and 10 files are accepted; one more certificate is then over the limit of 10 kept.
        await (await owner.PostAsync(url, Form(Enumerable.Range(1, 9).Select(i => (Pdf, $"c{i}.pdf")).Append((Sized(Png, 10 * 1024 * 1024), "max.png")).ToArray())))
            .ReadAsync(HttpStatusCode.OK);
        Assert.Equal("Certificates", await (await owner.PostAsync(url, Form((Pdf, "eleventh.pdf")))).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal(10, (await factory.QueryAsync(db => db.Suppliers.Where(s => s.Id == supplierId).Select(s => s.CertificateUrls).SingleAsync())).Count);
    }

    // BR-PURCHASE-01: a purchase receipt has no photo of the supplier's invoice, so it has no upload endpoint any more.
    [Fact]
    public async Task A_receipt_takes_no_invoice_photo()
    {
        var world = await factory.WorldAsync();
        var draft = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 100_000)));

        var response = await world.Owner.PostAsync($"/api/purchase-receipts/{draft.Id()}/invoice-photos", Form((Png, "invoice.png")));

        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, response.StatusCode.ToString());
        Assert.False(draft.TryGetProperty("invoicePhotoUrls", out _));
    }

    [Fact]
    public async Task A_storage_failure_answers_503_MSG_GEN_03_and_leaves_the_supplier_unchanged()
    {
        using var api = new AuthApiFactory();
        // A file where a category folder should be makes the real Local provider fail every upload of that category.
        Directory.CreateDirectory(api.StorageRoot);
        await File.WriteAllTextAsync(Path.Combine(api.StorageRoot, "supplier-certificates"), "blocked");
        var world = await api.WorldAsync();

        var certificate = await world.Owner.PostAsync($"/api/suppliers/{world.SupplierId}/certificates", Form((Pdf, "vietgap.pdf")));
        var problem = await certificate.ProblemAsync(HttpStatusCode.ServiceUnavailable, "MSG-GEN-03");
        Assert.Equal("The file could not be uploaded. Your other entries are kept; please try again.", problem.GetProperty("detail").GetString());
        Assert.Equal("MSG04", problem.GetProperty("legacyCode").GetString());

        Assert.Empty(await api.QueryAsync(db => db.Suppliers.Where(s => s.Id == world.SupplierId).Select(s => s.CertificateUrls).SingleAsync()));
        Assert.Equal(0, await api.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == world.SupplierId && e.Action.EndsWith(".Updated"))));
        Assert.Single(Directory.GetFileSystemEntries(api.StorageRoot, "*", SearchOption.AllDirectories));
    }
}
