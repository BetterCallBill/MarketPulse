using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MarketPulse.Infrastructure.Persistence;

/// <summary>Used only by `dotnet ef` at design time. Never resolved at runtime.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<MarketPulseDbContext>
{
    public MarketPulseDbContext CreateDbContext(string[] args)
    {
        // Read from the environment so the SA password lives in one place (docker-compose.yml
        // and test fixtures) rather than a second copy baked into this compiled assembly.
        // The literal below is the same local-only dev credential and is kept only as a
        // fallback so `dotnet ef` still works with zero setup.
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__MarketPulse")
            ?? "Server=localhost,1433;Database=MarketPulse;User Id=sa;" +
               "Password=Local!Dev!Pass123;TrustServerCertificate=True";

        var options = new DbContextOptionsBuilder<MarketPulseDbContext>()
            .UseSqlServer(connectionString)
            .Options;

        return new MarketPulseDbContext(options);
    }
}
