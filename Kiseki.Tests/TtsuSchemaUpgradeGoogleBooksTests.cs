using Kiseki.Core.Entities;
using Kiseki.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Kiseki.Tests;

public sealed class TtsuSchemaUpgradeGoogleBooksTests
{
    [Fact]
    public async Task ExistingSqliteDatabase_AdditiveUpgradeAddsCoverProviderItemId_AndIsIdempotent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<Kiseki.Core.ImmersionDbContext>()
            .UseSqlite(connection)
            .Options;

        var workId = Guid.NewGuid();

        await using (var context = new Kiseki.Core.ImmersionDbContext(options))
        {
            await context.Database.EnsureCreatedAsync();
            // Drop foreign key child tables and MediaWorks, then recreate MediaWorks without CoverProviderItemId to simulate legacy schema
            await context.Database.ExecuteSqlRawAsync("""
                DROP TABLE "ImmersionLogs";
                DROP TABLE "TtsuBindings";
                DROP TABLE "TtsuImportReceipts";
                DROP TABLE "MediaWorks";
                CREATE TABLE "MediaWorks" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_MediaWorks" PRIMARY KEY,
                    "Title" TEXT NOT NULL,
                    "MediaType" INTEGER NOT NULL DEFAULT 0,
                    "CoverUrl" TEXT NULL,
                    "CoverSource" INTEGER NOT NULL DEFAULT 0,
                    "IsCompleted" INTEGER NOT NULL DEFAULT 0,
                    "ManualCharacterCountOverride" INTEGER NULL,
                    "TtsuCharacterCount" INTEGER NULL,
                    "JitenDeckId" INTEGER NULL,
                    "JitenSubdeckId" INTEGER NULL,
                    "JitenCharacterCount" INTEGER NULL,
                    "MediaSeriesId" TEXT NULL
                );
                CREATE TABLE "TtsuBindings" (
                    "MediaWorkId" TEXT NOT NULL CONSTRAINT "PK_TtsuBindings" PRIMARY KEY REFERENCES "MediaWorks" ("Id") ON DELETE CASCADE,
                    "OriginalTitle" TEXT NOT NULL, "FolderHint" TEXT NULL, "Version" TEXT NOT NULL
                );
                CREATE TABLE "TtsuImportReceipts" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_TtsuImportReceipts" PRIMARY KEY,
                    "Books" INTEGER NOT NULL, "AddedDays" INTEGER NOT NULL, "UpdatedDays" INTEGER NOT NULL,
                    "UnchangedDays" INTEGER NOT NULL, "StaleDays" INTEGER NOT NULL
                );
                CREATE TABLE "ImmersionLogs" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_ImmersionLogs" PRIMARY KEY,
                    "Date" TEXT NOT NULL, "CharactersRead" INTEGER NOT NULL, "TimeSpentMinutes" REAL NOT NULL,
                    "Source" TEXT NOT NULL, "MediaWorkId" TEXT NULL REFERENCES "MediaWorks" ("Id")
                );
                """);

            await context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO \"MediaWorks\" (\"Id\", \"Title\", \"MediaType\", \"CoverSource\", \"CoverUrl\", \"IsCompleted\") VALUES ({workId}, 'Legacy Work', 0, 1, 'https://cdn.jiten.moe/legacy.jpg', 0)");
        }

        // Run additive upgrade once
        await using (var context = new Kiseki.Core.ImmersionDbContext(options))
        {
            await SqliteSchemaUpgrade.ApplyAsync(context);
        }

        // Run additive upgrade a second time (idempotence)
        await using (var context = new Kiseki.Core.ImmersionDbContext(options))
        {
            await SqliteSchemaUpgrade.ApplyAsync(context);
        }

        // Verify loaded entity preserves legacy state and has null CoverProviderItemId
        await using (var context = new Kiseki.Core.ImmersionDbContext(options))
        {
            var loaded = await context.MediaWorks.SingleAsync(w => w.Id == workId);
            Assert.Equal("Legacy Work", loaded.Title);
            Assert.Equal("https://cdn.jiten.moe/legacy.jpg", loaded.CoverUrl);
            Assert.Equal(MediaCoverSource.LegacyUnknown, loaded.CoverSource);
            Assert.Null(loaded.CoverProviderItemId);

            // Add a new work with Google Books cover to verify schema accepts it
            var googleWork = new MediaWork("New Google Work");
            googleWork.ApplyGoogleBooksCover("https://books.google.com/cover.jpg", "vol_123");
            context.MediaWorks.Add(googleWork);
            await context.SaveChangesAsync();
        }

        // Verify newly added work with Google Books cover is loaded properly
        await using (var context = new Kiseki.Core.ImmersionDbContext(options))
        {
            var loadedGoogle = await context.MediaWorks.SingleAsync(w => w.Title == "New Google Work");
            Assert.Equal("https://books.google.com/cover.jpg", loadedGoogle.CoverUrl);
            Assert.Equal(MediaCoverSource.GoogleBooks, loadedGoogle.CoverSource);
            Assert.Equal("vol_123", loadedGoogle.CoverProviderItemId);
        }
    }
}
