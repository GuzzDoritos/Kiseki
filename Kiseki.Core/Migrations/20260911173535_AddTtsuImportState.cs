using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kiseki.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddTtsuImportState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "SourceRevision",
                table: "ImmersionLogs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TtsuBindingId",
                table: "ImmersionLogs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TtsuBindings",
                columns: table => new
                {
                    MediaWorkId = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginalTitle = table.Column<string>(type: "text", nullable: false),
                    FolderHint = table.Column<string>(type: "text", nullable: true),
                    Version = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TtsuBindings", x => x.MediaWorkId);
                    table.ForeignKey(
                        name: "FK_TtsuBindings_MediaWorks_MediaWorkId",
                        column: x => x.MediaWorkId,
                        principalTable: "MediaWorks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TtsuImportReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Books = table.Column<int>(type: "integer", nullable: false),
                    AddedDays = table.Column<int>(type: "integer", nullable: false),
                    UpdatedDays = table.Column<int>(type: "integer", nullable: false),
                    UnchangedDays = table.Column<int>(type: "integer", nullable: false),
                    StaleDays = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TtsuImportReceipts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ImmersionLogs_TtsuBindingId_Date",
                table: "ImmersionLogs",
                columns: new[] { "TtsuBindingId", "Date" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_ImmersionLogs_TtsuBinding",
                table: "ImmersionLogs",
                sql: "\"TtsuBindingId\" IS NULL OR (\"MediaWorkId\" IS NOT NULL AND \"TtsuBindingId\" = \"MediaWorkId\" AND \"Source\" = 'ttsu')");

            migrationBuilder.AddForeignKey(
                name: "FK_ImmersionLogs_TtsuBindings_TtsuBindingId",
                table: "ImmersionLogs",
                column: "TtsuBindingId",
                principalTable: "TtsuBindings",
                principalColumn: "MediaWorkId",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ImmersionLogs_TtsuBindings_TtsuBindingId",
                table: "ImmersionLogs");

            migrationBuilder.DropTable(
                name: "TtsuBindings");

            migrationBuilder.DropTable(
                name: "TtsuImportReceipts");

            migrationBuilder.DropIndex(
                name: "IX_ImmersionLogs_TtsuBindingId_Date",
                table: "ImmersionLogs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ImmersionLogs_TtsuBinding",
                table: "ImmersionLogs");

            migrationBuilder.DropColumn(
                name: "SourceRevision",
                table: "ImmersionLogs");

            migrationBuilder.DropColumn(
                name: "TtsuBindingId",
                table: "ImmersionLogs");
        }
    }
}
