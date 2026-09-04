using Kiseki.Core;
using Kiseki.Core.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Kiseki.Tests;

public class DatabaseMigrationTests
{
    [Fact]
    public async Task Migration_PreservesExistingWorksAndDefaultsThemToBooks()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ImmersionDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new ImmersionDbContext(options);
        var migrator = context.Database.GetService<IMigrator>();

        await migrator.MigrateAsync("20260813204355_NewTable");

        var workId = Guid.NewGuid();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO MediaWorks
                (Id, Title, JitenDeckId, JitenCharacterCount,
                 ManualCharacterCountOverride, IsCompleted)
            VALUES
                ({workId}, {"Existing book"}, {95367}, {109474}, NULL, {false})
            """);

        await migrator.MigrateAsync();
        context.ChangeTracker.Clear();

        var migratedWork = await context.MediaWorks.SingleAsync();

        Assert.Equal("Existing book", migratedWork.Title);
        Assert.Equal(MediaType.Book, migratedWork.MediaType);
        Assert.Equal(95367, migratedWork.JitenDeckId);
        Assert.Null(migratedWork.JitenSubdeckId);
        Assert.Null(migratedWork.JitenCoverUrl);
        Assert.Null(migratedWork.MediaSeriesId);
        Assert.Equal(109_474, migratedWork.TotalCharacters);
    }

    [Fact]
    public async Task DeletingSeries_DoesNotDeleteItsWorks()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ImmersionDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new ImmersionDbContext(options);
        await context.Database.MigrateAsync();

        var franchise = new Franchise("Re:Zero", 54904);
        var series = new MediaSeries("Re:Zero Light Novels", MediaType.Book)
        {
            Franchise = franchise,
            JitenDeckId = 54904
        };
        var work = new MediaWork("Re:Zero volume 1")
        {
            MediaSeries = series
        };

        context.MediaWorks.Add(work);
        await context.SaveChangesAsync();

        context.MediaSeries.Remove(series);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var preservedWork = await context.MediaWorks.SingleAsync();
        Assert.Null(preservedWork.MediaSeriesId);
        Assert.Equal(MediaType.Book, preservedWork.MediaType);
        Assert.Single(await context.Franchises.ToListAsync());
    }
}
