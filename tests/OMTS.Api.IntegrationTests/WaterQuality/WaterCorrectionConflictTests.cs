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
    public async Task A_correction_that_loses_to_another_one_is_applied_on_it_and_keeps_nothing_of_its_first_attempt()
    {
        var (api, saves) = NewApi();
        await using var _ = api;
        var z = await api.ZoneAsync();
        var (_, manager) = await api.StaffAsync(z.Zone);
        var id = (await z.RecordedAsync(7m, 20m)).Id();
        saves.LoseNext(() => api.UpdateAsync<WaterMeasurement>(id, m => m.Salinity = 21m));

        var response = await z.Owner.CorrectAsync(id, 9m, 22m, raftId: z.Raft.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await api.QueryAsync(db => db.WaterMeasurements.AsNoTracking().SingleAsync(m => m.Id == id));
        Assert.Equal((9m, 22m, "pH 9.00 > 8.50"), (stored.Ph, stored.Salinity, stored.ViolatedValues));
        var audit = Assert.Single(await api.AuditOfAsync(id), e => e.Action == "WaterMeasurement.Corrected");
        Assert.Contains("\"Salinity\":21", audit.Before);
        Assert.Single(await api.AlertsOfAsync(id), n => n.RecipientAccountId == manager.Id);
        Assert.Single(api.Services.GetRequiredService<FakePushSender>().Inbox.List(manager.Id.ToString()), m => m.Text.Contains("corrected measurement"));
    }

    [Fact]
    public async Task The_notice_follows_the_record_as_the_winner_left_it()
    {
        var (api, saves) = NewApi();
        await using var _ = api;
        var z = await api.ZoneAsync();
        var (_, manager) = await api.StaffAsync(z.Zone);
        var id = (await z.RecordedAsync(9m, 20m)).Id();
        // The winner already brought the value into range: this correction changes nothing about what is outside.
        saves.LoseNext(() => api.UpdateAsync<WaterMeasurement>(id, m =>
        {
            m.Ph = 7.5m;
            m.ThresholdExceeded = false;
            m.ViolatedValues = null;
        }));

        var response = await z.Owner.CorrectAsync(id, 7m, 20m, raftId: z.Raft.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await api.WithdrawalsOfAsync(id));
        Assert.Contains(await api.AuditOfAsync(id), e => e.Action == "WaterMeasurement.Corrected" && e.Before!.Contains("\"Ph\":7.5"));
        Assert.Single(await api.AlertsOfAsync(id), n => n.RecipientAccountId == manager.Id);
    }

    [Fact]
    public async Task A_correction_that_loses_to_a_void_is_refused_and_does_not_bring_the_measurement_back()
    {
        var (api, saves) = NewApi();
        await using var _ = api;
        var z = await api.ZoneAsync();
        var id = (await z.RecordedAsync(7m, 20m)).Id();
        saves.LoseNext(() => api.UpdateAsync<WaterMeasurement>(id, m =>
        {
            m.StatusId = StatusIds.WaterMeasurement.Voided;
            m.VoidReason = "Đo nhầm";
        }));

        var response = await z.Owner.CorrectAsync(id, 7.2m, 20m, raftId: z.Raft.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("MSG-GEN-09", await response.ErrorCodeAsync());
        var stored = await api.QueryAsync(db => db.WaterMeasurements.AsNoTracking().SingleAsync(m => m.Id == id));
        Assert.Equal((StatusIds.WaterMeasurement.Voided, 7m), (stored.StatusId, stored.Ph));
        Assert.DoesNotContain(await api.AuditOfAsync(id), e => e.Action == "WaterMeasurement.Corrected");
    }

    [Fact]
    public async Task A_void_that_loses_to_a_correction_voids_the_corrected_values_and_withdraws_once()
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

        var response = await z.Owner.CorrectAsync(id, 7.2m, 20m, raftId: z.Raft.Id);

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
            services.AddScoped<IUnitOfWork>(provider => new LosingUnitOfWork(provider.GetRequiredService<PushOutbox>(), provider.GetRequiredService<UnitOfWork>(), saves));
        });
        return (api, saves);
    }

    /// <summary>The next correction or void's save happens after <c>elsewhere</c> and loses, as on a row version conflict.</summary>
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
    /// A lost save ends as a real one on SQL Server does: nothing written, the push messages dropped by the failed save.
    /// The in-memory database cannot show that by itself, as it writes the rows of a failing save one by one.
    /// </summary>
    private sealed class LosingUnitOfWork(PushOutbox outbox, IUnitOfWork inner, LostSaves saves) : IUnitOfWork
    {
        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (await saves.TakeLostSaveAsync())
            {
                outbox.Discard();
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
