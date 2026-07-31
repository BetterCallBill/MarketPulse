using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MarketPulse.Infrastructure.Persistence;

/// <summary>Used only by `dotnet ef` at design time. Never resolved at runtime.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<MarketPulseDbContext>
{
    public MarketPulseDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<MarketPulseDbContext>()
            .UseSqlServer("Server=localhost,1433;Database=MarketPulse;User Id=sa;" +
                          "Password=Local!Dev!Pass123;TrustServerCertificate=True")
            .Options;

        return new MarketPulseDbContext(options);
    }
}
