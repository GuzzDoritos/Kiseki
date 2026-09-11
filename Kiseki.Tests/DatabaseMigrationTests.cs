using Kiseki.Core;
using Kiseki.Core.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Kiseki.Tests;

public class DatabaseMigrationTests
{
    [Fact]
    public void Migration_HasRegisteredInitialPostgreSqlMigration()
    {
        var factory = new ImmersionDbContextFactory();
        using var context = factory.CreateDbContext([]);

        var migrations = context.Database.GetMigrations().ToList();

        Assert.Contains("20260911135026_InitialPostgreSql", migrations);
    }

    [Fact]
    public async Task Model_DefaultsNewWorksToBooks()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ImmersionDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new ImmersionDbContext(options);
        await context.Database.EnsureCreatedAsync();

        var work = new MediaWork("Test Book");
        context.MediaWorks.Add(work);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var savedWork = await context.MediaWorks.SingleAsync();
        Assert.Equal(MediaType.Book, savedWork.MediaType);
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
        await context.Database.EnsureCreatedAsync();

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
