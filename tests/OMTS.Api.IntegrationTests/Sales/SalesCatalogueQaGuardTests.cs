using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Common;
using OMTS.Domain.Entities.Sales;
using OMTS.Infrastructure;
using OMTS.Infrastructure.Persistence;
using Xunit.Sdk;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>
/// QA of OMTS-223: does the "every foreign key is listed" guard really fail? It runs the branch's own test against a host
/// whose model has one more table that names a customer profile, as a new PR would add it.
/// </summary>
public class SalesCatalogueQaGuardTests
{
    public sealed class ProbeRowWithForeignKey : Entity
    {
        public Guid CustomerId { get; set; }
    }

    public sealed class ProbeRowWithNoForeignKey : Entity
    {
        public Guid CustomerId { get; set; }
    }

    private sealed class WithForeignKey(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies)
    {
        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            base.Customize(modelBuilder, context);
            modelBuilder.Entity<ProbeRowWithForeignKey>().HasOne<Customer>().WithMany().HasForeignKey(r => r.CustomerId);
        }
    }

    private sealed class WithNoForeignKey(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies)
    {
        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            base.Customize(modelBuilder, context);
            modelBuilder.Entity<ProbeRowWithNoForeignKey>();
        }
    }

    private sealed class ProbeFactory<TCustomizer> : AuthApiFactory
        where TCustomizer : class, IModelCustomizer
    {
        private readonly string _database = $"omts-probe-{Guid.NewGuid()}";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<OmtsDbContext>>();
                services.AddDbContext<OmtsDbContext>((provider, options) => options
                    .UseInMemoryDatabase(_database)
                    .AddOmtsInterceptors(provider)
                    .ReplaceService<IModelCustomizer, TCustomizer>());
            });
        }
    }

    [Fact]
    public async Task The_guard_fails_when_a_new_table_with_a_foreign_key_to_a_customer_is_not_listed()
    {
        await using var factory = new ProbeFactory<WithForeignKey>();

        await Assert.ThrowsAnyAsync<XunitException>(() => new CustomerMergeTests(factory).Every_foreign_key_to_a_customer_profile_is_listed_for_the_merge());
    }

    // Its limit, as an observation: a customer id with no foreign key (a polymorphic related record, say) is not seen.
    [Fact]
    public async Task The_guard_does_not_see_a_customer_id_with_no_foreign_key()
    {
        await using var factory = new ProbeFactory<WithNoForeignKey>();

        await new CustomerMergeTests(factory).Every_foreign_key_to_a_customer_profile_is_listed_for_the_merge();
    }
}
