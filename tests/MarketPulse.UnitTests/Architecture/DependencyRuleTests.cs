using System.Reflection;
using MarketPulse.Domain.Entities;

namespace MarketPulse.UnitTests.Architecture;

public class DependencyRuleTests
{
    [Fact]
    public void Domain_references_nothing_but_the_base_class_library()
    {
        var domain = typeof(Watchlist).Assembly;

        var forbidden = domain.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name =>
                !name.StartsWith("System", StringComparison.Ordinal) &&
                !name.Equals("netstandard", StringComparison.Ordinal) &&
                !name.Equals("mscorlib", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(forbidden);
    }
}
