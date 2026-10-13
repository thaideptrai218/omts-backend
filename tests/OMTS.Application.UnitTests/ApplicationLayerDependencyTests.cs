namespace OMTS.Application.UnitTests;

public class ApplicationLayerDependencyTests
{
    // Use cases depend only on the domain and on abstractions; concrete adapters
    // (database, National Traceability Portal) live in Infrastructure.
    [Theory]
    [InlineData("OMTS.Infrastructure")]
    [InlineData("OMTS.Api")]
    public void Application_does_not_reference_outer_layer(string outerLayer)
    {
        var referenced = typeof(DependencyInjection).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name);

        Assert.DoesNotContain(outerLayer, referenced);
    }
}
