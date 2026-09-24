using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Kiseki.Core.Services;

// Existing local databases were created with EnsureCreated, without a SQLite migration history.
// Keep this additive bridge separate from the authoritative PostgreSQL EF migrations.
public static class SqliteSchemaUpgrade
{
    public static async Task ApplyAsync(ImmersionDbContext context, CancellationToken cancellationToken = default)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = (SqliteConnection)context.Database.GetDbConnection();
            await using var sqliteTransaction = connection.BeginTransaction(deferred: false);
            await using var transaction = context.Database.UseTransaction(sqliteTransaction)
                ?? throw new InvalidOperationException("SQLite upgrade transaction could not be attached to EF Core.");
            await context.Database.EnsureCreatedAsync(cancellationToken);
            await context.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "TtsuBindings" (
                "MediaWorkId" TEXT NOT NULL CONSTRAINT "PK_TtsuBindings" PRIMARY KEY,
                "OriginalTitle" TEXT NOT NULL,
                "FolderHint" TEXT NULL,
                "Version" TEXT NOT NULL,
                "CurrentCharacterPosition" INTEGER NULL CHECK ("CurrentCharacterPosition" >= 0),
                "ProgressFraction" REAL NULL CHECK ("ProgressFraction" >= 0 AND "ProgressFraction" <= 1),
                "ProgressRevision" INTEGER NULL CHECK ("ProgressRevision" >= 0),
                "ProgressExporterVersion" INTEGER NULL,
                "ProgressDatabaseVersion" INTEGER NULL,
                "TotalInferenceKind" INTEGER NULL,
                CONSTRAINT "FK_TtsuBindings_MediaWorks_MediaWorkId" FOREIGN KEY ("MediaWorkId") REFERENCES "MediaWorks" ("Id") ON DELETE CASCADE);
            CREATE TABLE IF NOT EXISTS "TtsuImportReceipts" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_TtsuImportReceipts" PRIMARY KEY,
                "Books" INTEGER NOT NULL, "AddedDays" INTEGER NOT NULL, "UpdatedDays" INTEGER NOT NULL,
                "UnchangedDays" INTEGER NOT NULL, "StaleDays" INTEGER NOT NULL,
                "ProgressUpdates" INTEGER NOT NULL DEFAULT 0, "CharacterTotalUpdates" INTEGER NOT NULL DEFAULT 0,
                "MetadataLinks" INTEGER NOT NULL DEFAULT 0, "MetadataSkips" INTEGER NOT NULL DEFAULT 0);
            """, cancellationToken);
            await context.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "MediaInstallments" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_MediaInstallments" PRIMARY KEY,
                "MediaSeriesId" TEXT NULL,
                "MediaType" INTEGER NOT NULL DEFAULT 1,
                "OrderKey" INTEGER NOT NULL CONSTRAINT "CK_MediaInstallments_OrderKey" CHECK ("OrderKey" >= 0),
                "Kind" INTEGER NOT NULL DEFAULT 0,
                "LegacyTitle" TEXT NULL,
                "CanonicalTitle" TEXT NULL,
                "TitleOverride" TEXT NULL,
                "ReleaseState" INTEGER NOT NULL DEFAULT 0,
                "ReleaseStateOverride" INTEGER NULL,
                "ReleaseDate" TEXT NULL,
                "ReleaseDateOverride" TEXT NULL,
                "IsIncluded" INTEGER NOT NULL DEFAULT 1,
                "CanonicalCharacterCount" INTEGER NULL CONSTRAINT "CK_MediaInstallments_CanonicalCharacterCount" CHECK ("CanonicalCharacterCount" IS NULL OR "CanonicalCharacterCount" > 0),
                "CharacterCountOverride" INTEGER NULL CONSTRAINT "CK_MediaInstallments_CharacterCountOverride" CHECK ("CharacterCountOverride" IS NULL OR "CharacterCountOverride" > 0),
                "CanonicalCoverUrl" TEXT NULL,
                "CanonicalCoverSource" INTEGER NOT NULL DEFAULT 0,
                "Version" TEXT NOT NULL,
                CONSTRAINT "CK_MediaInstallments_MediaType" CHECK ("MediaType" IN (1, 2, 3)),
                CONSTRAINT "CK_MediaInstallments_Kind" CHECK ("Kind" BETWEEN 0 AND 7),
                CONSTRAINT "CK_MediaInstallments_ReleaseState" CHECK ("ReleaseState" BETWEEN 0 AND 2 AND ("ReleaseStateOverride" IS NULL OR "ReleaseStateOverride" BETWEEN 0 AND 2)),
                CONSTRAINT "CK_MediaInstallments_CanonicalCoverSource" CHECK ("CanonicalCoverSource" BETWEEN 0 AND 3),
                CONSTRAINT "CK_MediaInstallments_Title" CHECK (coalesce(length(trim("LegacyTitle")), 0) > 0 OR coalesce(length(trim("CanonicalTitle")), 0) > 0 OR coalesce(length(trim("TitleOverride")), 0) > 0),
                CONSTRAINT "CK_MediaInstallments_CanonicalCover" CHECK (("CanonicalCoverUrl" IS NULL AND "CanonicalCoverSource" = 0) OR ("CanonicalCoverUrl" IS NOT NULL AND "CanonicalCoverSource" <> 0)),
                CONSTRAINT "FK_MediaInstallments_MediaSeries_MediaSeriesId" FOREIGN KEY ("MediaSeriesId") REFERENCES "MediaSeries" ("Id") ON DELETE SET NULL);

            CREATE TABLE IF NOT EXISTS "InstallmentProviderIdentities" (
                "Provider" TEXT NOT NULL,
                "NormalizedKey" TEXT NOT NULL,
                "MediaInstallmentId" TEXT NOT NULL,
                "ProviderItemId" INTEGER NOT NULL,
                "ParentProviderItemId" INTEGER NULL,
                "LastSeenAtUtc" TEXT NULL,
                "MissingSinceUtc" TEXT NULL,
                CONSTRAINT "PK_InstallmentProviderIdentities" PRIMARY KEY ("Provider", "NormalizedKey"),
                CONSTRAINT "CK_InstallmentProviderIdentities_ItemIds" CHECK ("ProviderItemId" > 0 AND ("ParentProviderItemId" IS NULL OR "ParentProviderItemId" > 0)),
                CONSTRAINT "CK_InstallmentProviderIdentities_Keys" CHECK (length(trim("Provider")) BETWEEN 1 AND 64 AND length(trim("NormalizedKey")) BETWEEN 1 AND 256),
                CONSTRAINT "FK_InstallmentProviderIdentities_MediaInstallments_MediaInstallmentId" FOREIGN KEY ("MediaInstallmentId") REFERENCES "MediaInstallments" ("Id") ON DELETE CASCADE);

            CREATE TABLE IF NOT EXISTS "InstallmentProviderSnapshots" (
                "Provider" TEXT NOT NULL,
                "NormalizedKey" TEXT NOT NULL,
                "Fingerprint" TEXT NOT NULL,
                "Title" TEXT NULL,
                "CharacterCount" INTEGER NULL CONSTRAINT "CK_InstallmentProviderSnapshots_CharacterCount" CHECK ("CharacterCount" IS NULL OR "CharacterCount" >= 0),
                "CoverUrl" TEXT NULL,
                "CoverSource" INTEGER NOT NULL DEFAULT 0,
                "ReleaseState" INTEGER NOT NULL DEFAULT 0,
                "ReleaseDate" TEXT NULL,
                "ProviderOrder" INTEGER NULL CONSTRAINT "CK_InstallmentProviderSnapshots_ProviderOrder" CHECK ("ProviderOrder" IS NULL OR "ProviderOrder" >= 0),
                "PayloadJson" TEXT NULL,
                "ObservedAtUtc" TEXT NULL,
                "IsComplete" INTEGER NOT NULL DEFAULT 0,
                "Version" TEXT NOT NULL,
                CONSTRAINT "PK_InstallmentProviderSnapshots" PRIMARY KEY ("Provider", "NormalizedKey", "Fingerprint"),
                CONSTRAINT "CK_InstallmentProviderSnapshots_Enums" CHECK ("CoverSource" BETWEEN 0 AND 3 AND "ReleaseState" BETWEEN 0 AND 2),
                CONSTRAINT "CK_InstallmentProviderSnapshots_Fingerprint" CHECK (length(trim("Fingerprint")) BETWEEN 1 AND 128),
                CONSTRAINT "CK_InstallmentProviderSnapshots_Cover" CHECK (("CoverUrl" IS NULL AND "CoverSource" = 0) OR ("CoverUrl" IS NOT NULL AND "CoverSource" <> 0)),
                CONSTRAINT "FK_InstallmentProviderSnapshots_InstallmentProviderIdentities" FOREIGN KEY ("Provider", "NormalizedKey") REFERENCES "InstallmentProviderIdentities" ("Provider", "NormalizedKey") ON DELETE CASCADE);

            CREATE TABLE IF NOT EXISTS "JitenCatalogueRefreshReceipts" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_JitenCatalogueRefreshReceipts" PRIMARY KEY,
                "MediaSeriesId" TEXT NOT NULL,
                "JitenDeckId" INTEGER NOT NULL CONSTRAINT "CK_JitenCatalogueRefreshReceipts_Deck" CHECK ("JitenDeckId" > 0),
                "ReviewFingerprint" TEXT NOT NULL CONSTRAINT "CK_JitenCatalogueRefreshReceipts_Fingerprint" CHECK (length(trim("ReviewFingerprint")) BETWEEN 1 AND 128),
                "AddedInstallments" INTEGER NOT NULL,
                "LinkedIdentities" INTEGER NOT NULL,
                "UpdatedInstallments" INTEGER NOT NULL,
                "MarkedMissing" INTEGER NOT NULL,
                "Ignored" INTEGER NOT NULL,
                "CompletedAtUtc" TEXT NOT NULL);

            CREATE TABLE IF NOT EXISTS "JitenFranchiseGraphNodeStates" (
                "FranchiseId" TEXT NOT NULL,
                "DeckId" INTEGER NOT NULL CONSTRAINT "CK_JitenFranchiseGraphNodeStates_Deck" CHECK ("DeckId" > 0),
                "Resolution" INTEGER NOT NULL CONSTRAINT "CK_JitenFranchiseGraphNodeStates_Resolution" CHECK ("Resolution" BETWEEN 1 AND 3),
                "MediaSeriesId" TEXT NULL,
                "LastProviderTitle" TEXT NOT NULL,
                "ProviderMediaType" INTEGER NOT NULL,
                "ProviderFingerprint" TEXT NOT NULL CONSTRAINT "CK_JitenFranchiseGraphNodeStates_Fingerprint" CHECK (length(trim("ProviderFingerprint")) BETWEEN 1 AND 128),
                "UpdatedAtUtc" TEXT NOT NULL,
                "Version" TEXT NOT NULL,
                CONSTRAINT "PK_JitenFranchiseGraphNodeStates" PRIMARY KEY ("FranchiseId", "DeckId"),
                CONSTRAINT "FK_JitenFranchiseGraphNodeStates_Franchises" FOREIGN KEY ("FranchiseId") REFERENCES "Franchises" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_JitenFranchiseGraphNodeStates_MediaSeries" FOREIGN KEY ("MediaSeriesId") REFERENCES "MediaSeries" ("Id") ON DELETE SET NULL);

            CREATE TABLE IF NOT EXISTS "JitenFranchiseTopologyReceipts" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_JitenFranchiseTopologyReceipts" PRIMARY KEY,
                "FranchiseId" TEXT NOT NULL,
                "AnchorDeckId" INTEGER NOT NULL CONSTRAINT "CK_JitenFranchiseTopologyReceipts_Anchor" CHECK ("AnchorDeckId" > 0),
                "ReviewFingerprint" TEXT NOT NULL CONSTRAINT "CK_JitenFranchiseTopologyReceipts_Fingerprint" CHECK (length(trim("ReviewFingerprint")) BETWEEN 1 AND 128),
                "CreatedSeries" INTEGER NOT NULL,
                "LinkedSeries" INTEGER NOT NULL,
                "IgnoredNodes" INTEGER NOT NULL,
                "UnresolvedNodes" INTEGER NOT NULL,
                "CompletedAtUtc" TEXT NOT NULL,
                CONSTRAINT "FK_JitenFranchiseTopologyReceipts_Franchises" FOREIGN KEY ("FranchiseId") REFERENCES "Franchises" ("Id") ON DELETE CASCADE);
            """, cancellationToken);
            var logColumns = await GetColumnsAsync(context, transaction, "ImmersionLogs", cancellationToken);
            if (!logColumns.Contains("TtsuBindingId"))
                await context.Database.ExecuteSqlRawAsync("""
                ALTER TABLE "ImmersionLogs" ADD COLUMN "TtsuBindingId" TEXT NULL
                    REFERENCES "TtsuBindings" ("MediaWorkId") ON DELETE RESTRICT
                    CONSTRAINT "CK_ImmersionLogs_TtsuBinding" CHECK (
                        "TtsuBindingId" IS NULL OR ("MediaWorkId" IS NOT NULL AND "TtsuBindingId" = "MediaWorkId" AND "Source" = 'ttsu'));
                """, cancellationToken);
            if (!logColumns.Contains("SourceRevision"))
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"ImmersionLogs\" ADD COLUMN \"SourceRevision\" INTEGER NULL;", cancellationToken);

            var workColumns = await GetColumnsAsync(context, transaction, "MediaWorks", cancellationToken);
            if (!workColumns.Contains("TtsuCharacterCount"))
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"MediaWorks\" ADD COLUMN \"TtsuCharacterCount\" INTEGER NULL CHECK (\"TtsuCharacterCount\" > 0);", cancellationToken);
            if (workColumns.Contains("JitenCoverUrl") && !workColumns.Contains("CoverUrl"))
            {
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"MediaWorks\" RENAME COLUMN \"JitenCoverUrl\" TO \"CoverUrl\";", cancellationToken);
                workColumns.Add("CoverUrl");
            }
            if (!workColumns.Contains("CoverSource"))
            {
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"MediaWorks\" ADD COLUMN \"CoverSource\" INTEGER NOT NULL DEFAULT 0;", cancellationToken);
                await context.Database.ExecuteSqlRawAsync("UPDATE \"MediaWorks\" SET \"CoverSource\" = 1 WHERE \"CoverUrl\" IS NOT NULL;", cancellationToken);
                await context.Database.ExecuteSqlRawAsync("UPDATE \"MediaWorks\" SET \"CoverSource\" = 0 WHERE \"CoverUrl\" IS NULL;", cancellationToken);
                workColumns.Add("CoverSource");
            }
            if (!workColumns.Contains("CoverProviderItemId"))
            {
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"MediaWorks\" ADD COLUMN \"CoverProviderItemId\" TEXT NULL;", cancellationToken);
                workColumns.Add("CoverProviderItemId");
            }
            if (!workColumns.Contains("MediaInstallmentId"))
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"MediaWorks\" ADD COLUMN \"MediaInstallmentId\" TEXT NULL REFERENCES \"MediaInstallments\" (\"Id\") ON DELETE RESTRICT;", cancellationToken);
            if (!workColumns.Contains("Version"))
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"MediaWorks\" ADD COLUMN \"Version\" TEXT NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000';", cancellationToken);

            var installmentColumns = await GetColumnsAsync(context, transaction, "MediaInstallments", cancellationToken);
            if (!installmentColumns.Contains("CharacterCountOverride"))
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"MediaInstallments\" ADD COLUMN \"CharacterCountOverride\" INTEGER NULL CHECK (\"CharacterCountOverride\" > 0);", cancellationToken);

            var bindingColumns = await GetColumnsAsync(context, transaction, "TtsuBindings", cancellationToken);
            if (!bindingColumns.Contains("CurrentCharacterPosition"))
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"TtsuBindings\" ADD COLUMN \"CurrentCharacterPosition\" INTEGER NULL CHECK (\"CurrentCharacterPosition\" >= 0);", cancellationToken);
            if (!bindingColumns.Contains("ProgressFraction"))
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"TtsuBindings\" ADD COLUMN \"ProgressFraction\" REAL NULL CHECK (\"ProgressFraction\" >= 0 AND \"ProgressFraction\" <= 1);", cancellationToken);
            if (!bindingColumns.Contains("ProgressRevision"))
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"TtsuBindings\" ADD COLUMN \"ProgressRevision\" INTEGER NULL CHECK (\"ProgressRevision\" >= 0);", cancellationToken);
            if (!bindingColumns.Contains("ProgressExporterVersion"))
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"TtsuBindings\" ADD COLUMN \"ProgressExporterVersion\" INTEGER NULL;", cancellationToken);
            if (!bindingColumns.Contains("ProgressDatabaseVersion"))
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"TtsuBindings\" ADD COLUMN \"ProgressDatabaseVersion\" INTEGER NULL;", cancellationToken);
            if (!bindingColumns.Contains("TotalInferenceKind"))
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"TtsuBindings\" ADD COLUMN \"TotalInferenceKind\" INTEGER NULL;", cancellationToken);

            var receiptColumns = await GetColumnsAsync(context, transaction, "TtsuImportReceipts", cancellationToken);
            if (!receiptColumns.Contains("ProgressUpdates"))
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"TtsuImportReceipts\" ADD COLUMN \"ProgressUpdates\" INTEGER NOT NULL DEFAULT 0;", cancellationToken);
            if (!receiptColumns.Contains("CharacterTotalUpdates"))
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"TtsuImportReceipts\" ADD COLUMN \"CharacterTotalUpdates\" INTEGER NOT NULL DEFAULT 0;", cancellationToken);
            if (!receiptColumns.Contains("MetadataLinks"))
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"TtsuImportReceipts\" ADD COLUMN \"MetadataLinks\" INTEGER NOT NULL DEFAULT 0;", cancellationToken);
            if (!receiptColumns.Contains("MetadataSkips"))
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"TtsuImportReceipts\" ADD COLUMN \"MetadataSkips\" INTEGER NOT NULL DEFAULT 0;", cancellationToken);

            var providerIdentityColumns = await GetColumnsAsync(context, transaction, "InstallmentProviderIdentities", cancellationToken);
            if (!providerIdentityColumns.Contains("LastSeenAtUtc"))
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"InstallmentProviderIdentities\" ADD COLUMN \"LastSeenAtUtc\" TEXT NULL;", cancellationToken);
            if (!providerIdentityColumns.Contains("MissingSinceUtc"))
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"InstallmentProviderIdentities\" ADD COLUMN \"MissingSinceUtc\" TEXT NULL;", cancellationToken);
            await context.Database.ExecuteSqlRawAsync("""
            WITH ranked AS (
                SELECT
                    w."Id",
                    w."MediaSeriesId",
                    w."MediaType",
                    w."Title",
                    ROW_NUMBER() OVER (
                        PARTITION BY w."MediaSeriesId"
                        ORDER BY w."Title" COLLATE NOCASE, w."Id") * 100 AS "OrderKey"
                FROM "MediaWorks" w
                WHERE w."MediaInstallmentId" IS NULL
            )
            INSERT OR IGNORE INTO "MediaInstallments" (
                "Id", "MediaSeriesId", "MediaType", "OrderKey", "Kind",
                "LegacyTitle", "CanonicalTitle", "TitleOverride",
                "ReleaseState", "ReleaseStateOverride", "ReleaseDate", "ReleaseDateOverride",
                "IsIncluded", "CanonicalCharacterCount", "CanonicalCoverUrl",
                "CanonicalCoverSource", "Version")
            SELECT
                r."Id", r."MediaSeriesId", r."MediaType", r."OrderKey", 0,
                r."Title", NULL, NULL,
                0, NULL, NULL, NULL,
                1, NULL, NULL, 0, r."Id"
            FROM ranked r;

            UPDATE "MediaWorks"
            SET
                "MediaInstallmentId" = "Id",
                "Version" = CASE
                    WHEN "Version" = '00000000-0000-0000-0000-000000000000' THEN "Id"
                    ELSE "Version"
                END
            WHERE "MediaInstallmentId" IS NULL;

            WITH claims AS (
                SELECT
                    w."Id" AS "MediaInstallmentId",
                    CASE
                        WHEN w."JitenSubdeckId" IS NULL
                            THEN 'deck:' || w."JitenDeckId"
                        ELSE 'subdeck:' || w."JitenDeckId" || ':' || w."JitenSubdeckId"
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
            )
            INSERT OR IGNORE INTO "InstallmentProviderIdentities" (
                "Provider", "NormalizedKey", "MediaInstallmentId",
                "ProviderItemId", "ParentProviderItemId")
            SELECT
                'jiten', c."NormalizedKey", c."MediaInstallmentId",
                c."ProviderItemId", c."ParentProviderItemId"
            FROM claims c
            WHERE (SELECT COUNT(*) FROM claims duplicate
                   WHERE duplicate."NormalizedKey" = c."NormalizedKey") = 1;

            UPDATE "MediaInstallments"
            SET
                "CanonicalCharacterCount" = (
                    SELECT CASE WHEN w."JitenCharacterCount" > 0 THEN w."JitenCharacterCount" ELSE NULL END
                    FROM "MediaWorks" w
                    WHERE w."Id" = "MediaInstallments"."Id"),
                "CanonicalCoverUrl" = (
                    SELECT CASE
                        WHEN w."CoverSource" IN (2, 3) AND w."CoverUrl" IS NOT NULL THEN w."CoverUrl"
                        ELSE NULL
                    END
                    FROM "MediaWorks" w
                    WHERE w."Id" = "MediaInstallments"."Id"),
                "CanonicalCoverSource" = (
                    SELECT CASE
                        WHEN w."CoverSource" = 2 AND w."CoverUrl" IS NOT NULL THEN 1
                        WHEN w."CoverSource" = 3 AND w."CoverUrl" IS NOT NULL THEN 2
                        ELSE 0
                    END
                    FROM "MediaWorks" w
                    WHERE w."Id" = "MediaInstallments"."Id")
            WHERE EXISTS (
                SELECT 1
                FROM "InstallmentProviderIdentities" p
                JOIN "MediaWorks" legacyWork ON legacyWork."Id" = p."MediaInstallmentId"
                WHERE p."Provider" = 'jiten'
                  AND p."MediaInstallmentId" = "MediaInstallments"."Id");

            INSERT OR IGNORE INTO "InstallmentProviderSnapshots" (
                "Provider", "NormalizedKey", "Fingerprint", "Title",
                "CharacterCount", "CoverUrl", "CoverSource", "ReleaseState",
                "ReleaseDate", "ProviderOrder", "PayloadJson", "ObservedAtUtc",
                "IsComplete", "Version")
            SELECT
                p."Provider",
                p."NormalizedKey",
                'legacy:' || lower(w."Id"),
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
                0,
                w."Id"
            FROM "InstallmentProviderIdentities" p
            JOIN "MediaWorks" w ON w."Id" = p."MediaInstallmentId";
            """, cancellationToken);
            await context.Database.ExecuteSqlRawAsync("""
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_ImmersionLogs_TtsuBindingId_Date" ON "ImmersionLogs" ("TtsuBindingId", "Date");
            CREATE INDEX IF NOT EXISTS "IX_MediaWorks_MediaInstallmentId" ON "MediaWorks" ("MediaInstallmentId");
            CREATE INDEX IF NOT EXISTS "IX_MediaInstallments_MediaSeriesId_OrderKey" ON "MediaInstallments" ("MediaSeriesId", "OrderKey");
            CREATE INDEX IF NOT EXISTS "IX_InstallmentProviderIdentities_MediaInstallmentId" ON "InstallmentProviderIdentities" ("MediaInstallmentId");
            CREATE INDEX IF NOT EXISTS "IX_InstallmentProviderSnapshots_Provider_NormalizedKey_ObservedAtUtc" ON "InstallmentProviderSnapshots" ("Provider", "NormalizedKey", "ObservedAtUtc");
            CREATE INDEX IF NOT EXISTS "IX_JitenCatalogueRefreshReceipts_MediaSeriesId" ON "JitenCatalogueRefreshReceipts" ("MediaSeriesId");
            CREATE INDEX IF NOT EXISTS "IX_JitenFranchiseGraphNodeStates_MediaSeriesId" ON "JitenFranchiseGraphNodeStates" ("MediaSeriesId");
            CREATE INDEX IF NOT EXISTS "IX_JitenFranchiseTopologyReceipts_FranchiseId" ON "JitenFranchiseTopologyReceipts" ("FranchiseId");
            """, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    private static async Task<HashSet<string>> GetColumnsAsync(ImmersionDbContext context,
        IDbContextTransaction transaction, string table, CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = $"PRAGMA table_info('{table}')";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(reader.GetString(1));
        }
        return columns;
    }
}
