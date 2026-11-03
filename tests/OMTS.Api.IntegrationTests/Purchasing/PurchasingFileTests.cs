using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>INP-01 certificates and INP-03 invoice photos: real file type, 10 MB, 10 files, storage failure (503 MSG04), zones and audit.</summary>
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

    [Fact]
    public async Task Invoice_photos_follow_the_same_file_rules_the_zone_check_and_are_copied_with_the_receipt()
    {
        var world = await factory.WorldAsync();
        var draft = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 100_000)));
        var url = $"/api/purchase-receipts/{draft.Id()}/invoice-photos";

        Assert.Equal("note.txt", await (await world.Owner.PostAsync(url, Form((Text, "note.txt")))).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal("huge.jpg", await (await world.Owner.PostAsync(url, Form((Sized(Jpeg, 10 * 1024 * 1024 + 1), "huge.jpg")))).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal("Invoice photos", await (await world.Owner.PostAsync(url, Form(Enumerable.Range(1, 11).Select(i => (Png, $"p{i}.png")).ToArray()))).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal("Invoice photos", await (await world.Owner.PostAsync(url, Form())).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));

        var (otherZone, _) = await factory.ZoneWithRaftAsync();
        var otherManager = await factory.ZoneManagerAsync(otherZone);
        await (await otherManager.PostAsync(url, Form((Png, "invoice.png")))).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        Assert.Empty(await factory.QueryAsync(db => db.PurchaseReceipts.Where(r => r.Id == draft.Id()).Select(r => r.InvoicePhotoUrls).SingleAsync()));

        var manager = await factory.ZoneManagerAsync(world.Zone);
        var withPhotos = await (await manager.PostAsync(url, Form((Png, "invoice.png"), (Pdf, "invoice.pdf")))).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(2, withPhotos.GetProperty("invoicePhotoUrls").GetArrayLength());
        var keys = await factory.QueryAsync(db => db.PurchaseReceipts.Where(r => r.Id == draft.Id()).Select(r => r.InvoicePhotoUrls).SingleAsync());
        Assert.All(keys, k => Assert.StartsWith("purchase-invoices/", k));
        var audit = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == draft.Id() && e.Action == "PurchaseReceipt.Updated"));
        Assert.Equal(world.Zone.Id, audit.ZoneId);

        await (await world.Owner.PostAsync($"/api/purchase-receipts/{draft.Id()}/complete", null)).ReadAsync(HttpStatusCode.OK);
        var copy = await (await world.Owner.PostAsync($"/api/purchase-receipts/{draft.Id()}/copy", null)).ReadAsync(HttpStatusCode.Created);
        Assert.Equal(keys, await factory.QueryAsync(db => db.PurchaseReceipts.Where(r => r.Id == copy.Id()).Select(r => r.InvoicePhotoUrls).SingleAsync()));
    }

    [Fact]
    public async Task A_storage_failure_answers_503_MSG_GEN_03_and_leaves_supplier_and_receipt_unchanged()
    {
        using var api = new AuthApiFactory();
        // A file where a category folder should be makes the real Local provider fail every upload of that category.
        Directory.CreateDirectory(api.StorageRoot);
        await File.WriteAllTextAsync(Path.Combine(api.StorageRoot, "supplier-certificates"), "blocked");
        await File.WriteAllTextAsync(Path.Combine(api.StorageRoot, "purchase-invoices"), "blocked");
        var world = await api.WorldAsync();
        var draft = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 100_000)));

        var certificate = await world.Owner.PostAsync($"/api/suppliers/{world.SupplierId}/certificates", Form((Pdf, "vietgap.pdf")));
        var problem = await certificate.ProblemAsync(HttpStatusCode.ServiceUnavailable, "MSG-GEN-03");
        Assert.Equal("The file could not be uploaded. Your other entries are kept; please try again.", problem.GetProperty("detail").GetString());
        Assert.Equal("MSG04", problem.GetProperty("legacyCode").GetString());
        var photo = await world.Owner.PostAsync($"/api/purchase-receipts/{draft.Id()}/invoice-photos", Form((Png, "invoice.png")));
        await photo.ProblemAsync(HttpStatusCode.ServiceUnavailable, "MSG-GEN-03");

        Assert.Empty(await api.QueryAsync(db => db.Suppliers.Where(s => s.Id == world.SupplierId).Select(s => s.CertificateUrls).SingleAsync()));
        Assert.Empty(await api.QueryAsync(db => db.PurchaseReceipts.Where(r => r.Id == draft.Id()).Select(r => r.InvoicePhotoUrls).SingleAsync()));
        Assert.Equal(0, await api.QueryAsync(db => db.AuditEvents.CountAsync(e => (e.RecordId == world.SupplierId || e.RecordId == draft.Id()) && e.Action.EndsWith(".Updated"))));
        Assert.Equal(2, Directory.GetFileSystemEntries(api.StorageRoot, "*", SearchOption.AllDirectories).Length);
    }
}
