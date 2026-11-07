using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Constants;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Entities.Sales;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>
/// QA of PR #58, F2 (BR-SALE-08): a sale that took its HD (and PT) code and then ends without its invoice leaves one audit row naming the unused codes and why, so every gap in the
/// codes is explained. The lost race is played by a unit of work that fails the sale's save like a parallel save that won, after running what that parallel save did.
/// </summary>
public class SalesInvoicesQaTestsUnusedCodes
{
    private const string UnusedAction = "SalesInvoice.CodeUnused";

    // F2: the retry after a lost race finds the lot Closed meanwhile (MSG-GEN-09); the codes the first attempt took are explained in the audit.
    [Fact]
    public async Task F2_a_sale_refused_on_its_retry_leaves_an_audit_row_naming_its_unused_invoice_and_payment_codes()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var races = new LostRaces
        {
            Failures = 1,
            Meanwhile = () => api.UpdateAsync<HarvestLot>(sale.LotId, l => (l.StatusId, l.CloseDate, l.LeftoverKg) = (StatusIds.HarvestLot.Closed, FarmingTestKit.Today, 0m)),
        };
        var (host, client) = Losing(api, sale.Manager, races);
        await using var _ = host;

        var answer = await client.PostInvoiceAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 10m)], Cash(300_000m), sale.LotId));

        Assert.Equal((HttpStatusCode.Conflict, "MSG-GEN-09"), (answer.StatusCode, await answer.ErrorCodeAsync()));
        var (invoiceCode, paymentCode) = await LastCodesAsync(api);
        var row = Assert.Single(await UnusedRowsAsync(api));
        Assert.Equal((invoiceCode, sale.ZoneId, sale.Manager.Account.Id), (row.RecordCode, row.ZoneId, row.ActorAccountId));
        Assert.Contains(invoiceCode, row.After!);
        Assert.Contains(paymentCode, row.After!);
        Assert.Contains("MSG-GEN-09", row.Reason!);
        Assert.Equal(0, await api.QueryAsync(db => db.SalesInvoices.CountAsync()));
    }

    // F2: every attempt loses (MSG06 after the last); the code taken once is explained once.
    [Fact]
    public async Task F2_a_sale_that_loses_every_attempt_answers_msg06_and_leaves_one_audit_row_naming_its_code()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var (host, client) = Losing(api, sale.Manager, new LostRaces { Failures = int.MaxValue });
        await using var _ = host;

        var answer = await client.PostInvoiceAsync(Invoice(null, [Line(sale.ProductId, sale.LotId, 10m)], Cash(300_000m), sale.LotId));

        Assert.Equal((HttpStatusCode.Conflict, "MSG06"), (answer.StatusCode, await answer.ErrorCodeAsync()));
        var (invoiceCode, paymentCode) = await LastCodesAsync(api);
        Assert.Equal(SalesDocumentCodes.InvoiceCode(1), invoiceCode);
        var row = Assert.Single(await UnusedRowsAsync(api));
        Assert.Equal(invoiceCode, row.RecordCode);
        Assert.Contains(paymentCode, row.After!);
        Assert.Contains("MSG06", row.Reason!);
        Assert.Equal(0, await api.QueryAsync(db => db.SalesInvoices.CountAsync()));
    }

    // F2: a second tap saved its invoice while the first was being saved; the first answers that invoice, and the code it took is explained.
    [Fact]
    public async Task F2_a_sale_that_loses_to_a_save_of_its_own_deduplication_key_answers_that_invoice_and_explains_its_own_code()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var body = Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 10m)], Cash(300_000m), sale.LotId).With("deduplicationKey", "tap-twice");
        var races = new LostRaces { Failures = 1, Meanwhile = async () => (await sale.Manager.Client.PostInvoiceAsync(body)).EnsureSuccessStatusCode() };
        var (host, client) = Losing(api, sale.Manager, races);
        await using var _ = host;

        var saved = (await client.SellAsync(body)).GetProperty("invoice");

        var stored = await api.QueryAsync(db => db.SalesInvoices.AsNoTracking().SingleAsync());
        Assert.Equal((stored.Id, stored.Code), (saved.Id(), saved.Text("code")));
        var row = Assert.Single(await UnusedRowsAsync(api));
        Assert.Equal(SalesDocumentCodes.InvoiceCode(1), row.RecordCode);
        Assert.NotEqual(stored.Code, row.RecordCode);
        Assert.Contains("deduplication key", row.Reason!);
    }

    private static Task<List<AuditEvent>> UnusedRowsAsync(AuthApiFactory api) =>
        api.QueryAsync(db => db.AuditEvents.AsNoTracking().Where(e => e.Action == UnusedAction).ToListAsync());

    /// <summary>The last HD and PT codes given: with no other sale in the test, the ones the lost sale took.</summary>
    private static async Task<(string Invoice, string Payment)> LastCodesAsync(AuthApiFactory api)
    {
        var last = await api.QueryAsync(db => db.DocumentSequences.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.LastValue));
        return (SalesDocumentCodes.InvoiceCode(last[DocumentSequenceKeys.SalesInvoice]), SalesDocumentCodes.PaymentCode(last[DocumentSequenceKeys.CustomerPayment]));
    }

    /// <summary>A host of the same database whose sale saves lose; the client is the seller's, logged in on <paramref name="api"/>.</summary>
    private static (WebApplicationFactory<Program> Host, HttpClient Client) Losing(AuthApiFactory api, Actor seller, LostRaces races)
    {
        var host = api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddScoped<UnitOfWork>();
            services.AddScoped<IUnitOfWork>(provider => new LosingUnitOfWork(provider.GetRequiredService<UnitOfWork>(), provider.GetRequiredService<OmtsDbContext>(), races));
        }));
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = seller.Client.DefaultRequestHeaders.Authorization;
        return (host, client);
    }

    /// <summary>How many sale saves lose, and what the parallel save that wins does before each loss.</summary>
    private sealed class LostRaces
    {
        public int Failures { get; set; }
        public Func<Task>? Meanwhile { get; init; }
    }

    /// <summary>Fails the saves that hold a new invoice while <see cref="LostRaces.Failures"/> lasts; every other save (the audit row of the unused codes) goes through.</summary>
    private sealed class LosingUnitOfWork(IUnitOfWork inner, OmtsDbContext db, LostRaces races) : IUnitOfWork
    {
        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (races.Failures > 0 && db.ChangeTracker.Entries<SalesInvoice>().Any(e => e.State == EntityState.Added))
            {
                races.Failures--;
                if (races.Meanwhile is { } meanwhile)
                {
                    await meanwhile();
                }

                throw new ConcurrencyConflictException(new InvalidOperationException("Test: a parallel save won."));
            }

            return await inner.SaveChangesAsync(cancellationToken);
        }

        public Task ReloadAsync(object entity, CancellationToken cancellationToken = default) => inner.ReloadAsync(entity, cancellationToken);

        public void ExpectRowVersion(AuditableEntity entity, byte[] rowVersion) => inner.ExpectRowVersion(entity, rowVersion);

        public void MarkChanged(AuditableEntity entity) => inner.MarkChanged(entity);

        public void DiscardChanges() => inner.DiscardChanges();
    }
}
