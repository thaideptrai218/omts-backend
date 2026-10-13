using OMTS.Domain.Common;

namespace OMTS.Domain.UnitTests;

public class DomainLayerDependencyTests
{
    // Clean Architecture rule: the domain model must stay independent of every outer layer,
    // otherwise business rules become coupled to persistence or HTTP concerns.
    [Fact]
    public void Domain_does_not_reference_other_OMTS_layers()
    {
        var referenced = typeof(Entity).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(name => name is not null && name.StartsWith("OMTS.", StringComparison.Ordinal));

        Assert.Empty(referenced);
    }

    // Offline-created records must already carry a unique identity before reaching the server.
    [Fact]
    public void New_entities_get_distinct_non_empty_ids()
    {
        var first = new TestEntity();
        var second = new TestEntity();

        Assert.NotEqual(Guid.Empty, first.Id);
        Assert.NotEqual(first.Id, second.Id);
    }

    private sealed class TestEntity : Entity;
}
