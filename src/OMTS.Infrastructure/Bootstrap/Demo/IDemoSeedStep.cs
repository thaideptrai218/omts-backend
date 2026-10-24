namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>
/// One idempotent part of the Development demo data (zones, licenses, receipts…). A later module (water
/// measurements, workers, payroll) adds its own step to <see cref="DemoDataSeeder"/>; steps run in order
/// and each one finds its rows by business code, so a second run creates nothing.
/// </summary>
internal interface IDemoSeedStep
{
    /// <summary>Shown in the command output, e.g. "zones".</summary>
    string Name { get; }

    /// <summary>Creates what is missing; returns how many records were created.</summary>
    Task<int> RunAsync(DemoSeedContext context, CancellationToken cancellationToken);
}
