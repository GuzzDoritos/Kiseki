using Kiseki.Core;
using Kiseki.Core.DTOs;
using Kiseki.Core.Entities;
using Kiseki.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Kiseki.Tests;

public sealed class SeriesServiceTests
{
    [Fact]
    public async Task CreateManualSeriesAsync_Throws_WhenTitleIsEmpty()
    {
        await using var db = await TestDatabase.CreateAsync();
        var service = new SeriesService(db.Context, new StubJitenApiClient());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateManualSeriesAsync(string.Empty));
    }

    [Fact]
    public async Task CreateManualSeriesAsync_CreatesAndPersistsEmptySeries()
    {
        await using var db = await TestDatabase.CreateAsync();
        var service = new SeriesService(db.Context, new StubJitenApiClient());

        var series = await service.CreateManualSeriesAsync("Aobuta Series", MediaType.Book);

        Assert.NotNull(series);
        Assert.Equal("Aobuta Series", series.Title);
        Assert.Equal(MediaType.Book, series.MediaType);
        Assert.Empty(series.Installments);

        db.Context.ChangeTracker.Clear();
        var persisted = await db.Context.MediaSeries
            .Include(s => s.Installments)
            .SingleAsync(s => s.Id == series.Id);

        Assert.Equal("Aobuta Series", persisted.Title);
        Assert.Empty(persisted.Installments);
    }

    [Fact]
    public async Task CreateSeriesFromJitenDeckAsync_CreatesInstallmentsFromSubdecks()
    {
        await using var db = await TestDatabase.CreateAsync();
        var client = new StubJitenApiClient
        {
            Detail = CreateMultiVolumeDetail(10, 3)
        };
        var service = new SeriesService(db.Context, client);

        var series = await service.CreateSeriesFromJitenDeckAsync(10);

        Assert.NotNull(series);
        Assert.Equal("Spice and Wolf", series.Title);
        Assert.Equal(10, series.JitenDeckId);
        Assert.Equal("https://cdn.jiten.moe/spice.jpg", series.CoverUrl);
        Assert.Equal(3, series.Installments.Count);

        Assert.Equal(1, series.Installments[0].SequenceNumber);
        Assert.Equal("Volume 1", series.Installments[0].Title);
        Assert.Equal(101, series.Installments[0].JitenSubdeckId);
        Assert.Equal(100_000, series.Installments[0].JitenCharacterCount);
        Assert.Equal("https://cdn.jiten.moe/vol1.jpg", series.Installments[0].CoverUrl);

        Assert.Equal(2, series.Installments[1].SequenceNumber);
        Assert.Equal(102, series.Installments[1].JitenSubdeckId);

        Assert.Equal(3, series.Installments[2].SequenceNumber);
        Assert.Equal(103, series.Installments[2].JitenSubdeckId);
    }

    [Fact]
    public async Task CreateSeriesFromJitenDeckAsync_AutoMatchesExistingMediaWorks_BySubdeckId()
    {
        await using var db = await TestDatabase.CreateAsync();

        // Existing library work linked to subdeck 101
        var work1 = new MediaWork("Spice & Wolf 1");
        work1.LinkToJitenSubdeck(10, 101, 100_000);
        db.Context.MediaWorks.Add(work1);

        // Another library work linked to subdeck 103
        var work3 = new MediaWork("Spice & Wolf 3");
        work3.LinkToJitenSubdeck(10, 103, 110_000);
        db.Context.MediaWorks.Add(work3);

        // Unrelated work
        var unrelatedWork = new MediaWork("Unrelated Book");
        db.Context.MediaWorks.Add(unrelatedWork);

        await db.Context.SaveChangesAsync();

        var client = new StubJitenApiClient
        {
            Detail = CreateMultiVolumeDetail(10, 3)
        };
        var service = new SeriesService(db.Context, client);

        var series = await service.CreateSeriesFromJitenDeckAsync(10);

        // Vol 1 was matched
        Assert.Equal(work1.Id, series.Installments[0].MediaWorkId);
        Assert.Equal(series.Id, work1.MediaSeriesId);

        // Vol 2 was unlinked
        Assert.Null(series.Installments[1].MediaWorkId);

        // Vol 3 was matched
        Assert.Equal(work3.Id, series.Installments[2].MediaWorkId);
        Assert.Equal(series.Id, work3.MediaSeriesId);

        // Unrelated work was untouched
        Assert.Null(unrelatedWork.MediaSeriesId);
    }

    [Fact]
    public async Task CreateSeriesFromJitenDeckAsync_AutoMatchesExistingMediaWorks_ByDirectDeckId()
    {
        await using var db = await TestDatabase.CreateAsync();

        // Work linked directly to deck 101 (without subdeckId)
        var work = new MediaWork("Standalone Volume 1");
        work.LinkToJitenDeck(101, 100_000);
        db.Context.MediaWorks.Add(work);
        await db.Context.SaveChangesAsync();

        var client = new StubJitenApiClient
        {
            Detail = CreateMultiVolumeDetail(10, 2)
        };
        var service = new SeriesService(db.Context, client);

        var series = await service.CreateSeriesFromJitenDeckAsync(10);

        // Installment with subdeckId 101 matches work with JitenDeckId = 101
        Assert.Equal(work.Id, series.Installments[0].MediaWorkId);
        Assert.Equal(series.Id, work.MediaSeriesId);
    }

    [Fact]
    public async Task CreateSeriesFromJitenDeckAsync_CreatesSingleInstallment_WhenNoSubdecksExist()
    {
        await using var db = await TestDatabase.CreateAsync();

        var client = new StubJitenApiClient
        {
            Detail = new JitenDeckDetailDTO
            {
                MainDeck = new JitenDeckDTO
                {
                    DeckId = 50,
                    OriginalTitle = "Standalone Novel",
                    CharacterCount = 150_000,
                    CoverName = "https://cdn.jiten.moe/novel.jpg"
                },
                SubDecks = []
            }
        };
        var service = new SeriesService(db.Context, client);

        var series = await service.CreateSeriesFromJitenDeckAsync(50);

        Assert.Equal("Standalone Novel", series.Title);
        Assert.Single(series.Installments);
        Assert.Equal(1, series.Installments[0].SequenceNumber);
        Assert.Equal("Standalone Novel", series.Installments[0].Title);
        Assert.Equal(50, series.Installments[0].JitenSubdeckId);
        Assert.Equal(150_000, series.Installments[0].JitenCharacterCount);
    }

    [Fact]
    public async Task GetSeriesDetailsAsync_ReturnsSeriesWithOrderedInstallmentsAndWorks()
    {
        await using var db = await TestDatabase.CreateAsync();
        var series = new MediaSeries("Spice and Wolf", MediaType.Book);
        var work = new MediaWork("Volume 1");
        work.Logs.Add(new ImmersionLog { CharactersRead = 20_000 });
        db.Context.MediaWorks.Add(work);

        var inst2 = new SeriesInstallment(series.Id, 2, "Volume 2", 102, 100_000);
        var inst1 = new SeriesInstallment(series.Id, 1, "Volume 1", 101, 100_000)
        {
            MediaWorkId = work.Id
        };
        series.Installments.AddRange([inst2, inst1]);
        db.Context.MediaSeries.Add(series);
        await db.Context.SaveChangesAsync();

        var service = new SeriesService(db.Context, new StubJitenApiClient());
        var details = await service.GetSeriesDetailsAsync(series.Id);

        Assert.NotNull(details);
        Assert.Equal(2, details.Installments.Count);
        // Ordered by sequence number
        Assert.Equal(1, details.Installments[0].SequenceNumber);
        Assert.Equal("Volume 1", details.Installments[0].Title);
        Assert.NotNull(details.Installments[0].MediaWork);
        Assert.Equal(20_000, details.Installments[0].MediaWork!.CurrentCharactersRead);
        Assert.Equal(2, details.Installments[1].SequenceNumber);
    }

    [Fact]
    public async Task GetAvailableLibraryWorksAsync_ReturnsUnassignedWorksOnly()
    {
        await using var db = await TestDatabase.CreateAsync();
        var series = new MediaSeries("Spice and Wolf", MediaType.Book);
        var assignedWork = new MediaWork("Volume 1");
        var unassignedWork = new MediaWork("Volume 2");
        db.Context.MediaWorks.AddRange([assignedWork, unassignedWork]);

        var inst = new SeriesInstallment(series.Id, 1, "Volume 1", 101, 100_000)
        {
            MediaWorkId = assignedWork.Id
        };
        series.Installments.Add(inst);
        db.Context.MediaSeries.Add(series);
        await db.Context.SaveChangesAsync();

        var service = new SeriesService(db.Context, new StubJitenApiClient());
        var available = await service.GetAvailableLibraryWorksAsync(series.Id);

        Assert.Single(available);
        Assert.Equal(unassignedWork.Id, available[0].Id);
        Assert.Equal("Volume 2", available[0].Title);
    }

    [Fact]
    public async Task LinkAndUnlinkWorkFromInstallmentAsync_UpdatesRelationshipsCorrectly()
    {
        await using var db = await TestDatabase.CreateAsync();
        var series = new MediaSeries("Spice and Wolf", MediaType.Book);
        var inst = new SeriesInstallment(series.Id, 1, "Volume 1", 101, 100_000);
        series.Installments.Add(inst);
        db.Context.MediaSeries.Add(series);

        var work = new MediaWork("Volume 1 Work");
        db.Context.MediaWorks.Add(work);
        await db.Context.SaveChangesAsync();

        var service = new SeriesService(db.Context, new StubJitenApiClient());

        // Link
        await service.LinkWorkToInstallmentAsync(inst.Id, work.Id);

        db.Context.ChangeTracker.Clear();
        var linkedInst = await db.Context.SeriesInstallments.Include(i => i.MediaWork).SingleAsync(i => i.Id == inst.Id);
        var linkedWork = await db.Context.MediaWorks.SingleAsync(w => w.Id == work.Id);
        Assert.Equal(work.Id, linkedInst.MediaWorkId);
        Assert.Equal(series.Id, linkedWork.MediaSeriesId);

        // Unlink
        await service.UnlinkWorkFromInstallmentAsync(inst.Id);

        db.Context.ChangeTracker.Clear();
        var unlinkedInst = await db.Context.SeriesInstallments.SingleAsync(i => i.Id == inst.Id);
        var unlinkedWork = await db.Context.MediaWorks.SingleAsync(w => w.Id == work.Id);
        Assert.Null(unlinkedInst.MediaWorkId);
        Assert.Null(unlinkedWork.MediaSeriesId);
    }

    [Fact]
    public async Task AddInstallmentAsync_AppendsSlotWithNextSequenceNumber_AndAutoMatchesWork()
    {
        await using var db = await TestDatabase.CreateAsync();
        var series = new MediaSeries("Spice and Wolf", MediaType.Book);
        var inst1 = new SeriesInstallment(series.Id, 1, "Volume 1", 101, 100_000);
        series.Installments.Add(inst1);
        db.Context.MediaSeries.Add(series);

        var work2 = new MediaWork("Volume 2 Work");
        work2.LinkToJitenSubdeck(10, 102, 110_000);
        db.Context.MediaWorks.Add(work2);
        await db.Context.SaveChangesAsync();

        var service = new SeriesService(db.Context, new StubJitenApiClient());
        var added = await service.AddInstallmentAsync(series.Id, "Volume 2", 110_000, jitenSubdeckId: 102);

        Assert.Equal(2, added.SequenceNumber);
        Assert.Equal("Volume 2", added.Title);
        Assert.Equal(work2.Id, added.MediaWorkId);

        db.Context.ChangeTracker.Clear();
        var reloadedWork = await db.Context.MediaWorks.SingleAsync(w => w.Id == work2.Id);
        Assert.Equal(series.Id, reloadedWork.MediaSeriesId);
    }

    [Fact]
    public async Task RemoveInstallmentAsync_DeletesSlotAndClearsWorkSeriesId()
    {
        await using var db = await TestDatabase.CreateAsync();
        var series = new MediaSeries("Spice and Wolf", MediaType.Book);
        var work = new MediaWork("Volume 1");
        work.MediaSeriesId = series.Id;
        db.Context.MediaWorks.Add(work);

        var inst = new SeriesInstallment(series.Id, 1, "Volume 1", 101, 100_000)
        {
            MediaWorkId = work.Id
        };
        series.Installments.Add(inst);
        db.Context.MediaSeries.Add(series);
        await db.Context.SaveChangesAsync();

        var service = new SeriesService(db.Context, new StubJitenApiClient());
        await service.RemoveInstallmentAsync(inst.Id);

        db.Context.ChangeTracker.Clear();
        Assert.Empty(await db.Context.SeriesInstallments.ToListAsync());
        var reloadedWork = await db.Context.MediaWorks.SingleAsync(w => w.Id == work.Id);
        Assert.Null(reloadedWork.MediaSeriesId);
    }

    [Fact]
    public async Task GetAllSeriesAsync_ReturnsAllSeries_OrderedByTitle()
    {
        await using var db = await TestDatabase.CreateAsync();
        var seriesB = new MediaSeries("Spice and Wolf", MediaType.Book);
        var seriesA = new MediaSeries("86 - Eighty Six", MediaType.Book);
        var seriesC = new MediaSeries("Steins;Gate", MediaType.Game);
        db.Context.MediaSeries.AddRange(seriesB, seriesA, seriesC);
        await db.Context.SaveChangesAsync();

        var service = new SeriesService(db.Context, new StubJitenApiClient());
        var result = await service.GetAllSeriesAsync();

        Assert.Equal(3, result.Count);
        Assert.Equal("86 - Eighty Six", result[0].Title);
        Assert.Equal("Spice and Wolf", result[1].Title);
        Assert.Equal("Steins;Gate", result[2].Title);
    }

    [Fact]
    public async Task GetAllSeriesAsync_FiltersByMediaType()
    {
        await using var db = await TestDatabase.CreateAsync();
        var seriesBook = new MediaSeries("Spice and Wolf", MediaType.Book);
        var seriesGame = new MediaSeries("Steins;Gate", MediaType.Game);
        db.Context.MediaSeries.AddRange(seriesBook, seriesGame);
        await db.Context.SaveChangesAsync();

        var service = new SeriesService(db.Context, new StubJitenApiClient());
        var result = await service.GetAllSeriesAsync(mediaType: MediaType.Game);

        Assert.Single(result);
        Assert.Equal("Steins;Gate", result[0].Title);
    }

    [Fact]
    public async Task GetAllSeriesAsync_FiltersBySearch()
    {
        await using var db = await TestDatabase.CreateAsync();
        var series1 = new MediaSeries("Spice and Wolf", MediaType.Book);
        var series2 = new MediaSeries("Wolf Children", MediaType.Book);
        var series3 = new MediaSeries("Overlord", MediaType.Book);
        db.Context.MediaSeries.AddRange(series1, series2, series3);
        await db.Context.SaveChangesAsync();

        var service = new SeriesService(db.Context, new StubJitenApiClient());
        var result = await service.GetAllSeriesAsync(search: "wolf");

        Assert.Equal(2, result.Count);
        Assert.Contains(result, s => s.Title == "Spice and Wolf");
        Assert.Contains(result, s => s.Title == "Wolf Children");
    }

    [Fact]
    public async Task GetAllSeriesAsync_SortsInstallmentsBySequenceNumber()
    {
        await using var db = await TestDatabase.CreateAsync();
        var series = new MediaSeries("Spice and Wolf", MediaType.Book);
        var inst2 = new SeriesInstallment(series.Id, sequenceNumber: 2, title: "Vol 2", jitenCharacterCount: 50_000);
        var inst1 = new SeriesInstallment(series.Id, sequenceNumber: 1, title: "Vol 1", jitenCharacterCount: 50_000);
        series.Installments.Add(inst2);
        series.Installments.Add(inst1);
        db.Context.MediaSeries.Add(series);
        await db.Context.SaveChangesAsync();

        var service = new SeriesService(db.Context, new StubJitenApiClient());
        var result = await service.GetAllSeriesAsync();

        Assert.Single(result);
        Assert.Equal(1, result[0].Installments[0].SequenceNumber);
        Assert.Equal(2, result[0].Installments[1].SequenceNumber);
    }

    private static JitenDeckDetailDTO CreateMultiVolumeDetail(int parentDeckId, int volumeCount)
    {
        var subdecks = new List<JitenDeckDTO>();
        for (var i = 1; i <= volumeCount; i++)
        {
            subdecks.Add(new JitenDeckDTO
            {
                DeckId = parentDeckId * 10 + i,
                OriginalTitle = $"Volume {i}",
                CharacterCount = 100_000,
                CoverName = $"https://cdn.jiten.moe/vol{i}.jpg"
            });
        }

        return new JitenDeckDetailDTO
        {
            MainDeck = new JitenDeckDTO
            {
                DeckId = parentDeckId,
                OriginalTitle = "Spice and Wolf",
                CharacterCount = 100_000 * volumeCount,
                CoverName = "https://cdn.jiten.moe/spice.jpg",
                ChildrenDeckCount = volumeCount
            },
            SubDecks = subdecks
        };
    }

    private sealed class StubJitenApiClient : IJitenApiClient
    {
        public JitenDeckDetailDTO? Detail { get; init; }

        public Task<IReadOnlyList<JitenDeckDTO>> SearchBooksAsync(string query, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<JitenDeckDTO>>([]);

        public Task<JitenDeckDetailDTO?> GetDeckDetailAsync(int deckId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Detail);

        public Task<JitenFranchiseDTO?> GetFranchiseAsync(int deckId, CancellationToken cancellationToken = default) =>
            Task.FromResult<JitenFranchiseDTO?>(null);
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

