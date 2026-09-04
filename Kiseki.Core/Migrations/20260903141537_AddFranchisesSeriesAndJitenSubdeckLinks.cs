using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kiseki.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddFranchisesSeriesAndJitenSubdeckLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "JitenSubdeckId",
                table: "MediaWorks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MediaSeriesId",
                table: "MediaWorks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MediaType",
                table: "MediaWorks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateTable(
                name: "Franchises",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    JitenAnchorDeckId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Franchises", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MediaSeries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    MediaType = table.Column<int>(type: "INTEGER", nullable: false),
                    FranchiseId = table.Column<Guid>(type: "TEXT", nullable: true),
                    JitenDeckId = table.Column<int>(type: "INTEGER", nullable: true)
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

            migrationBuilder.AddCheckConstraint(
                name: "CK_MediaWorks_JitenSubdeckRequiresDeck",
                table: "MediaWorks",
                sql: "JitenSubdeckId IS NULL OR JitenDeckId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Franchises_JitenAnchorDeckId",
                table: "Franchises",
                column: "JitenAnchorDeckId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaSeries_FranchiseId",
                table: "MediaSeries",
                column: "FranchiseId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaSeries_JitenDeckId",
                table: "MediaSeries",
                column: "JitenDeckId");

            migrationBuilder.AddForeignKey(
                name: "FK_MediaWorks_MediaSeries_MediaSeriesId",
                table: "MediaWorks",
                column: "MediaSeriesId",
                principalTable: "MediaSeries",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MediaWorks_MediaSeries_MediaSeriesId",
                table: "MediaWorks");

            migrationBuilder.DropTable(
                name: "MediaSeries");

            migrationBuilder.DropTable(
                name: "Franchises");

            migrationBuilder.DropIndex(
                name: "IX_MediaWorks_JitenDeckId",
                table: "MediaWorks");

            migrationBuilder.DropIndex(
                name: "IX_MediaWorks_JitenSubdeckId",
                table: "MediaWorks");

            migrationBuilder.DropIndex(
                name: "IX_MediaWorks_MediaSeriesId",
                table: "MediaWorks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_MediaWorks_JitenSubdeckRequiresDeck",
                table: "MediaWorks");

            migrationBuilder.DropColumn(
                name: "JitenSubdeckId",
                table: "MediaWorks");

            migrationBuilder.DropColumn(
                name: "MediaSeriesId",
                table: "MediaWorks");

            migrationBuilder.DropColumn(
                name: "MediaType",
                table: "MediaWorks");
        }
    }
}
