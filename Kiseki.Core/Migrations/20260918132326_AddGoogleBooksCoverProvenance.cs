using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kiseki.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddGoogleBooksCoverProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CoverProviderItemId",
                table: "MediaWorks",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_MediaWorks_CoverProviderItemId",
                table: "MediaWorks",
                sql: "(\"CoverSource\" = 5 AND \"CoverProviderItemId\" IS NOT NULL) OR (\"CoverSource\" <> 5 AND \"CoverProviderItemId\" IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_MediaWorks_CoverProviderItemId",
                table: "MediaWorks");

            migrationBuilder.DropColumn(
                name: "CoverProviderItemId",
                table: "MediaWorks");
        }
    }
}
