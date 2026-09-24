using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kiseki.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddJitenCatalogueRefreshState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CharacterCountOverride",
                table: "MediaInstallments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastSeenAtUtc",
                table: "InstallmentProviderIdentities",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MissingSinceUtc",
                table: "InstallmentProviderIdentities",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "JitenCatalogueRefreshReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MediaSeriesId = table.Column<Guid>(type: "uuid", nullable: false),
                    JitenDeckId = table.Column<int>(type: "integer", nullable: false),
                    ReviewFingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    AddedInstallments = table.Column<int>(type: "integer", nullable: false),
                    LinkedIdentities = table.Column<int>(type: "integer", nullable: false),
                    UpdatedInstallments = table.Column<int>(type: "integer", nullable: false),
                    MarkedMissing = table.Column<int>(type: "integer", nullable: false),
                    Ignored = table.Column<int>(type: "integer", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JitenCatalogueRefreshReceipts", x => x.Id);
                    table.CheckConstraint("CK_JitenCatalogueRefreshReceipts_Deck", "\"JitenDeckId\" > 0");
                    table.CheckConstraint("CK_JitenCatalogueRefreshReceipts_Fingerprint", "length(trim(\"ReviewFingerprint\")) BETWEEN 1 AND 128");
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_MediaInstallments_CharacterCountOverride",
                table: "MediaInstallments",
                sql: "\"CharacterCountOverride\" IS NULL OR \"CharacterCountOverride\" > 0");

            migrationBuilder.CreateIndex(
                name: "IX_JitenCatalogueRefreshReceipts_MediaSeriesId",
                table: "JitenCatalogueRefreshReceipts",
                column: "MediaSeriesId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JitenCatalogueRefreshReceipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_MediaInstallments_CharacterCountOverride",
                table: "MediaInstallments");

            migrationBuilder.DropColumn(
                name: "CharacterCountOverride",
                table: "MediaInstallments");

            migrationBuilder.DropColumn(
                name: "LastSeenAtUtc",
                table: "InstallmentProviderIdentities");

            migrationBuilder.DropColumn(
                name: "MissingSinceUtc",
                table: "InstallmentProviderIdentities");
        }
    }
}
