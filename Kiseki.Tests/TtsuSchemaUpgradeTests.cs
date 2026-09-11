using Kiseki.Core.Entities;
using Kiseki.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Kiseki.Tests;

public sealed class TtsuSchemaUpgradeTests
{
    [Fact]
    public async Task ExistingSqliteDatabase_AdditiveUpgradePreservesDuplicatesAndOrphans()
    {
        await using var db = await ImportDatabase.CreateAsync();
        var work = new MediaWork("Legacy book");
        db.Context.Add(work);
        await db.Context.SaveChangesAsync();
        // Reproduce the previous local schema, keeping the remaining library tables intact.
        await db.Context.Database.ExecuteSqlRawAsync("""
            DROP TABLE "ImmersionLogs";
            DROP TABLE "TtsuBindings";
            DROP TABLE "TtsuImportReceipts";
            CREATE TABLE "ImmersionLogs" (
                "Id" TEXT NOT NULL PRIMARY KEY, "Date" TEXT NOT NULL,
                "CharactersRead" INTEGER NOT NULL, "TimeSpentMinutes" REAL NOT NULL,
                "Source" TEXT NOT NULL, "MediaWorkId" TEXT NULL REFERENCES "MediaWorks" ("Id"));
            """);
        var first = Guid.NewGuid(); var second = Guid.NewGuid(); var orphan = Guid.NewGuid();
        foreach (var id in new[] { first, second, orphan })
        {
            Guid? target = id == orphan ? null : work.Id;
            await db.Context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"ImmersionLogs\" VALUES ({id}, {"2026-09-01"}, {100}, {1d}, {"ttsu"}, {target})");
        }
        await SqliteSchemaUpgrade.ApplyAsync(db.Context);
        await SqliteSchemaUpgrade.ApplyAsync(db.Context);
        db.Context.ChangeTracker.Clear();
        var logs = await db.Context.ImmersionLogs.ToListAsync();
        Assert.Equal(3, logs.Count);
        Assert.All(logs, x => { Assert.Null(x.SourceRevision); Assert.Null(x.TtsuBindingId); });
        Assert.Equal(2, logs.Count(x => x.MediaWorkId == work.Id));
        Assert.Single(await db.Service.GetOrphansAsync());
        Assert.False((await db.Service.PreviewAsync(TtsuMergeServiceTests.Book(), work.Id)).CanApply);
    }

    [Fact]
    public async Task BoundDailyRows_EnforceWorkSourceAndUniqueness()
    {
        await using var db = await ImportDatabase.CreateAsync();
        await TtsuMergeServiceTests.Apply(db.Service, TtsuMergeServiceTests.Book(TtsuMergeServiceTests.Entry(100, 1)));
        var workId = (await db.Context.MediaWorks.SingleAsync()).Id;
        db.Context.ImmersionLogs.Add(new() { MediaWorkId = workId, TtsuBindingId = workId, Date = new(2026, 9, 1) });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.Context.SaveChangesAsync());
        db.Context.ChangeTracker.Clear();
        db.Context.ImmersionLogs.Add(new() { MediaWorkId = workId, TtsuBindingId = workId, Date = new(2026, 9, 2), Source = "manual" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.Context.SaveChangesAsync());
        db.Context.ChangeTracker.Clear();
        var another = new MediaWork("Another"); db.Context.Add(another); await db.Context.SaveChangesAsync();
        db.Context.ImmersionLogs.Add(new() { MediaWorkId = another.Id, TtsuBindingId = workId, Date = new(2026, 9, 2) });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.Context.SaveChangesAsync());
    }
}
