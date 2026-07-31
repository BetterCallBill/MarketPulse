using MarketPulse.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MarketPulse.Infrastructure.Persistence;

public sealed class MarketPulseDbContext(DbContextOptions<MarketPulseDbContext> options)
    : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Ticker> Tickers => Set<Ticker>();
    public DbSet<Watchlist> Watchlists => Set<Watchlist>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Email).HasMaxLength(256).IsRequired();
            e.HasIndex(x => x.Email).IsUnique();
        });

        b.Entity<Ticker>(e =>
        {
            e.HasKey(x => x.Code);
            e.Property(x => x.Code).HasMaxLength(8);
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
            e.Property(x => x.SeedPrice).HasPrecision(18, 4);
            e.HasData(SeedData.ReferenceTickers);
        });

        b.Entity<Watchlist>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.UserId).IsUnique();
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // `Items` is a computed property over the `_items` backing field, not itself a
            // mapped navigation. Without this, EF's convention discovers `Items` as a second,
            // ambiguous navigation candidate alongside the field-mapped one below and throws
            // at model-build time.
            e.Ignore(x => x.Items);

            e.OwnsMany<WatchlistItem>("_items", items =>
            {
                items.ToTable("WatchlistItems");
                items.WithOwner().HasForeignKey(x => x.WatchlistId);
                items.HasKey(x => x.Id);
                items.Property(x => x.Ticker).HasMaxLength(8).IsRequired();
                items.HasIndex(x => new { x.WatchlistId, x.Ticker }).IsUnique();
            });
        });
    }
}
