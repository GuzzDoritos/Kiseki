using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kiseki.Core.Migrations
{
    /// <inheritdoc />
    public partial class GeneralizeMediaWorkCoverProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "JitenCoverUrl",
                table: "MediaWorks",
                newName: "CoverUrl");

            migrationBuilder.AddColumn<int>(
                name: "CoverSource",
                table: "MediaWorks",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql("UPDATE \"MediaWorks\" SET \"CoverSource\" = 1 WHERE \"CoverUrl\" IS NOT NULL;");
            migrationBuilder.Sql("UPDATE \"MediaWorks\" SET \"CoverSource\" = 0 WHERE \"CoverUrl\" IS NULL;");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MediaWorks_CoverUrlAndSource",
                table: "MediaWorks",
                sql: "(\"CoverUrl\" IS NULL AND \"CoverSource\" = 0) OR (\"CoverUrl\" IS NOT NULL AND \"CoverSource\" <> 0)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_MediaWorks_CoverUrlAndSource",
                table: "MediaWorks");

            migrationBuilder.DropColumn(
                name: "CoverSource",
                table: "MediaWorks");

            migrationBuilder.RenameColumn(
                name: "CoverUrl",
                table: "MediaWorks",
                newName: "JitenCoverUrl");
        }
    }
}
