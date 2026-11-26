using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Infrastructure.Messaging;
using OMTS.Infrastructure.Messaging.Fakes;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// NFR-09 and NFR-13: water has no version check. A save that loses to a parallel one runs again from the record as it now
/// is, without keeping anything of the first attempt. <see cref="LostSaves"/> plays the race in a fixed order; the real
/// parallel requests on SQL Server are in <see cref="WaterCorrectionSqlServerTests"/>.
/// </summary>
public class WaterCorrectionConflictTests
{
    [Fact]
    public async Task A_void_that_loses_to_another_change_voids_the_values_it_left_and_withdraws_once()
    {
        var (api, saves) = NewApi();
        await using var _ = api;
        var z = await api.ZoneAsync();
        var (_, manager) = await api.StaffAsync(z.Zone);
        var id = (await z.RecordedAsync(9m, 20m)).Id();
        saves.LoseNext(() => api.UpdateAsync<WaterMeasurement>(id, m => m.Ph = 9.3m));

        var response = await z.Owner.VoidAsync(id, reason: "Đo nhầm");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("Voided", 9.3m), (body.Text("status"), body.GetProperty("ph").GetDecimal()));
        Assert.Single(await api.WithdrawalsOfAsync(id), n => n.RecipientAccountId == manager.Id);
        Assert.Single(await api.AuditOfAsync(id), e => e.Action == "WaterMeasurement.Voided");
    }

    [Fact]
    public async Task Saves_that_keep_losing_stop_with_MSG06_and_save_nothing()
    {
        var (api, saves) = NewApi();
        await using var _ = api;
        var z = await api.ZoneAsync();
        var id = (await z.RecordedAsync(7m, 20m)).Id();
        saves.LoseAlways();

        var response = await z.Owner.VoidAsync(id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("MSG06", await response.ErrorCodeAsync());
        Assert.Equal(20, saves.Lost);
    }

    private static (WaterApiFactory Api, LostSaves Saves) NewApi()
    {
        var saves = new LostSaves();
        var api = new WaterApiFactory(services: services =>
        {
            services.AddScoped<UnitOfWork>();
            services.AddScoped<IUnitOfWork>(provider => new LosingUnitOfWork(provider.GetRequiredService<UnitOfWork>(), saves));
        });
        return (api, saves);
    }

    /// <summary>The next void's save happens after <c>elsewhere</c> and loses, as on a row version conflict.</summary>
    private sealed class LostSaves
    {
        private Func<Task>? _elsewhere;
        private bool _always;

        public int Lost { get; private set; }

        public void LoseNext(Func<Task> elsewhere) => _elsewhere = elsewhere;

        public void LoseAlways() => _always = true;

        /// <summary>Whether the save now about to run has to lose.</summary>
        public async Task<bool> TakeLostSaveAsync()
        {
            if (_elsewhere is null && !_always)
            {
                return false;
            }

            var elsewhere = _elsewhere;
            _elsewhere = null;
            if (elsewhere is not null)
            {
                await elsewhere();
            }

            Lost++;
            return true;
        }
    }

    /// <summary>
    /// A lost save ends as a real one on SQL Server does: nothing written, and the push messages stay queued until the use
    /// case discards its changes. The in-memory database cannot show that by itself, as it writes the rows of a failing save one by one.
    /// </summary>
    private sealed class LosingUnitOfWork(IUnitOfWork inner, LostSaves saves) : IUnitOfWork
    {
        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (await saves.TakeLostSaveAsync())
            {
                throw new ConcurrencyConflictException(new DbUpdateConcurrencyException("Test: the row version changed."));
            }

            return await inner.SaveChangesAsync(cancellationToken);
        }

        public Task ReloadAsync(object entity, CancellationToken cancellationToken = default) => inner.ReloadAsync(entity, cancellationToken);

        public void DiscardChanges() => inner.DiscardChanges();

        public void ExpectRowVersion(AuditableEntity entity, byte[] rowVersion) => inner.ExpectRowVersion(entity, rowVersion);

        public void MarkChanged(AuditableEntity entity) => inner.MarkChanged(entity);
    }
}
