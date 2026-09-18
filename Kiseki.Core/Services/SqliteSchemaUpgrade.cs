using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Kiseki.Core.Services;

// Existing local databases were created with EnsureCreated, without a SQLite migration history.
// Keep this additive bridge separate from the authoritative PostgreSQL EF migrations.
public static class SqliteSchemaUpgrade
{
    public static async Task ApplyAsync(ImmersionDbContext context, CancellationToken cancellationToken = default)
    {
        await context.Database.EnsureCreatedAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
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
        await context.Database.ExecuteSqlRawAsync("""
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_ImmersionLogs_TtsuBindingId_Date" ON "ImmersionLogs" ("TtsuBindingId", "Date");
            """, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
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
