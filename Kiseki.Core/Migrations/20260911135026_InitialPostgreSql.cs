using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kiseki.Core.Migrations
{
    /// <inheritdoc />
    public partial class InitialPostgreSql : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Franchises",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    JitenAnchorDeckId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Franchises", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MediaSeries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    MediaType = table.Column<int>(type: "integer", nullable: false),
                    FranchiseId = table.Column<Guid>(type: "uuid", nullable: true),
                    JitenDeckId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaSeries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MediaSeries_Franchises_FranchiseId",
                        column: x => x.FranchiseId,
                        principalTable: "Franchises",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "MediaWorks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    MediaType = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    MediaSeriesId = table.Column<Guid>(type: "uuid", nullable: true),
                    JitenDeckId = table.Column<int>(type: "integer", nullable: true),
                    JitenSubdeckId = table.Column<int>(type: "integer", nullable: true),
                    JitenCoverUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    JitenCharacterCount = table.Column<int>(type: "integer", nullable: true),
                    ManualCharacterCountOverride = table.Column<int>(type: "integer", nullable: true),
                    IsCompleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaWorks", x => x.Id);
                    table.CheckConstraint("CK_MediaWorks_JitenSubdeckRequiresDeck", "\"JitenSubdeckId\" IS NULL OR \"JitenDeckId\" IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_MediaWorks_MediaSeries_MediaSeriesId",
                        column: x => x.MediaSeriesId,
                        principalTable: "MediaSeries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ImmersionLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    CharactersRead = table.Column<int>(type: "integer", nullable: false),
                    TimeSpentMinutes = table.Column<double>(type: "double precision", nullable: false),
                    Source = table.Column<string>(type: "text", nullable: false),
                    MediaWorkId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImmersionLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImmersionLogs_MediaWorks_MediaWorkId",
                        column: x => x.MediaWorkId,
                        principalTable: "MediaWorks",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Franchises_JitenAnchorDeckId",
                table: "Franchises",
                column: "JitenAnchorDeckId");

            migrationBuilder.CreateIndex(
                name: "IX_ImmersionLogs_MediaWorkId",
                table: "ImmersionLogs",
                column: "MediaWorkId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaSeries_FranchiseId",
                table: "MediaSeries",
                column: "FranchiseId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaSeries_JitenDeckId",
                table: "MediaSeries",
                column: "JitenDeckId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaWorks_JitenDeckId",
                table: "MediaWorks",
                column: "JitenDeckId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaWorks_JitenSubdeckId",
                table: "MediaWorks",
                column: "JitenSubdeckId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaWorks_MediaSeriesId",
                table: "MediaWorks",
                column: "MediaSeriesId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ImmersionLogs");

            migrationBuilder.DropTable(
                name: "MediaWorks");

            migrationBuilder.DropTable(
                name: "MediaSeries");

            migrationBuilder.DropTable(
                name: "Franchises");
        }
    }
}
