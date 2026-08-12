using System.Reflection;
using MarketPulse.Application.Watchlists;
using MarketPulse.Domain.Entities;
using MarketPulse.Infrastructure.Persistence;

namespace MarketPulse.UnitTests.Architecture;

public class DependencyRuleTests
{
    /// <summary>
    /// Frameworks the Application layer must stay ignorant of: the web host it is called
    /// from, the ORM and driver that persist it, and the broker that will carry alert
    /// events once evaluation is extracted (ADR-001). Each belongs behind an interface the
    /// Application layer owns and Infrastructure implements.
    /// </summary>
    private static readonly string[] ForbiddenFrameworks =
    [
        "Microsoft.AspNetCore",
        "Microsoft.EntityFrameworkCore",
        "Microsoft.Data",
        "Microsoft.Extensions.Identity",
        "RabbitMQ"
    ];

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

    /// <summary>
    /// The Domain rule above only guards the innermost layer. Application is allowed
    /// third-party libraries (MediatR, FluentValidation, Options) but not the frameworks
    /// that would tie a use case to a transport, a database, or a broker.
    /// </summary>
    [Fact]
    public void Application_references_no_web_persistence_or_messaging_framework()
    {
        var application = typeof(AddWatchlistItemCommand).Assembly;

        Assert.Empty(ForbiddenReferencesOf(application));
    }

    /// <summary>
    /// Control for the test above. A typo in <see cref="ForbiddenFrameworks"/> — or a
    /// future change to how references are resolved — would make the Application assertion
    /// pass vacuously while checking nothing. Infrastructure is where EF Core legitimately
    /// lives, so the detector must flag it.
    /// </summary>
    [Fact]
    public void The_forbidden_framework_detector_flags_the_infrastructure_layer()
    {
        var infrastructure = typeof(MarketPulseDbContext).Assembly;

        Assert.NotEmpty(ForbiddenReferencesOf(infrastructure));
    }

    private static string[] ForbiddenReferencesOf(Assembly assembly) =>
        assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => ForbiddenFrameworks.Any(
                f => name.StartsWith(f, StringComparison.Ordinal)))
            .ToArray();

    /// <summary>
    /// The worker is a second deployable, not a second web host. It shares the database and
    /// the domain with the API and reaches the outside world only through the broker; a web
    /// framework reference would mean someone had started building an HTTP surface on it.
    /// </summary>
    [Fact]
    public void The_alerts_worker_references_no_web_framework()
    {
        var worker = typeof(MarketPulse.Alerts.AlertEvaluator).Assembly;

        var forbidden = worker.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(forbidden);
    }
}
