using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Core.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MediaWorks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    JitenDeckId = table.Column<int>(type: "INTEGER", nullable: true),
                    JitenCharacterCount = table.Column<int>(type: "INTEGER", nullable: true),
                    ManualCharacterCountOverride = table.Column<int>(type: "INTEGER", nullable: true),
                    IsCompleted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaWorks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ImmersionLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    CharactersRead = table.Column<int>(type: "INTEGER", nullable: false),
                    TimeSpentSeconds = table.Column<double>(type: "REAL", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    MediaWorkId = table.Column<Guid>(type: "TEXT", nullable: true)
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
                name: "IX_ImmersionLogs_MediaWorkId",
                table: "ImmersionLogs",
                column: "MediaWorkId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ImmersionLogs");

            migrationBuilder.DropTable(
                name: "MediaWorks");
        }
    }
}
