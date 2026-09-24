using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kiseki.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddCanonicalInstallments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // EF's migration lock protects normal startup. Keep the data backfill serialized
            // even when multiple application versions race during a rolling deployment.
            migrationBuilder.Sql("SELECT pg_advisory_xact_lock(1264211171);");

            migrationBuilder.AddColumn<Guid>(
                name: "MediaInstallmentId",
                table: "MediaWorks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "Version",
                table: "MediaWorks",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "MediaInstallments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MediaSeriesId = table.Column<Guid>(type: "uuid", nullable: true),
                    MediaType = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    OrderKey = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    LegacyTitle = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CanonicalTitle = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    TitleOverride = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ReleaseState = table.Column<int>(type: "integer", nullable: false),
                    ReleaseStateOverride = table.Column<int>(type: "integer", nullable: true),
                    ReleaseDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ReleaseDateOverride = table.Column<DateOnly>(type: "date", nullable: true),
                    IsIncluded = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CanonicalCharacterCount = table.Column<int>(type: "integer", nullable: true),
                    CanonicalCoverUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    CanonicalCoverSource = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false, defaultValue: new Guid("00000000-0000-0000-0000-000000000000"))
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaInstallments", x => x.Id);
                    table.CheckConstraint("CK_MediaInstallments_CanonicalCharacterCount", "\"CanonicalCharacterCount\" IS NULL OR \"CanonicalCharacterCount\" > 0");
                    table.CheckConstraint("CK_MediaInstallments_CanonicalCover", "(\"CanonicalCoverUrl\" IS NULL AND \"CanonicalCoverSource\" = 0) OR (\"CanonicalCoverUrl\" IS NOT NULL AND \"CanonicalCoverSource\" <> 0)");
                    table.CheckConstraint("CK_MediaInstallments_CanonicalCoverSource", "\"CanonicalCoverSource\" BETWEEN 0 AND 3");
                    table.CheckConstraint("CK_MediaInstallments_Kind", "\"Kind\" BETWEEN 0 AND 7");
                    table.CheckConstraint("CK_MediaInstallments_MediaType", "\"MediaType\" IN (1, 2, 3)");
                    table.CheckConstraint("CK_MediaInstallments_OrderKey", "\"OrderKey\" >= 0");
                    table.CheckConstraint("CK_MediaInstallments_ReleaseState", "\"ReleaseState\" BETWEEN 0 AND 2 AND (\"ReleaseStateOverride\" IS NULL OR \"ReleaseStateOverride\" BETWEEN 0 AND 2)");
                    table.CheckConstraint("CK_MediaInstallments_Title", "coalesce(length(trim(\"LegacyTitle\")), 0) > 0 OR coalesce(length(trim(\"CanonicalTitle\")), 0) > 0 OR coalesce(length(trim(\"TitleOverride\")), 0) > 0");
                    table.ForeignKey(
                        name: "FK_MediaInstallments_MediaSeries_MediaSeriesId",
                        column: x => x.MediaSeriesId,
                        principalTable: "MediaSeries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "InstallmentProviderIdentities",
                columns: table => new
                {
                    Provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    NormalizedKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    MediaInstallmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderItemId = table.Column<int>(type: "integer", nullable: false),
                    ParentProviderItemId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstallmentProviderIdentities", x => new { x.Provider, x.NormalizedKey });
                    table.CheckConstraint("CK_InstallmentProviderIdentities_ItemIds", "\"ProviderItemId\" > 0 AND (\"ParentProviderItemId\" IS NULL OR \"ParentProviderItemId\" > 0)");
                    table.CheckConstraint("CK_InstallmentProviderIdentities_Keys", "length(trim(\"Provider\")) BETWEEN 1 AND 64 AND length(trim(\"NormalizedKey\")) BETWEEN 1 AND 256");
                    table.ForeignKey(
                        name: "FK_InstallmentProviderIdentities_MediaInstallments_MediaInstal~",
                        column: x => x.MediaInstallmentId,
                        principalTable: "MediaInstallments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InstallmentProviderSnapshots",
                columns: table => new
                {
                    Provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    NormalizedKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CharacterCount = table.Column<int>(type: "integer", nullable: true),
                    CoverUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    CoverSource = table.Column<int>(type: "integer", nullable: false),
                    ReleaseState = table.Column<int>(type: "integer", nullable: false),
                    ReleaseDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ProviderOrder = table.Column<int>(type: "integer", nullable: true),
                    PayloadJson = table.Column<string>(type: "text", nullable: true),
                    ObservedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsComplete = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false, defaultValue: new Guid("00000000-0000-0000-0000-000000000000"))
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstallmentProviderSnapshots", x => new { x.Provider, x.NormalizedKey, x.Fingerprint });
                    table.CheckConstraint("CK_InstallmentProviderSnapshots_CharacterCount", "\"CharacterCount\" IS NULL OR \"CharacterCount\" >= 0");
                    table.CheckConstraint("CK_InstallmentProviderSnapshots_Cover", "(\"CoverUrl\" IS NULL AND \"CoverSource\" = 0) OR (\"CoverUrl\" IS NOT NULL AND \"CoverSource\" <> 0)");
                    table.CheckConstraint("CK_InstallmentProviderSnapshots_Enums", "\"CoverSource\" BETWEEN 0 AND 3 AND \"ReleaseState\" BETWEEN 0 AND 2");
                    table.CheckConstraint("CK_InstallmentProviderSnapshots_Fingerprint", "length(trim(\"Fingerprint\")) BETWEEN 1 AND 128");
                    table.CheckConstraint("CK_InstallmentProviderSnapshots_ProviderOrder", "\"ProviderOrder\" IS NULL OR \"ProviderOrder\" >= 0");
                    table.ForeignKey(
                        name: "FK_InstallmentProviderSnapshots_InstallmentProviderIdentities_~",
                        columns: x => new { x.Provider, x.NormalizedKey },
                        principalTable: "InstallmentProviderIdentities",
                        principalColumns: new[] { "Provider", "NormalizedKey" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql("""
                WITH ranked AS (
                    SELECT
                        w."Id",
                        w."MediaSeriesId",
                        w."MediaType",
                        w."Title",
                        (ROW_NUMBER() OVER (
                            PARTITION BY w."MediaSeriesId"
                            ORDER BY w."Title", w."Id") * 100)::integer AS "OrderKey"
                    FROM "MediaWorks" w
                    WHERE w."MediaInstallmentId" IS NULL
                )
                INSERT INTO "MediaInstallments" (
                    "Id", "MediaSeriesId", "MediaType", "OrderKey", "Kind",
                    "LegacyTitle", "CanonicalTitle", "TitleOverride",
                    "ReleaseState", "ReleaseStateOverride", "ReleaseDate", "ReleaseDateOverride",
                    "IsIncluded", "CanonicalCharacterCount", "CanonicalCoverUrl",
                    "CanonicalCoverSource", "Version")
                SELECT
                    r."Id", r."MediaSeriesId", r."MediaType", r."OrderKey", 0,
                    r."Title", NULL, NULL,
                    0, NULL, NULL, NULL,
                    TRUE, NULL, NULL, 0, r."Id"
                FROM ranked r
                ON CONFLICT ("Id") DO NOTHING;

                UPDATE "MediaWorks"
                SET
                    "MediaInstallmentId" = "Id",
                    "Version" = CASE
                        WHEN "Version" = '00000000-0000-0000-0000-000000000000'::uuid THEN "Id"
                        ELSE "Version"
                    END
                WHERE "MediaInstallmentId" IS NULL;

                WITH claims AS (
                    SELECT
                        w."Id" AS "MediaInstallmentId",
                        CASE
                            WHEN w."JitenSubdeckId" IS NULL
                                THEN 'deck:' || w."JitenDeckId"::text
                            ELSE 'subdeck:' || w."JitenDeckId"::text || ':' || w."JitenSubdeckId"::text
                        END AS "NormalizedKey",
                        CASE
                            WHEN w."JitenSubdeckId" IS NULL THEN w."JitenDeckId"
                            ELSE w."JitenSubdeckId"
                        END AS "ProviderItemId",
                        CASE
                            WHEN w."JitenSubdeckId" IS NULL THEN NULL
                            ELSE w."JitenDeckId"
                        END AS "ParentProviderItemId"
                    FROM "MediaWorks" w
                    WHERE w."JitenDeckId" > 0
                      AND (w."JitenSubdeckId" IS NULL OR w."JitenSubdeckId" > 0)
                ),
                unique_claims AS (
                    SELECT
                        c."MediaInstallmentId",
                        c."NormalizedKey",
                        c."ProviderItemId",
                        c."ParentProviderItemId"
                    FROM claims c
                    WHERE (SELECT COUNT(*) FROM claims duplicate
                           WHERE duplicate."NormalizedKey" = c."NormalizedKey") = 1
                )
                INSERT INTO "InstallmentProviderIdentities" (
                    "Provider", "NormalizedKey", "MediaInstallmentId",
                    "ProviderItemId", "ParentProviderItemId")
                SELECT
                    'jiten', u."NormalizedKey", u."MediaInstallmentId",
                    u."ProviderItemId", u."ParentProviderItemId"
                FROM unique_claims u
                ON CONFLICT ("Provider", "NormalizedKey") DO NOTHING;

                UPDATE "MediaInstallments" i
                SET
                    "CanonicalCharacterCount" = CASE
                        WHEN w."JitenCharacterCount" > 0 THEN w."JitenCharacterCount"
                        ELSE NULL
                    END,
                    "CanonicalCoverUrl" = CASE
                        WHEN w."CoverSource" IN (2, 3) AND w."CoverUrl" IS NOT NULL THEN w."CoverUrl"
                        ELSE NULL
                    END,
                    "CanonicalCoverSource" = CASE
                        WHEN w."CoverSource" = 2 AND w."CoverUrl" IS NOT NULL THEN 1
                        WHEN w."CoverSource" = 3 AND w."CoverUrl" IS NOT NULL THEN 2
                        ELSE 0
                    END
                FROM "InstallmentProviderIdentities" p
                JOIN "MediaWorks" w ON w."Id" = p."MediaInstallmentId"
                WHERE p."Provider" = 'jiten'
                  AND i."Id" = p."MediaInstallmentId";

                INSERT INTO "InstallmentProviderSnapshots" (
                    "Provider", "NormalizedKey", "Fingerprint", "Title",
                    "CharacterCount", "CoverUrl", "CoverSource", "ReleaseState",
                    "ReleaseDate", "ProviderOrder", "PayloadJson", "ObservedAtUtc",
                    "IsComplete", "Version")
                SELECT
                    p."Provider",
                    p."NormalizedKey",
                    'legacy:' || lower(w."Id"::text),
                    NULL,
                    CASE WHEN w."JitenCharacterCount" >= 0 THEN w."JitenCharacterCount" ELSE NULL END,
                    CASE WHEN w."CoverSource" IN (2, 3) AND w."CoverUrl" IS NOT NULL THEN w."CoverUrl" ELSE NULL END,
                    CASE
                        WHEN w."CoverSource" = 2 AND w."CoverUrl" IS NOT NULL THEN 1
                        WHEN w."CoverSource" = 3 AND w."CoverUrl" IS NOT NULL THEN 2
                        ELSE 0
                    END,
                    0,
                    NULL,
                    NULL,
                    NULL,
                    NULL,
                    FALSE,
                    w."Id"
                FROM "InstallmentProviderIdentities" p
                JOIN "MediaWorks" w ON w."Id" = p."MediaInstallmentId"
                ON CONFLICT ("Provider", "NormalizedKey", "Fingerprint") DO NOTHING;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_MediaWorks_MediaInstallmentId",
                table: "MediaWorks",
                column: "MediaInstallmentId");

            migrationBuilder.CreateIndex(
                name: "IX_InstallmentProviderIdentities_MediaInstallmentId",
                table: "InstallmentProviderIdentities",
                column: "MediaInstallmentId");

            migrationBuilder.CreateIndex(
                name: "IX_InstallmentProviderSnapshots_Provider_NormalizedKey_Observe~",
                table: "InstallmentProviderSnapshots",
                columns: new[] { "Provider", "NormalizedKey", "ObservedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MediaInstallments_MediaSeriesId_OrderKey",
                table: "MediaInstallments",
                columns: new[] { "MediaSeriesId", "OrderKey" });

            migrationBuilder.AddForeignKey(
                name: "FK_MediaWorks_MediaInstallments_MediaInstallmentId",
                table: "MediaWorks",
                column: "MediaInstallmentId",
                principalTable: "MediaInstallments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MediaWorks_MediaInstallments_MediaInstallmentId",
                table: "MediaWorks");

            migrationBuilder.DropTable(
                name: "InstallmentProviderSnapshots");

            migrationBuilder.DropTable(
                name: "InstallmentProviderIdentities");

            migrationBuilder.DropTable(
                name: "MediaInstallments");

            migrationBuilder.DropIndex(
                name: "IX_MediaWorks_MediaInstallmentId",
                table: "MediaWorks");

            migrationBuilder.DropColumn(
                name: "MediaInstallmentId",
                table: "MediaWorks");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "MediaWorks");
        }
    }
}
