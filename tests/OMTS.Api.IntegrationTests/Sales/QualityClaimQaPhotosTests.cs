using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Features.Files;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.Sales.QualityClaims;
using OMTS.Domain.Constants;
using OMTS.Infrastructure.Jobs;
using static OMTS.Api.IntegrationTests.Cooperatives.CooperativeProfileQaKit;
using static OMTS.Api.IntegrationTests.Sales.SalesCatalogueTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>QA of the photos of CLAIM-01 (GFR-01, NFR-14, BR-CLAIM-01): limits, tricks and what is left in storage.</summary>
public class QualityClaimQaPhotosTests
{
    private const string Category = FileCategories.QualityClaimPhotos;

    private static readonly byte[] Exe = "MZ this is not a photo"u8.ToArray();

    private static byte[] Sized(byte[] head, int size)
    {
        var bytes = new byte[size];
        head.CopyTo(bytes, 0);
        return bytes;
    }

    /// <summary>The first look at the invoice passes, the second (after the uploads) finds a sales return.</summary>
    private sealed class ReturnedAfterUploadApiFactory : AuthApiFactory
    {
        private sealed class ReturnedOnSecondCall : IInvoiceSalesReturnCheck
        {
            private int _calls;

            public Task<bool> HasSalesReturnAsync(Guid invoiceId, CancellationToken cancellationToken) => Task.FromResult(Interlocked.Increment(ref _calls) > 1);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.AddSingleton<IInvoiceSalesReturnCheck>(new ReturnedOnSecondCall()));
        }
    }

    [Fact]
    public async Task A_file_that_only_claims_to_be_a_photo_is_refused_whatever_its_name_and_content_type()
    {
        using var api = new QualityClaimApiFactory();
        var world = await api.ClaimWorldAsync();

        var answer = await world.Trader.Client.PostPartsAsync(world.InvoiceId, "x", new ClaimPart(Exe, FileName: "photo.jpg", ContentType: "image/jpeg"));

        Assert.Equal(["Photos"], await answer.InvalidFieldsAsync());
        Assert.Empty(StoredKeys(api, Category));
        Assert.Equal(0, await api.QueryAsync(db => db.QualityClaims.CountAsync()));
    }

    [Fact]
    public async Task The_real_format_decides_the_key_and_a_client_file_name_never_reaches_storage_or_the_answer()
    {
        using var api = new QualityClaimApiFactory();
        var world = await api.ClaimWorldAsync();

        var answer = await (await world.Trader.Client.PostPartsAsync(world.InvoiceId, "x", [
            new ClaimPart(QualityClaimQaKit.Jpeg, FileName: "..\\..\\evil.exe", ContentType: "text/plain"),
            new ClaimPart(QualityClaimQaKit.Png, FileName: "../../../etc/passwd.jpg", ContentType: "text/html"),
            new ClaimPart(QualityClaimQaKit.Jpeg, FileName: new string('a', 400) + ".png", ContentType: null),
        ])).ReadAsync(HttpStatusCode.Created);

        var keys = StoredKeys(api, Category);
        Assert.Equal(3, keys.Count);
        Assert.All(keys, key => Assert.Matches(new Regex(@"^claim-photos/\d{4}/\d{2}/[0-9a-f]{32}\.(jpg|png)$"), key));
        Assert.Equal(2, keys.Count(k => k.EndsWith(".jpg", StringComparison.Ordinal)));
        Assert.Equal(1, keys.Count(k => k.EndsWith(".png", StringComparison.Ordinal)));
        Assert.DoesNotContain("evil", answer.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("passwd", answer.GetRawText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("gif", new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0, 0 })]
    [InlineData("webp", new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50, 0, 0 })]
    [InlineData("pdf", new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x34 })]
    public async Task Formats_other_than_jpeg_and_png_are_refused(string name, byte[] content)
    {
        using var api = new QualityClaimApiFactory();
        var world = await api.ClaimWorldAsync();

        var answer = await world.Trader.Client.PostPartsAsync(world.InvoiceId, "x", new ClaimPart(content, FileName: $"photo.{name}"));

        Assert.Equal(["Photos"], await answer.InvalidFieldsAsync());
        Assert.Empty(StoredKeys(api, Category));
    }

    [Fact]
    public async Task An_empty_file_or_one_bad_file_among_good_ones_stores_nothing()
    {
        using var api = new QualityClaimApiFactory();
        var world = await api.ClaimWorldAsync();
        var trader = world.Trader.Client;

        Assert.Equal(["Photos"], await (await trader.PostPartsAsync(world.InvoiceId, "x", new ClaimPart([], FileName: "empty.jpg"))).InvalidFieldsAsync());
        Assert.Equal(["Photos"], await (await trader.PostPartsAsync(world.InvoiceId, "x", new ClaimPart(QualityClaimQaKit.Jpeg), new ClaimPart(Exe))).InvalidFieldsAsync());

        Assert.Empty(StoredKeys(api, Category));
        Assert.Equal(0, await api.QueryAsync(db => db.QualityClaims.CountAsync()));
    }

    [Fact]
    public async Task A_photo_of_exactly_ten_megabytes_is_kept_and_one_byte_more_is_refused()
    {
        using var api = new QualityClaimApiFactory();
        var world = await api.ClaimWorldAsync();
        var trader = world.Trader.Client;
        var limit = 10 * 1024 * 1024;

        Assert.Equal(["Photos"], await (await trader.PostPartsAsync(world.InvoiceId, "x", new ClaimPart(Sized(QualityClaimQaKit.Jpeg, limit + 1)))).InvalidFieldsAsync());
        Assert.Empty(StoredKeys(api, Category));

        await (await trader.PostPartsAsync(world.InvoiceId, "x", new ClaimPart(Sized(QualityClaimQaKit.Jpeg, limit)))).ReadAsync(HttpStatusCode.Created);
        Assert.Single(StoredKeys(api, Category));
    }

    [Fact]
    public async Task A_text_part_named_photos_is_not_a_photo_and_another_file_part_is_refused()
    {
        using var api = new QualityClaimApiFactory();
        var world = await api.ClaimWorldAsync();
        var trader = world.Trader.Client;

        Assert.Equal(["Photos"], await (await trader.PostPartsAsync(world.InvoiceId, "x", new ClaimPart(Encoding.UTF8.GetBytes("not a file"), FileName: null, ContentType: null))).InvalidFieldsAsync());
        Assert.Equal(["Photos"], await (await trader.PostPartsAsync(world.InvoiceId, "x", new ClaimPart(QualityClaimQaKit.Jpeg), new ClaimPart(QualityClaimQaKit.Jpeg, Name: "other"))).InvalidFieldsAsync());
        Assert.Equal(["Photos", "Description"], (await (await trader.PostPartsAsync(world.InvoiceId, null, new ClaimPart(Encoding.UTF8.GetBytes("x"), Name: "other", FileName: null, ContentType: null))).InvalidFieldsAsync()).OrderDescending().ToList());
        Assert.Empty(StoredKeys(api, Category));
    }

    [Theory]
    [InlineData("Photos")]
    [InlineData("PHOTOS")]
    public async Task The_part_name_is_matched_as_the_binder_matches_it(string partName)
    {
        using var api = new QualityClaimApiFactory();
        var world = await api.ClaimWorldAsync();

        await (await world.Trader.Client.PostPartsAsync(world.InvoiceId, "x", new ClaimPart(QualityClaimQaKit.Jpeg, Name: partName))).ReadAsync(HttpStatusCode.Created);
        Assert.Single(StoredKeys(api, Category));
    }

    [Theory]
    [InlineData("multipart/form-data", "no boundary at all")]
    [InlineData("multipart/form-data; boundary=x", "--x\r\nContent-Disposition: form-data; name=\"description\"\r\n\r\nx")]
    [InlineData("multipart/form-data; boundary=x", "")]
    public async Task A_malformed_multipart_body_answers_400_or_415_never_500(string contentType, string body)
    {
        using var api = new QualityClaimApiFactory();
        var world = await api.ClaimWorldAsync();
        var content = new StringContent(body, Encoding.UTF8);
        content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);

        var answer = await world.Trader.Client.PostRawClaimAsync(world.InvoiceId, content);

        Assert.Contains((int)answer.StatusCode, new[] { 400, 415 });
        Assert.Empty(StoredKeys(api, Category));
    }

    [Theory]
    [InlineData("application/json", "{}")]
    [InlineData("application/x-www-form-urlencoded", "description=x")]
    [InlineData("text/plain", "x")]
    public async Task Another_body_type_is_refused_and_saves_nothing(string contentType, string body)
    {
        using var api = new QualityClaimApiFactory();
        var world = await api.ClaimWorldAsync();
        var content = new StringContent(body, Encoding.UTF8);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);

        var answer = await world.Trader.Client.PostRawClaimAsync(world.InvoiceId, content);

        Assert.InRange((int)answer.StatusCode, 400, 499);
        Assert.Equal(0, await api.QueryAsync(db => db.QualityClaims.CountAsync()));
    }

    [Theory]
    [InlineData(2000, true)]
    [InlineData(2001, false)]
    public async Task The_description_limit_counts_after_the_trim(int length, bool saved)
    {
        using var api = new QualityClaimApiFactory();
        var world = await api.ClaimWorldAsync();

        var answer = await world.Trader.Client.PostPartsAsync(world.InvoiceId, "  " + new string('d', length) + "  ", new ClaimPart(QualityClaimQaKit.Jpeg));

        if (saved)
        {
            await answer.ReadAsync(HttpStatusCode.Created);
        }
        else
        {
            Assert.Equal(["Description"], await answer.InvalidFieldsAsync());
        }
    }

    [Fact]
    public async Task A_description_of_blank_characters_is_missing_and_a_huge_one_never_answers_500()
    {
        using var api = new QualityClaimApiFactory();
        var world = await api.ClaimWorldAsync();
        var trader = world.Trader.Client;

        Assert.Equal(["Description"], await (await trader.PostPartsAsync(world.InvoiceId, "\t\r\n  ", new ClaimPart(QualityClaimQaKit.Jpeg))).InvalidFieldsAsync());
        var huge = await trader.PostPartsAsync(world.InvoiceId, new string('x', 5 * 1024 * 1024), new ClaimPart(QualityClaimQaKit.Jpeg));
        Assert.Equal(HttpStatusCode.BadRequest, huge.StatusCode);
        Assert.Empty(StoredKeys(api, Category));
    }

    [Fact]
    public async Task A_refusal_after_the_uploads_removes_them_and_tells_nobody()
    {
        using var api = new ReturnedAfterUploadApiFactory();
        var world = await api.ClaimWorldAsync();

        var answer = await (await world.Trader.Client.PostClaimAsync(world.InvoiceId)).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");

        Assert.Contains("returned", answer.Text("detail"));
        Assert.Empty(StoredKeys(api, Category));
        Assert.Equal(0, await api.QueryAsync(db => db.QualityClaims.CountAsync()));
        Assert.Equal(0, await api.QueryAsync(db => db.Notifications.CountAsync(n => n.Type == NotificationTypes.QualityClaimNew)));
    }

    [Fact]
    public async Task A_failed_save_removes_the_uploads_and_a_save_committed_before_the_failure_keeps_them()
    {
        using var api = new FileFaultApiFactory();
        var world = await api.ClaimWorldAsync();
        api.Faults.FailNextSave(new InvalidOperationException("Test: database down."));

        var failed = await world.Trader.Client.PostClaimAsync(world.InvoiceId, photos: [QualityClaimQaKit.Jpeg, QualityClaimQaKit.Png]);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Empty(StoredKeys(api, Category));
        Assert.Equal(0, await api.QueryAsync(db => db.QualityClaims.CountAsync()));

        api.Faults.FailNextSaveAfterCommit(new InvalidOperationException("Test: reply lost after the commit."));
        var lost = await world.Trader.Client.PostClaimAsync(world.InvoiceId);
        Assert.Equal(HttpStatusCode.InternalServerError, lost.StatusCode);
        Assert.Single(StoredKeys(api, Category));
        Assert.Equal(1, await api.QueryAsync(db => db.QualityClaims.CountAsync()));
    }

    [Fact]
    public async Task The_clean_up_leaves_photos_of_a_database_that_never_held_a_claim_then_removes_only_real_orphans()
    {
        using var api = new QualityClaimApiFactory();
        var storage = api.Services.GetRequiredService<IFileStorage>();
        var job = api.Services.GetRequiredService<OrphanFileCleanupJob>();

        var first = FileValidator.Validate([new IncomingFile("o.jpg", QualityClaimQaKit.Jpeg.Length, new MemoryStream(QualityClaimQaKit.Jpeg))], FileRules.Photos);
        var earlyOrphan = await storage.UploadAsync(first.Files[0], Category);
        await job.RunOnceAsync(DateTimeOffset.UtcNow.AddDays(2));
        Assert.Contains(earlyOrphan.Key!, StoredKeys(api, Category));

        var world = await api.ClaimWorldAsync();
        var claim = await world.Trader.Client.SubmitClaimAsync(world.InvoiceId, [QualityClaimQaKit.Jpeg, QualityClaimQaKit.Png]);
        var live = await api.QueryAsync(db => db.QualityClaimPhotos.Where(p => p.ClaimId == claim.Id()).Select(p => p.FileKey).ToListAsync());

        await job.RunOnceAsync(DateTimeOffset.UtcNow.AddDays(2));

        var kept = StoredKeys(api, Category);
        Assert.Equal(live.Order(), kept.Order());
        Assert.DoesNotContain(earlyOrphan.Key!, kept);
    }

    [Fact]
    public async Task Links_live_one_hour_and_a_changed_link_opens_nothing()
    {
        using var api = new QualityClaimApiFactory();
        var world = await api.ClaimWorldAsync();
        var claim = await world.Trader.Client.SubmitClaimAsync(world.InvoiceId, [QualityClaimQaKit.Jpeg, QualityClaimQaKit.Png]);
        var urls = claim.GetProperty("photos").EnumerateArray().Select(p => p.Text("url")!).ToList();
        var anonymous = api.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync(urls[0])).StatusCode);
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(new Uri("http://x"), urls[0]).Query);
        var expires = DateTimeOffset.FromUnixTimeSeconds(long.Parse(query["expires"]!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.InRange(expires, DateTimeOffset.UtcNow.AddMinutes(59), DateTimeOffset.UtcNow.AddMinutes(61));

        var sig = query["sig"]!;
        var tampered = urls[0].Replace($"sig={Uri.EscapeDataString(sig)}", "sig=AAAA", StringComparison.Ordinal);
        Assert.NotEqual(urls[0], tampered);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(tampered)).StatusCode);

        var otherPath = new Uri(new Uri("http://x"), urls[1]).AbsolutePath;
        var crossed = otherPath + new Uri(new Uri("http://x"), urls[0]).Query;
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(crossed)).StatusCode);
    }

    [Fact]
    public async Task A_claim_photo_is_never_a_public_link_of_a_trader_who_did_not_send_it()
    {
        using var api = new QualityClaimApiFactory();
        var c = await api.QaClaimedAsync();
        var other = await api.TraderAsync();

        var body = await (await other.Client.GetAsync("/api/me/quality-claims")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("claim-photos", body, StringComparison.Ordinal);
        var foreign = await other.Client.GetAsync($"/api/me/quality-claims/{c.Id}");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.DoesNotContain("claim-photos", await foreign.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
