using Kiseki.Core;
using Kiseki.Core.DTOs;
using Kiseki.Core.Entities;
using Kiseki.Core.Services;
using Kiseki.Web.Pages.Series;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Kiseki.Tests;

public sealed class SeriesCreatePageTests
{
    [Fact]
    public async Task OnGetAsync_WithSearchQuery_PopulatesResults()
    {
        await using var db = await TestDatabase.CreateAsync();
        var client = new StubJitenApiClient
        {
            SearchResults =
            [
                new JitenDeckDTO
                {
                    DeckId = 10,
                    OriginalTitle = "Spice and Wolf",
                    CharacterCount = 500_000,
                    ChildrenDeckCount = 5
                }
            ]
        };
        var service = new SeriesService(db.Context, client);
        var page = CreatePageModel(service, client);
        page.Query = "Spice";

        var result = await page.OnGetAsync();

        Assert.IsType<PageResult>(result);
        Assert.Single(page.Results);
        Assert.Equal("Spice and Wolf", page.Results[0].DisplayTitle);
        Assert.Equal("Spice", client.LastSearchQuery);
    }

    [Fact]
    public async Task OnPostJitenAsync_CreatesSeriesAndRedirectsToDetails()
    {
        await using var db = await TestDatabase.CreateAsync();
        var client = new StubJitenApiClient
        {
            Detail = new JitenDeckDetailDTO
            {
                MainDeck = new JitenDeckDTO
                {
                    DeckId = 10,
                    OriginalTitle = "Spice and Wolf",
                    CharacterCount = 200_000
                },
                SubDecks =
                [
                    new JitenDeckDTO { DeckId = 101, OriginalTitle = "Volume 1", CharacterCount = 100_000 },
                    new JitenDeckDTO { DeckId = 102, OriginalTitle = "Volume 2", CharacterCount = 100_000 }
                ]
            }
        };
        var service = new SeriesService(db.Context, client);
        var page = CreatePageModel(service, client);

        var result = await page.OnPostJitenAsync(10);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Series/Details", redirect.PageName);
        Assert.NotNull(redirect.RouteValues?["id"]);

        var seriesId = (Guid)redirect.RouteValues["id"]!;
        var series = await db.Context.MediaSeries
            .Include(s => s.Installments)
            .SingleAsync(s => s.Id == seriesId);

        Assert.Equal("Spice and Wolf", series.Title);
        Assert.Equal(2, series.Installments.Count);
        Assert.True(page.TempData.ContainsKey("SeriesNotice"));
    }

    [Fact]
    public async Task OnPostManualAsync_CreatesSeriesAndRedirectsToDetails()
    {
        await using var db = await TestDatabase.CreateAsync();
        var client = new StubJitenApiClient();
        var service = new SeriesService(db.Context, client);
        var page = CreatePageModel(service, client);

        page.ManualInput = new CreateModel.ManualSeriesInput
        {
            Title = "Rascal Does Not Dream Series",
            MediaType = MediaType.Book
        };

        var result = await page.OnPostManualAsync();

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Series/Details", redirect.PageName);
        Assert.NotNull(redirect.RouteValues?["id"]);

        var seriesId = (Guid)redirect.RouteValues["id"]!;
        var series = await db.Context.MediaSeries.SingleAsync(s => s.Id == seriesId);

        Assert.Equal("Rascal Does Not Dream Series", series.Title);
        Assert.Equal(MediaType.Book, series.MediaType);
    }

    private static CreateModel CreatePageModel(ISeriesService seriesService, IJitenApiClient jitenApiClient)
    {
        var httpContext = new DefaultHttpContext();
        return new CreateModel(seriesService, jitenApiClient)
        {
            PageContext = new PageContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, new TestTempDataProvider())
        };
    }

    private sealed class StubJitenApiClient : IJitenApiClient
    {
        public IReadOnlyList<JitenDeckDTO> SearchResults { get; init; } = [];
        public JitenDeckDetailDTO? Detail { get; init; }
        public string? LastSearchQuery { get; private set; }

        public Task<IReadOnlyList<JitenDeckDTO>> SearchBooksAsync(string query, CancellationToken cancellationToken = default)
        {
            LastSearchQuery = query;
            return Task.FromResult(SearchResults);
        }

        public Task<JitenDeckDetailDTO?> GetDeckDetailAsync(int deckId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Detail);

        public Task<JitenFranchiseDTO?> GetFranchiseAsync(int deckId, CancellationToken cancellationToken = default) =>
            Task.FromResult<JitenFranchiseDTO?>(null);
    }

    private sealed class TestTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) =>
            new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
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

