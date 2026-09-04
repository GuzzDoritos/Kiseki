using Kiseki.Core;
using Kiseki.Core.DTOs;
using Kiseki.Core.Services;
using Kiseki.Web.Pages.Import;
using Kiseki.Web.Services;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Kiseki.Tests;

public sealed class TtsuImportPageTests
{
    [Fact]
    public async Task Preview_ParsesBooksWithoutWritingToDatabase()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var fixtureStream = File.OpenRead(GetFixturePath());
        var model = CreateModel(database.Context);
        model.FolderFiles = [StatisticsFile(fixtureStream)];

        var result = await model.OnPostPreviewAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.True(model.HasPreview);
        Assert.Single(model.Books);
        Assert.Equal("Test Book", model.Books[0].Title);
        Assert.Equal(18_610, model.Books[0].CharactersRead);
        Assert.Empty(await database.Context.MediaWorks.ToListAsync());
    }

    [Fact]
    public async Task Confirm_ImportsTheSelectedBookAndSessions()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var fixtureStream = File.OpenRead(GetFixturePath());
        var model = CreateModel(database.Context);
        model.FolderFiles = [StatisticsFile(fixtureStream)];
        await model.OnPostPreviewAsync(CancellationToken.None);

        var result = await model.OnPostConfirmAsync(CancellationToken.None);

        Assert.Equal("/Library/Index", Assert.IsType<RedirectToPageResult>(result).PageName);
        database.Context.ChangeTracker.Clear();
        var work = await database.Context.MediaWorks
            .Include(mediaWork => mediaWork.Logs)
            .SingleAsync();
        Assert.Equal("Test Book", work.Title);
        Assert.Equal(2, work.Logs.Count);
        Assert.Equal(18_610, work.CurrentCharactersRead);
    }

    [Fact]
    public async Task Confirm_ReimportUpdatesMatchingTtsuDates()
    {
        await using var database = await TestDatabase.CreateAsync();
        var existingBook = new TtsuBookContainer
        {
            Title = "test book",
            Entries =
            [
                new TtsuReaderDTO
                {
                    Title = "test book",
                    DateKey = "2026-08-05",
                    CharactersRead = 1,
                    ReadingTime = 1,
                    LastStatisticModified = 1
                }
            ]
        };
        database.Context.MediaWorks.Add(TtsuBookImporter.CreateMediaWork(existingBook));
        await database.Context.SaveChangesAsync();

        using var fixtureStream = File.OpenRead(GetFixturePath());
        var model = CreateModel(database.Context);
        model.FolderFiles = [StatisticsFile(fixtureStream)];
        await model.OnPostPreviewAsync(CancellationToken.None);

        Assert.True(model.Books.Single().ExistsInLibrary);
        database.Context.ChangeTracker.Clear();
        await model.OnPostConfirmAsync(CancellationToken.None);

        database.Context.ChangeTracker.Clear();
        var works = await database.Context.MediaWorks
            .Include(mediaWork => mediaWork.Logs)
            .ToListAsync();
        Assert.Single(works);
        Assert.Equal(2, works[0].Logs.Count);
        Assert.Equal(18_610, works[0].CurrentCharactersRead);
    }

    [Fact]
    public async Task Preview_RejectsAFolderWithoutStatisticsFiles()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var unrelatedStream = new MemoryStream([1, 2, 3]);
        var model = CreateModel(database.Context);
        model.FolderFiles =
        [
            new FormFile(unrelatedStream, 0, unrelatedStream.Length, "FolderFiles", "cover.png")
        ];

        var result = await model.OnPostPreviewAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.False(model.ModelState.IsValid);
        Assert.False(model.HasPreview);
        Assert.Empty(await database.Context.MediaWorks.ToListAsync());
    }

    [Fact]
    public async Task Confirm_ImportsOnlySelectedBooksFromAMultiBookPreview()
    {
        await using var database = await TestDatabase.CreateAsync();
        var fixture = await File.ReadAllTextAsync(GetFixturePath());
        using var firstStream = new MemoryStream(Encoding.UTF8.GetBytes(fixture));
        using var secondStream = new MemoryStream(Encoding.UTF8.GetBytes(
            fixture.Replace("Test Book", "Second Book", StringComparison.Ordinal)));
        var model = CreateModel(database.Context);
        model.FolderFiles =
        [
            StatisticsFile(firstStream, "Test Book"),
            StatisticsFile(secondStream, "Second Book")
        ];
        await model.OnPostPreviewAsync(CancellationToken.None);

        Assert.Equal(2, model.Books.Count);
        var secondBookIndex = model.Books
            .Select((book, index) => new { book.Title, Index = index })
            .Single(item => item.Title == "Second Book")
            .Index;
        model.Selections[secondBookIndex].Selected = false;

        await model.OnPostConfirmAsync(CancellationToken.None);

        var importedWork = await database.Context.MediaWorks.SingleAsync();
        Assert.Equal("Test Book", importedWork.Title);
    }

    [Fact]
    public async Task Confirm_RejectsAnExpiredBatch()
    {
        await using var database = await TestDatabase.CreateAsync();
        var model = CreateModel(database.Context);
        model.BatchId = Guid.NewGuid();

        var result = await model.OnPostConfirmAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.False(model.ModelState.IsValid);
        Assert.False(model.HasPreview);
        Assert.Empty(await database.Context.MediaWorks.ToListAsync());
    }

    private static TtsuModel CreateModel(ImmersionDbContext context)
    {
        var httpContext = new DefaultHttpContext();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var model = new TtsuModel(
            new TtsuDataLoader(),
            new TtsuImportBatchStore(cache),
            context)
        {
            PageContext = new PageContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, new TestTempDataProvider())
        };

        return model;
    }

    private static FormFile StatisticsFile(Stream stream, string folderTitle = "Test Book")
    {
        return new FormFile(
            stream,
            0,
            stream.Length,
            "FolderFiles",
            $"ttu-reader-data/{folderTitle}/statistics.json");
    }

    private static string GetFixturePath()
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures", "ttsu-statistics.json");
    }

    private sealed class TestTempDataProvider : ITempDataProvider
    {
        private Dictionary<string, object> _values = [];

        public IDictionary<string, object> LoadTempData(HttpContext context) => _values;

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
            _values = new Dictionary<string, object>(values);
        }
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
