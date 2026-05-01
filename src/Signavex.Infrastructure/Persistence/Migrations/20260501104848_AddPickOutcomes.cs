using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Signavex.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPickOutcomes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PickOutcomes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ScanDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Ticker = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    RawScore = table.Column<double>(type: "float", nullable: false),
                    FinalScore = table.Column<double>(type: "float", nullable: false),
                    EntryDate = table.Column<DateOnly>(type: "date", nullable: true),
                    EntryPrice = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    SpyEntryPrice = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    EntrySkippedReason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Price30d = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    TickerReturn30d = table.Column<double>(type: "float", nullable: true),
                    SpyReturn30d = table.Column<double>(type: "float", nullable: true),
                    Outperformance30d = table.Column<double>(type: "float", nullable: true),
                    Price90d = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    TickerReturn90d = table.Column<double>(type: "float", nullable: true),
                    SpyReturn90d = table.Column<double>(type: "float", nullable: true),
                    Outperformance90d = table.Column<double>(type: "float", nullable: true),
                    Price180d = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    TickerReturn180d = table.Column<double>(type: "float", nullable: true),
                    SpyReturn180d = table.Column<double>(type: "float", nullable: true),
                    Outperformance180d = table.Column<double>(type: "float", nullable: true),
                    Price365d = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    TickerReturn365d = table.Column<double>(type: "float", nullable: true),
                    SpyReturn365d = table.Column<double>(type: "float", nullable: true),
                    Outperformance365d = table.Column<double>(type: "float", nullable: true),
                    LastEvaluatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PickOutcomes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PickOutcomes_EntryDate",
                table: "PickOutcomes",
                column: "EntryDate");

            migrationBuilder.CreateIndex(
                name: "IX_PickOutcomes_ScanDate_Ticker",
                table: "PickOutcomes",
                columns: new[] { "ScanDate", "Ticker" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PickOutcomes");
        }
    }
}
