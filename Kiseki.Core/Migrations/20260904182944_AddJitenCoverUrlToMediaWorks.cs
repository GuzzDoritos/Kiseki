using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kiseki.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddJitenCoverUrlToMediaWorks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "JitenCoverUrl",
                table: "MediaWorks",
                type: "TEXT",
                maxLength: 2048,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "JitenCoverUrl",
                table: "MediaWorks");
        }
    }
}
