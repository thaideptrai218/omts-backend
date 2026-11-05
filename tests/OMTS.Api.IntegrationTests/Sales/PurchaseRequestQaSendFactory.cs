using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Models;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>Fails one save of a request on purpose, by its number within that request.</summary>
public sealed class QaSaveGate
{
    public int FailSaveNumber { get; set; }
}

internal sealed class QaGatedUnitOfWork(IUnitOfWork inner, QaSaveGate gate) : IUnitOfWork
{
    private int _saves;

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        ++_saves == gate.FailSaveNumber ? throw new InvalidOperationException("The database went away.") : inner.SaveChangesAsync(cancellationToken);

    public Task ReloadAsync(object entity, CancellationToken cancellationToken = default) => inner.ReloadAsync(entity, cancellationToken);

    public void DiscardChanges() => inner.DiscardChanges();

    public void ExpectRowVersion(AuditableEntity entity, byte[] rowVersion) => inner.ExpectRowVersion(entity, rowVersion);

    public void MarkChanged(AuditableEntity entity) => inner.MarkChanged(entity);
}

/// <summary>The host with a sender a test sets, and a save that can be made to fail.</summary>
public sealed class PurchaseRequestQaSendFactory : AuthApiFactory
{
    public StubTraderMessageSender Sender { get; } = new();

    public QaSaveGate Gate { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ITraderMessageSender>(Sender);
            services.AddSingleton(Gate);
            var original = services.Last(d => d.ServiceType == typeof(IUnitOfWork));
            services.Remove(original);
            services.AddScoped<IUnitOfWork>(provider => new QaGatedUnitOfWork((IUnitOfWork)ActivatorUtilities.CreateInstance(provider, original.ImplementationType!), provider.GetRequiredService<QaSaveGate>()));
        });
    }
}
