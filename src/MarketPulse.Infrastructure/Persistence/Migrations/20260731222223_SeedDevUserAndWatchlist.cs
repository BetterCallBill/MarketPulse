using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace MarketPulse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SeedDevUserAndWatchlist : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "Users",
                columns: new[] { "Id", "Email" },
                values: new object[] { new Guid("11111111-1111-1111-1111-111111111111"), "dev@marketpulse.local" });

            migrationBuilder.InsertData(
                table: "Watchlists",
                columns: new[] { "Id", "UserId" },
                values: new object[] { new Guid("22222222-2222-2222-2222-222222222222"), new Guid("11111111-1111-1111-1111-111111111111") });

            migrationBuilder.InsertData(
                table: "WatchlistItems",
                columns: new[] { "Id", "AddedUtc", "Ticker", "WatchlistId" },
                values: new object[,]
                {
                    { new Guid("33333333-3333-3333-3333-333333333001"), new DateTimeOffset(new DateTime(2026, 7, 31, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "IVV", new Guid("22222222-2222-2222-2222-222222222222") },
                    { new Guid("33333333-3333-3333-3333-333333333002"), new DateTimeOffset(new DateTime(2026, 7, 31, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "NDQ", new Guid("22222222-2222-2222-2222-222222222222") },
                    { new Guid("33333333-3333-3333-3333-333333333003"), new DateTimeOffset(new DateTime(2026, 7, 31, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "VHY", new Guid("22222222-2222-2222-2222-222222222222") },
                    { new Guid("33333333-3333-3333-3333-333333333004"), new DateTimeOffset(new DateTime(2026, 7, 31, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "FANG", new Guid("22222222-2222-2222-2222-222222222222") }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: new Guid("33333333-3333-3333-3333-333333333001"));

            migrationBuilder.DeleteData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: new Guid("33333333-3333-3333-3333-333333333002"));

            migrationBuilder.DeleteData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: new Guid("33333333-3333-3333-3333-333333333003"));

            migrationBuilder.DeleteData(
                table: "WatchlistItems",
                keyColumn: "Id",
                keyValue: new Guid("33333333-3333-3333-3333-333333333004"));

            migrationBuilder.DeleteData(
                table: "Watchlists",
                keyColumn: "Id",
                keyValue: new Guid("22222222-2222-2222-2222-222222222222"));

            migrationBuilder.DeleteData(
                table: "Users",
                keyColumn: "Id",
                keyValue: new Guid("11111111-1111-1111-1111-111111111111"));
        }
    }
}
