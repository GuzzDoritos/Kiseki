using Kiseki.Core;
using Kiseki.Core.DTOs;
using Kiseki.Core.Entities;
using Kiseki.Core.Services;
using Kiseki.Web.Pages.Series;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Kiseki.Tests;

public sealed class SeriesIndexPageTests
{
    [Fact]
    public async Task OnGetAsync_LoadsAllSeries_WhenNoFilterProvided()
    {
        await using var db = await TestDatabase.CreateAsync();
        var seriesA = new MediaSeries("86 - Eighty Six", MediaType.Book);
        var seriesB = new MediaSeries("Spice and Wolf", MediaType.Book);

        var inst1 = new SeriesInstallment(seriesB.Id, 1, "Volume 1", jitenCharacterCount: 100_000);
        seriesB.Installments.Add(inst1);

        db.Context.MediaSeries.AddRange(seriesA, seriesB);
        await db.Context.SaveChangesAsync();

        var service = new SeriesService(db.Context, new StubJitenApiClient());
        var page = CreatePageModel(service);

        await page.OnGetAsync();

        Assert.Equal(2, page.TotalCount);
        Assert.Equal(2, page.Series.Count);
        Assert.Equal("86 - Eighty Six", page.Series[0].Title);
        Assert.Equal("Spice and Wolf", page.Series[1].Title);
        Assert.Equal(1, page.Series[1].InstallmentsCount);
    }

    [Fact]
    public async Task OnGetAsync_FiltersByMediaType_WhenTypeProvided()
    {
        await using var db = await TestDatabase.CreateAsync();
        var seriesBook = new MediaSeries("Spice and Wolf", MediaType.Book);
        var seriesGame = new MediaSeries("Steins;Gate", MediaType.Game);

        db.Context.MediaSeries.AddRange(seriesBook, seriesGame);
        await db.Context.SaveChangesAsync();

        var service = new SeriesService(db.Context, new StubJitenApiClient());
        var page = CreatePageModel(service);
        page.Type = MediaType.Game;

        await page.OnGetAsync();

        Assert.Equal(2, page.TotalCount);
        Assert.Single(page.Series);
        Assert.Equal("Steins;Gate", page.Series[0].Title);
        Assert.Equal(MediaType.Game, page.Series[0].MediaType);
    }

    [Fact]
    public async Task OnGetAsync_FiltersBySearch_WhenSearchProvided()
    {
        await using var db = await TestDatabase.CreateAsync();
        var series1 = new MediaSeries("Spice and Wolf", MediaType.Book);
        var series2 = new MediaSeries("Overlord", MediaType.Book);

        db.Context.MediaSeries.AddRange(series1, series2);
        await db.Context.SaveChangesAsync();

        var service = new SeriesService(db.Context, new StubJitenApiClient());
        var page = CreatePageModel(service);
        page.Search = "spice";

        await page.OnGetAsync();

        Assert.Equal(2, page.TotalCount);
        Assert.Single(page.Series);
        Assert.Equal("Spice and Wolf", page.Series[0].Title);
    }

    [Fact]
    public async Task OnGetAsync_EmptyDatabase_ReturnsEmptyListAndZeroCount()
    {
        await using var db = await TestDatabase.CreateAsync();
        var service = new SeriesService(db.Context, new StubJitenApiClient());
        var page = CreatePageModel(service);

        await page.OnGetAsync();

        Assert.Equal(0, page.TotalCount);
        Assert.Empty(page.Series);
    }

    private static IndexModel CreatePageModel(ISeriesService service)
    {
        var httpContext = new DefaultHttpContext();
        var modelState = new ModelStateDictionary();
        var actionContext = new Microsoft.AspNetCore.Mvc.ActionContext(
            httpContext,
            new Microsoft.AspNetCore.Routing.RouteData(),
            new PageActionDescriptor(),
            modelState);
        var modelMetadataProvider = new EmptyModelMetadataProvider();
        var viewData = new ViewDataDictionary(modelMetadataProvider, modelState);
        var pageContext = new PageContext(actionContext)
        {
            ViewData = viewData
        };

        return new IndexModel(service)
        {
            PageContext = pageContext
        };
    }

    private sealed class StubJitenApiClient : IJitenApiClient
    {
        public Task<IReadOnlyList<JitenDeckDTO>> SearchBooksAsync(string query, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<JitenDeckDTO>>([]);

        public Task<JitenDeckDetailDTO?> GetDeckDetailAsync(int deckId, CancellationToken cancellationToken = default) =>
            Task.FromResult<JitenDeckDetailDTO?>(null);

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
