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
                CONSTRAINT "FK_TtsuBindings_MediaWorks_MediaWorkId" FOREIGN KEY ("MediaWorkId") REFERENCES "MediaWorks" ("Id") ON DELETE CASCADE);
            CREATE TABLE IF NOT EXISTS "TtsuImportReceipts" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_TtsuImportReceipts" PRIMARY KEY,
                "Books" INTEGER NOT NULL, "AddedDays" INTEGER NOT NULL, "UpdatedDays" INTEGER NOT NULL,
                "UnchangedDays" INTEGER NOT NULL, "StaleDays" INTEGER NOT NULL);
            """, cancellationToken);
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using (var command = context.Database.GetDbConnection().CreateCommand())
        {
            command.Transaction = transaction.GetDbTransaction();
            command.CommandText = "PRAGMA table_info('ImmersionLogs')";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) columns.Add(reader.GetString(1));
        }
        if (!columns.Contains("TtsuBindingId"))
            await context.Database.ExecuteSqlRawAsync("""
                ALTER TABLE "ImmersionLogs" ADD COLUMN "TtsuBindingId" TEXT NULL
                    REFERENCES "TtsuBindings" ("MediaWorkId") ON DELETE RESTRICT
                    CONSTRAINT "CK_ImmersionLogs_TtsuBinding" CHECK (
                        "TtsuBindingId" IS NULL OR ("MediaWorkId" IS NOT NULL AND "TtsuBindingId" = "MediaWorkId" AND "Source" = 'ttsu'));
                """, cancellationToken);
        if (!columns.Contains("SourceRevision"))
            await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"ImmersionLogs\" ADD COLUMN \"SourceRevision\" INTEGER NULL;", cancellationToken);
        await context.Database.ExecuteSqlRawAsync("""
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_ImmersionLogs_TtsuBindingId_Date" ON "ImmersionLogs" ("TtsuBindingId", "Date");
            """, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
