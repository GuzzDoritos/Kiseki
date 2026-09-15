using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kiseki.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddTtsuProgressState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CharacterTotalUpdates",
                table: "TtsuImportReceipts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ProgressUpdates",
                table: "TtsuImportReceipts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CurrentCharacterPosition",
                table: "TtsuBindings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProgressDatabaseVersion",
                table: "TtsuBindings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProgressExporterVersion",
                table: "TtsuBindings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ProgressFraction",
                table: "TtsuBindings",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ProgressRevision",
                table: "TtsuBindings",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TotalInferenceKind",
                table: "TtsuBindings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TtsuCharacterCount",
                table: "MediaWorks",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_TtsuBindings_CurrentCharacterPosition",
                table: "TtsuBindings",
                sql: "\"CurrentCharacterPosition\" IS NULL OR \"CurrentCharacterPosition\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_TtsuBindings_ProgressFraction",
                table: "TtsuBindings",
                sql: "\"ProgressFraction\" IS NULL OR (\"ProgressFraction\" >= 0 AND \"ProgressFraction\" <= 1)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_TtsuBindings_ProgressRevision",
                table: "TtsuBindings",
                sql: "\"ProgressRevision\" IS NULL OR \"ProgressRevision\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MediaWorks_TtsuCharacterCount",
                table: "MediaWorks",
                sql: "\"TtsuCharacterCount\" IS NULL OR \"TtsuCharacterCount\" > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_TtsuBindings_CurrentCharacterPosition",
                table: "TtsuBindings");

            migrationBuilder.DropCheckConstraint(
                name: "CK_TtsuBindings_ProgressFraction",
                table: "TtsuBindings");

            migrationBuilder.DropCheckConstraint(
                name: "CK_TtsuBindings_ProgressRevision",
                table: "TtsuBindings");

            migrationBuilder.DropCheckConstraint(
                name: "CK_MediaWorks_TtsuCharacterCount",
                table: "MediaWorks");

            migrationBuilder.DropColumn(
                name: "CharacterTotalUpdates",
                table: "TtsuImportReceipts");

            migrationBuilder.DropColumn(
                name: "ProgressUpdates",
                table: "TtsuImportReceipts");

            migrationBuilder.DropColumn(
                name: "CurrentCharacterPosition",
                table: "TtsuBindings");

            migrationBuilder.DropColumn(
                name: "ProgressDatabaseVersion",
                table: "TtsuBindings");

            migrationBuilder.DropColumn(
                name: "ProgressExporterVersion",
                table: "TtsuBindings");

            migrationBuilder.DropColumn(
                name: "ProgressFraction",
                table: "TtsuBindings");

            migrationBuilder.DropColumn(
                name: "ProgressRevision",
                table: "TtsuBindings");

            migrationBuilder.DropColumn(
                name: "TotalInferenceKind",
                table: "TtsuBindings");

            migrationBuilder.DropColumn(
                name: "TtsuCharacterCount",
                table: "MediaWorks");
        }
    }
}
