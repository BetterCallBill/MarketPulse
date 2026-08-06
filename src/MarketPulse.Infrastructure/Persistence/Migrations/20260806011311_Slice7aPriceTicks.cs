using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarketPulse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Slice7aPriceTicks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PriceTicks",
                columns: table => new
                {
                    Ticker = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    TimestampUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Price = table.Column<decimal>(type: "decimal(18,4)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceTicks", x => new { x.Ticker, x.TimestampUtc })
                        .Annotation("SqlServer:Clustered", true);
                });

            // EF cannot express IGNORE_DUP_KEY; rebuild the PK with it. A replayed
            // (Ticker, TimestampUtc) — Yahoo re-serving an observation across polls — then becomes
            // a no-op at the database instead of an exception poisoning a whole flush batch.
            migrationBuilder.Sql("""
                ALTER TABLE [PriceTicks] DROP CONSTRAINT [PK_PriceTicks];
                ALTER TABLE [PriceTicks] ADD CONSTRAINT [PK_PriceTicks]
                    PRIMARY KEY CLUSTERED ([Ticker], [TimestampUtc]) WITH (IGNORE_DUP_KEY = ON);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PriceTicks");
        }
    }
}
