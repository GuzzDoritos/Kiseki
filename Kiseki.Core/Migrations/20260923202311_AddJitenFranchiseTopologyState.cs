using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kiseki.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddJitenFranchiseTopologyState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "JitenFranchiseGraphNodeStates",
                columns: table => new
                {
                    FranchiseId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeckId = table.Column<int>(type: "integer", nullable: false),
                    Resolution = table.Column<int>(type: "integer", nullable: false),
                    MediaSeriesId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastProviderTitle = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ProviderMediaType = table.Column<int>(type: "integer", nullable: false),
                    ProviderFingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false, defaultValue: new Guid("00000000-0000-0000-0000-000000000000"))
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JitenFranchiseGraphNodeStates", x => new { x.FranchiseId, x.DeckId });
                    table.CheckConstraint("CK_JitenFranchiseGraphNodeStates_Deck", "\"DeckId\" > 0");
                    table.CheckConstraint("CK_JitenFranchiseGraphNodeStates_Fingerprint", "length(trim(\"ProviderFingerprint\")) BETWEEN 1 AND 128");
                    table.CheckConstraint("CK_JitenFranchiseGraphNodeStates_Resolution", "\"Resolution\" BETWEEN 1 AND 3");
                    table.ForeignKey(
                        name: "FK_JitenFranchiseGraphNodeStates_Franchises_FranchiseId",
                        column: x => x.FranchiseId,
                        principalTable: "Franchises",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_JitenFranchiseGraphNodeStates_MediaSeries_MediaSeriesId",
                        column: x => x.MediaSeriesId,
                        principalTable: "MediaSeries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "JitenFranchiseTopologyReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FranchiseId = table.Column<Guid>(type: "uuid", nullable: false),
                    AnchorDeckId = table.Column<int>(type: "integer", nullable: false),
                    ReviewFingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CreatedSeries = table.Column<int>(type: "integer", nullable: false),
                    LinkedSeries = table.Column<int>(type: "integer", nullable: false),
                    IgnoredNodes = table.Column<int>(type: "integer", nullable: false),
                    UnresolvedNodes = table.Column<int>(type: "integer", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JitenFranchiseTopologyReceipts", x => x.Id);
                    table.CheckConstraint("CK_JitenFranchiseTopologyReceipts_Anchor", "\"AnchorDeckId\" > 0");
                    table.CheckConstraint("CK_JitenFranchiseTopologyReceipts_Fingerprint", "length(trim(\"ReviewFingerprint\")) BETWEEN 1 AND 128");
                    table.ForeignKey(
                        name: "FK_JitenFranchiseTopologyReceipts_Franchises_FranchiseId",
                        column: x => x.FranchiseId,
                        principalTable: "Franchises",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JitenFranchiseGraphNodeStates_MediaSeriesId",
                table: "JitenFranchiseGraphNodeStates",
                column: "MediaSeriesId");

            migrationBuilder.CreateIndex(
                name: "IX_JitenFranchiseTopologyReceipts_FranchiseId",
                table: "JitenFranchiseTopologyReceipts",
                column: "FranchiseId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JitenFranchiseGraphNodeStates");

            migrationBuilder.DropTable(
                name: "JitenFranchiseTopologyReceipts");
        }
    }
}
