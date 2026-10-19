namespace OMTS.Application.UnitTests;

public class ApplicationLayerDependencyTests
{
    // Use cases depend only on the domain and on abstractions; concrete adapters
    // (database, National Traceability Portal) live in Infrastructure. Data access goes through
    // repositories and the unit of work, so the ORM never leaks into use cases.
    [Theory]
    [InlineData("OMTS.Infrastructure")]
    [InlineData("OMTS.Api")]
    [InlineData("Microsoft.EntityFrameworkCore")]
    public void Application_does_not_reference_outer_layer(string outerLayer)
    {
        var referenced = typeof(DependencyInjection).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name);

        Assert.DoesNotContain(outerLayer, referenced);
    }
}
