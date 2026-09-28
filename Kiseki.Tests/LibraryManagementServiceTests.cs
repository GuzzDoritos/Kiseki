using Kiseki.Core;
using Kiseki.Core.Entities;
using Kiseki.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Kiseki.Tests;

public sealed class LibraryManagementServiceTests
{
    [Fact]
    public async Task DeleteMediaWorkAsync_Throws_WhenWorkNotFound()
    {
        await using var db = await TestDatabase.CreateAsync();
        var service = new LibraryManagementService(db.Context);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteMediaWorkAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task DeleteMediaWorkAsync_RemovesWorkAndLogs()
    {
        await using var db = await TestDatabase.CreateAsync();
        var work = new MediaWork("Test Book", mediaType: MediaType.Book);
        var log1 = new ImmersionLog
        {
            MediaWorkId = work.Id,
            Date = new DateOnly(2026, 1, 1),
            CharactersRead = 1000,
            TimeSpentMinutes = 20.0
        };
        var log2 = new ImmersionLog
        {
            MediaWorkId = work.Id,
            Date = new DateOnly(2026, 1, 2),
            CharactersRead = 2000,
            TimeSpentMinutes = 40.0
        };
        work.Logs.Add(log1);
        work.Logs.Add(log2);

        db.Context.MediaWorks.Add(work);
        await db.Context.SaveChangesAsync();

        var service = new LibraryManagementService(db.Context);
        await service.DeleteMediaWorkAsync(work.Id);

        db.Context.ChangeTracker.Clear();

        Assert.Null(await db.Context.MediaWorks.FindAsync(work.Id));
        Assert.Empty(await db.Context.ImmersionLogs.Where(l => l.MediaWorkId == work.Id).ToListAsync());
    }

    [Fact]
    public async Task DeleteMediaWorkAsync_RemovesTtsuBindingAndLogs_WithoutFkViolation()
    {
        await using var db = await TestDatabase.CreateAsync();
        var work = new MediaWork("TTSU Book", mediaType: MediaType.Book);
        var binding = new TtsuBinding
        {
            MediaWorkId = work.Id,
            OriginalTitle = "TTSU Book"
        };
        var log = new ImmersionLog
        {
            MediaWorkId = work.Id,
            Date = new DateOnly(2026, 1, 1),
            CharactersRead = 1500,
            TimeSpentMinutes = 25.0,
            TtsuBindingId = binding.MediaWorkId
        };
        work.Logs.Add(log);

        db.Context.MediaWorks.Add(work);
        db.Context.TtsuBindings.Add(binding);
        await db.Context.SaveChangesAsync();

        var service = new LibraryManagementService(db.Context);
        await service.DeleteMediaWorkAsync(work.Id);

        db.Context.ChangeTracker.Clear();

        Assert.Null(await db.Context.MediaWorks.FindAsync(work.Id));
        Assert.Null(await db.Context.TtsuBindings.FindAsync(binding.MediaWorkId));
        Assert.Empty(await db.Context.ImmersionLogs.Where(l => l.MediaWorkId == work.Id).ToListAsync());
    }

    [Fact]
    public async Task DeleteMediaWorkAsync_UnlinksSeriesInstallments_PreservesInstallmentSlot()
    {
        await using var db = await TestDatabase.CreateAsync();
        var series = new MediaSeries("Spice and Wolf", MediaType.Book);
        var installment = new SeriesInstallment(series.Id, sequenceNumber: 1, title: "Vol 1", jitenCharacterCount: 50_000);
        series.Installments.Add(installment);

        var work = new MediaWork("Spice and Wolf Vol 1", mediaType: MediaType.Book)
        {
            MediaSeriesId = series.Id
        };
        installment.MediaWorkId = work.Id;
        installment.MediaWork = work;

        db.Context.MediaSeries.Add(series);
        db.Context.MediaWorks.Add(work);
        await db.Context.SaveChangesAsync();

        var service = new LibraryManagementService(db.Context);
        await service.DeleteMediaWorkAsync(work.Id);

        db.Context.ChangeTracker.Clear();

        // Work deleted
        Assert.Null(await db.Context.MediaWorks.FindAsync(work.Id));

        // Installment preserved, but unlinked
        var persistedInstallment = await db.Context.SeriesInstallments
            .Include(i => i.MediaWork)
            .SingleAsync(i => i.Id == installment.Id);

        Assert.NotNull(persistedInstallment);
        Assert.Null(persistedInstallment.MediaWorkId);
        Assert.Null(persistedInstallment.MediaWork);
        Assert.Equal("Vol 1", persistedInstallment.Title);
    }

    [Fact]
    public async Task DeleteImmersionLogAsync_Throws_WhenLogNotFound()
    {
        await using var db = await TestDatabase.CreateAsync();
        var service = new LibraryManagementService(db.Context);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteImmersionLogAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task DeleteImmersionLogAsync_Throws_WhenBelongsToDifferentWork()
    {
        await using var db = await TestDatabase.CreateAsync();
        var work1 = new MediaWork("Book 1", mediaType: MediaType.Book);
        var work2 = new MediaWork("Book 2", mediaType: MediaType.Book);
        var log = new ImmersionLog
        {
            MediaWorkId = work1.Id,
            Date = new DateOnly(2026, 1, 1),
            CharactersRead = 1000,
            TimeSpentMinutes = 20.0
        };
        work1.Logs.Add(log);

        db.Context.MediaWorks.AddRange(work1, work2);
        await db.Context.SaveChangesAsync();

        var service = new LibraryManagementService(db.Context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteImmersionLogAsync(log.Id, mediaWorkId: work2.Id));

        Assert.Contains(work2.Id.ToString(), ex.Message);
    }

    [Fact]
    public async Task DeleteImmersionLogAsync_DeletesLog_UpdatesCharactersRead()
    {
        await using var db = await TestDatabase.CreateAsync();
        var work = new MediaWork("Book 1", mediaType: MediaType.Book);
        var log1 = new ImmersionLog
        {
            MediaWorkId = work.Id,
            Date = new DateOnly(2026, 1, 1),
            CharactersRead = 1000,
            TimeSpentMinutes = 20.0
        };
        var log2 = new ImmersionLog
        {
            MediaWorkId = work.Id,
            Date = new DateOnly(2026, 1, 2),
            CharactersRead = 2500,
            TimeSpentMinutes = 40.0
        };
        work.Logs.Add(log1);
        work.Logs.Add(log2);

        db.Context.MediaWorks.Add(work);
        await db.Context.SaveChangesAsync();

        var service = new LibraryManagementService(db.Context);
        await service.DeleteImmersionLogAsync(log1.Id, mediaWorkId: work.Id);

        db.Context.ChangeTracker.Clear();

        var persistedWork = await db.Context.MediaWorks
            .Include(w => w.Logs)
            .SingleAsync(w => w.Id == work.Id);

        Assert.Single(persistedWork.Logs);
        Assert.Equal(log2.Id, persistedWork.Logs.First().Id);
        Assert.Equal(2500, persistedWork.CurrentCharactersRead);
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private TestDatabase(SqliteConnection connection, ImmersionDbContext context)
        {
            _connection = connection;
            Context = context;
        }

        public ImmersionDbContext Context { get; }

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ImmersionDbContext>()
                .UseSqlite(connection)
                .Options;
            var context = new ImmersionDbContext(options);
            await context.Database.EnsureCreatedAsync();
            return new TestDatabase(connection, context);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
