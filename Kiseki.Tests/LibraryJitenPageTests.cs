using Kiseki.Core;
using Kiseki.Core.DTOs;
using Kiseki.Core.Entities;
using Kiseki.Core.Models;
using Kiseki.Core.Services;
using Kiseki.Web.Pages.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Kiseki.Tests;

public sealed class LibraryJitenPageTests
{
    [Fact]
    public async Task Link_RefetchesJitenAndPersistsSelectedSubdeckMetadata()
    {
        await using var database = await TestDatabase.CreateAsync();
        var work = new MediaWork("Local title");
        database.Context.MediaWorks.Add(work);
        await database.Context.SaveChangesAsync();

        var client = new StubJitenApiClient
        {
            Detail = BookDetail()
        };
        var model = CreateLinkModel(database.Context, client);
        model.Input = new LinkJitenModel.LinkJitenInput
        {
            ParentDeckId = 10,
            SubdeckId = 11,
            TitleChoice = JitenTitleChoice.English
        };

        var result = await model.OnPostLinkAsync(work.Id, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Library/Details", redirect.PageName);
        Assert.Equal(10, client.LastDetailDeckId);

        database.Context.ChangeTracker.Clear();
        var linkedWork = await database.Context.MediaWorks.SingleAsync();
        Assert.Equal("Volume 1", linkedWork.Title);
        Assert.Equal(10, linkedWork.JitenDeckId);
        Assert.Equal(11, linkedWork.JitenSubdeckId);
        Assert.Equal(123_456, linkedWork.JitenCharacterCount);
        Assert.Equal("https://cdn.jiten.moe/volume-1.jpg", linkedWork.JitenCoverUrl);
    }

    [Fact]
    public async Task Link_RejectsASubdeckThatIsNotInTheFreshJitenResponse()
    {
        await using var database = await TestDatabase.CreateAsync();
        var work = new MediaWork("Local title");
        database.Context.MediaWorks.Add(work);
        await database.Context.SaveChangesAsync();

        var model = CreateLinkModel(
            database.Context,
            new StubJitenApiClient { Detail = BookDetail() });
        model.Input = new LinkJitenModel.LinkJitenInput
        {
            ParentDeckId = 10,
            SubdeckId = 999,
            TitleChoice = JitenTitleChoice.KeepCurrent
        };

        var result = await model.OnPostLinkAsync(work.Id, CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.False(model.ModelState.IsValid);
        database.Context.ChangeTracker.Clear();
        var unchangedWork = await database.Context.MediaWorks.SingleAsync();
        Assert.False(unchangedWork.HasJitenLink);
        Assert.Equal("Local title", unchangedWork.Title);
    }

    [Fact]
    public async Task Search_PopulatesResultsWithoutChangingTheMediaWork()
    {
        await using var database = await TestDatabase.CreateAsync();
        var work = new MediaWork("Local title");
        database.Context.MediaWorks.Add(work);
        await database.Context.SaveChangesAsync();

        var client = new StubJitenApiClient
        {
            SearchResults = [BookDetail().MainDeck!]
        };
        var model = CreateLinkModel(database.Context, client);
        model.Query = "  volume 1  ";

        var result = await model.OnGetSearchAsync(work.Id, CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Equal("volume 1", client.LastSearchQuery);
        Assert.Single(model.Results);
        database.Context.ChangeTracker.Clear();
        Assert.False((await database.Context.MediaWorks.SingleAsync()).HasJitenLink);
    }

    [Fact]
    public async Task Details_LoadsMetadataAndOrdersImmersionLogsNewestFirst()
    {
        await using var database = await TestDatabase.CreateAsync();
        var work = new MediaWork("Volume 1")
        {
            MediaSeries = new MediaSeries("Example series", MediaType.Book),
            Logs =
            [
                new ImmersionLog
                {
                    Date = new DateOnly(2026, 8, 1),
                    CharactersRead = 1_000,
                    TimeSpentMinutes = 20,
                    Source = "ttsu"
                },
                new ImmersionLog
                {
                    Date = new DateOnly(2026, 8, 3),
                    CharactersRead = 2_000,
                    TimeSpentMinutes = 30,
                    Source = "manual"
                }
            ]
        };
        work.LinkToJitenDeck(10, 100_000, "https://cdn.jiten.moe/book.jpg");
        database.Context.MediaWorks.Add(work);
        await database.Context.SaveChangesAsync();

        var model = new DetailsModel(database.Context);

        var result = await model.OnGetAsync(work.Id, CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Equal("Example series", model.Work.SeriesTitle);
        Assert.Equal(3_000, model.Work.CharactersRead);
        Assert.Equal(2, model.Work.Logs.Count);
        Assert.Equal(new DateOnly(2026, 8, 3), model.Work.Logs[0].Date);
        Assert.Equal("MANUAL", model.Work.Logs[0].SourceLabel);
        Assert.Equal(50, model.Work.TotalTimeMinutes);
    }

    [Fact]
    public async Task Details_ReturnsNotFoundForAnUnknownWork()
    {
        await using var database = await TestDatabase.CreateAsync();
        var model = new DetailsModel(database.Context);

        var result = await model.OnGetAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    private static LinkJitenModel CreateLinkModel(
        ImmersionDbContext context,
        IJitenApiClient client)
    {
        var httpContext = new DefaultHttpContext();
        return new LinkJitenModel(context, client)
        {
            PageContext = new PageContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, new TestTempDataProvider())
        };
    }

    private static JitenDeckDetailDTO BookDetail()
    {
        return new JitenDeckDetailDTO
        {
            MainDeck = new JitenDeckDTO
            {
                DeckId = 10,
                OriginalTitle = "Series",
                EnglishTitle = "Example series",
                CharacterCount = 500_000,
                CoverName = "https://cdn.jiten.moe/series.jpg",
                ChildrenDeckCount = 1
            },
            SubDecks =
            [
                new JitenDeckDTO
                {
                    DeckId = 11,
                    OriginalTitle = "第一巻",
                    RomajiTitle = "Dai Ikkan",
                    EnglishTitle = "Volume 1",
                    CharacterCount = 123_456,
                    CoverName = "https://cdn.jiten.moe/volume-1.jpg"
                }
            ]
        };
    }

    private sealed class StubJitenApiClient : IJitenApiClient
    {
        public IReadOnlyList<JitenDeckDTO> SearchResults { get; init; } = [];
        public JitenDeckDetailDTO? Detail { get; init; }
        public string? LastSearchQuery { get; private set; }
        public int? LastDetailDeckId { get; private set; }

        public Task<IReadOnlyList<JitenDeckDTO>> SearchBooksAsync(
            string query,
            CancellationToken cancellationToken = default)
        {
            LastSearchQuery = query;
            return Task.FromResult(SearchResults);
        }

        public Task<JitenDeckDetailDTO?> GetDeckDetailAsync(
            int deckId,
            CancellationToken cancellationToken = default)
        {
            LastDetailDeckId = deckId;
            return Task.FromResult(Detail);
        }

        public Task<JitenFranchiseDTO?> GetFranchiseAsync(
            int deckId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<JitenFranchiseDTO?>(null);
        }
    }

    private sealed class TestTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) =>
            new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
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
