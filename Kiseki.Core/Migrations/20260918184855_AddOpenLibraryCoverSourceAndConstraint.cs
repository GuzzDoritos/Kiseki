using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kiseki.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddOpenLibraryCoverSourceAndConstraint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_MediaWorks_CoverProviderItemId",
                table: "MediaWorks");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MediaWorks_CoverProviderItemId",
                table: "MediaWorks",
                sql: "(\"CoverSource\" IN (5, 6) AND \"CoverProviderItemId\" IS NOT NULL) OR (\"CoverSource\" NOT IN (5, 6) AND \"CoverProviderItemId\" IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_MediaWorks_CoverProviderItemId",
                table: "MediaWorks");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MediaWorks_CoverProviderItemId",
                table: "MediaWorks",
                sql: "(\"CoverSource\" = 5 AND \"CoverProviderItemId\" IS NOT NULL) OR (\"CoverSource\" <> 5 AND \"CoverProviderItemId\" IS NULL)");
        }
    }
}
