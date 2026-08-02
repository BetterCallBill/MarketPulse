using MarketPulse.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MarketPulse.Infrastructure.Persistence;

public sealed class MarketPulseDbContext(DbContextOptions<MarketPulseDbContext> options)
    : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Ticker> Tickers => Set<Ticker>();
    public DbSet<Watchlist> Watchlists => Set<Watchlist>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Email).HasMaxLength(256).IsRequired();
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.PasswordHash).HasMaxLength(256).IsRequired();
            e.Property(x => x.CreatedUtc).IsRequired();
            e.Property(x => x.FailedLoginCount).IsRequired();
            e.Property(x => x.LockoutEndUtc);
            e.HasData(new User(
                SeedData.DevUserId,
                SeedData.DevUserEmail,
                SeedData.DevUserPasswordHash,
                SeedData.DevUserCreatedUtc));
        });

        b.Entity<RefreshToken>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();

            // Every refresh looks a token up by hash — without this index that is a
            // table scan on the hottest authenticated path in the app.
            e.HasIndex(x => x.TokenHash).IsUnique();

            // Reuse detection revokes the whole family, which queries by user.
            e.HasIndex(x => x.UserId);

            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
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

            // Seed the dev user's watchlist itself. `HasData` cannot call `Watchlist.Create`
            // (private constructor, and `Create` mints a random Id) so an anonymous type
            // supplying just the mapped key/FK properties is used instead — EF only reads
            // property values by name to build the migration's InsertData, it never
            // constructs a real `Watchlist` instance from this.
            e.HasData(new { Id = SeedData.DevWatchlistId, UserId = SeedData.DevUserId });

            e.OwnsMany<WatchlistItem>("_items", items =>
            {
                items.ToTable("WatchlistItems");
                items.WithOwner().HasForeignKey(x => x.WatchlistId);
                items.HasKey(x => x.Id);

                // The Domain mints this id itself, exactly as `User`, `Watchlist` and
                // `RefreshToken` do. EF's convention for a Guid key is otherwise
                // "generated on add", and it uses "is the key already set?" to classify a
                // dependent it discovers on a *loaded* principal: a set key reads as "this
                // row exists", so it emits an UPDATE that matches no row and throws
                // DbUpdateConcurrencyException instead of inserting. Telling EF the id is
                // never store-generated makes it classify the new item as Added.
                // Schema-neutral — this changes classification, not the column.
                items.Property(x => x.Id).ValueGeneratedNever();

                items.Property(x => x.Ticker).HasMaxLength(8).IsRequired();
                items.HasIndex(x => new { x.WatchlistId, x.Ticker }).IsUnique();

                // Same reasoning as above: `WatchlistItem`'s real constructor is `internal`
                // to the Domain assembly, so seeding uses anonymous types carrying the
                // owned entity's key, its owner FK, and its data — fixed GUIDs and a fixed
                // timestamp so the migration is deterministic across regenerations.
                items.HasData(SeedData.DefaultWatchlist.Select((ticker, i) => new
                {
                    Id = SeedData.DefaultWatchlistItemIds[i],
                    WatchlistId = SeedData.DevWatchlistId,
                    Ticker = ticker,
                    AddedUtc = SeedData.DevWatchlistItemsAddedUtc
                }));
            });
        });
    }
}
