using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace MarketPulse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Tickers",
                columns: table => new
                {
                    Code = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SeedPrice = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tickers", x => x.Code);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Watchlists",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Watchlists", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Watchlists_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WatchlistItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WatchlistId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ticker = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    AddedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WatchlistItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WatchlistItems_Watchlists_WatchlistId",
                        column: x => x.WatchlistId,
                        principalTable: "Watchlists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "Tickers",
                columns: new[] { "Code", "Name", "SeedPrice" },
                values: new object[,]
                {
                    { "A200", "BetaShares Australia 200 ETF", 142.60m },
                    { "ACDC", "Global X Battery Tech & Lithium ETF", 72.15m },
                    { "ASIA", "BetaShares Asia Technology Tigers ETF", 11.85m },
                    { "ETHI", "BetaShares Global Sustainability", 14.55m },
                    { "FANG", "Global X FANG+ ETF", 26.40m },
                    { "GEAR", "BetaShares Geared Australian Equity", 32.40m },
                    { "HACK", "BetaShares Global Cybersecurity ETF", 12.30m },
                    { "IHVV", "iShares S&P 500 AUD Hedged ETF", 46.55m },
                    { "IOO", "iShares Global 100 ETF", 140.25m },
                    { "IOZ", "iShares Core S&P/ASX 200 ETF", 34.85m },
                    { "IVV", "iShares S&P 500 ETF", 62.10m },
                    { "MOAT", "VanEck Morningstar Wide Moat ETF", 118.90m },
                    { "NDQ", "BetaShares NASDAQ 100 ETF", 54.30m },
                    { "QUAL", "VanEck MSCI Intl Quality ETF", 48.70m },
                    { "RBTZ", "Global X ROBO Global Robotics ETF", 21.05m },
                    { "SLF", "SPDR S&P/ASX 200 Listed Property", 13.75m },
                    { "STW", "SPDR S&P/ASX 200 Fund", 74.90m },
                    { "SYI", "SPDR MSCI Australia Select High Div", 29.60m },
                    { "VAF", "Vanguard Australian Fixed Interest", 45.15m },
                    { "VAP", "Vanguard Australian Property Securities", 88.40m },
                    { "VAS", "Vanguard Australian Shares Index", 98.20m },
                    { "VEU", "Vanguard All-World ex-US Shares", 94.20m },
                    { "VGS", "Vanguard MSCI Intl Shares Index", 125.90m },
                    { "VHY", "Vanguard Australian Shares High Yield", 68.75m },
                    { "VTS", "Vanguard US Total Market Shares", 410.30m }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                table: "Users",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WatchlistItems_WatchlistId_Ticker",
                table: "WatchlistItems",
                columns: new[] { "WatchlistId", "Ticker" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Watchlists_UserId",
                table: "Watchlists",
                column: "UserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Tickers");

            migrationBuilder.DropTable(
                name: "WatchlistItems");

            migrationBuilder.DropTable(
                name: "Watchlists");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
