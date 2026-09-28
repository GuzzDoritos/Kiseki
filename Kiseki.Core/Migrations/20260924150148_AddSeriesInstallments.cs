using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kiseki.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddSeriesInstallments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CoverUrl",
                table: "MediaSeries",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SeriesInstallments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MediaSeriesId = table.Column<Guid>(type: "uuid", nullable: false),
                    SequenceNumber = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    JitenSubdeckId = table.Column<int>(type: "integer", nullable: true),
                    JitenCharacterCount = table.Column<int>(type: "integer", nullable: false),
                    CoverUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    MediaWorkId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeriesInstallments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SeriesInstallments_MediaSeries_MediaSeriesId",
                        column: x => x.MediaSeriesId,
                        principalTable: "MediaSeries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SeriesInstallments_MediaWorks_MediaWorkId",
                        column: x => x.MediaWorkId,
                        principalTable: "MediaWorks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SeriesInstallments_JitenSubdeckId",
                table: "SeriesInstallments",
                column: "JitenSubdeckId");

            migrationBuilder.CreateIndex(
                name: "IX_SeriesInstallments_MediaSeriesId",
                table: "SeriesInstallments",
                column: "MediaSeriesId");

            migrationBuilder.CreateIndex(
                name: "IX_SeriesInstallments_MediaWorkId",
                table: "SeriesInstallments",
                column: "MediaWorkId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SeriesInstallments");

            migrationBuilder.DropColumn(
                name: "CoverUrl",
                table: "MediaSeries");
        }
    }
}
