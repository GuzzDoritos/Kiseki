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

public sealed class SeriesDetailsPageTests
{
    [Fact]
    public async Task OnGetAsync_WithValidId_LoadsSeriesDetailsAndAvailableWorks()
    {
        await using var db = await TestDatabase.CreateAsync();
        var series = new MediaSeries("Spice and Wolf", MediaType.Book);
        var work = new MediaWork("Volume 1 Work");
        var availableWork = new MediaWork("Unassigned Work");
        db.Context.MediaWorks.AddRange([work, availableWork]);

        var inst = new SeriesInstallment(series.Id, 1, "Volume 1", 101, 100_000)
        {
            MediaWorkId = work.Id
        };
        series.Installments.Add(inst);
        db.Context.MediaSeries.Add(series);
        await db.Context.SaveChangesAsync();

        var service = new SeriesService(db.Context, new StubJitenApiClient());
        var page = CreatePageModel(service);

        var result = await page.OnGetAsync(series.Id);

        Assert.IsType<PageResult>(result);
        Assert.NotNull(page.Series);
        Assert.Equal("Spice and Wolf", page.Series.Title);
        Assert.Single(page.Series.Installments);
        Assert.Equal("Volume 1 Work", page.Series.Installments[0].MediaWorkTitle);
        Assert.Single(page.Series.AvailableWorks);
        Assert.Equal("Unassigned Work", page.Series.AvailableWorks[0].Title);
    }

    [Fact]
    public async Task OnGetAsync_CalculatesSeriesAndInstallmentTimeEstimates_WhenLogsExist()
    {
        await using var db = await TestDatabase.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);

        // Add 120 minutes of reading in the last 10 days (30,000 chars -> 15,000 ch/h, 1,000 ch/day)
        db.Context.ImmersionLogs.AddRange([
            new ImmersionLog { Date = today.AddDays(-2), CharactersRead = 15000, TimeSpentMinutes = 60 },
            new ImmersionLog { Date = today.AddDays(-5), CharactersRead = 15000, TimeSpentMinutes = 60 }
        ]);

        var series = new MediaSeries("Spice and Wolf", MediaType.Book);
        var work = new MediaWork("Volume 1 Work") { JitenCharacterCount = 100_000 };
        // 40,000 read of 100,000 -> 60,000 remaining
        work.Logs.Add(new ImmersionLog { CharactersRead = 40000, TimeSpentMinutes = 160 });
        db.Context.MediaWorks.Add(work);

        var inst1 = new SeriesInstallment(series.Id, 1, "Volume 1", 101, 100_000)
        {
            MediaWorkId = work.Id
        };
        var inst2 = new SeriesInstallment(series.Id, 2, "Volume 2", 102, 90_000); // 90,000 remaining
        series.Installments.AddRange([inst1, inst2]);
        db.Context.MediaSeries.Add(series);
        await db.Context.SaveChangesAsync();

        var seriesService = new SeriesService(db.Context, new StubJitenApiClient());
        var paceService = new ReadingPaceService(db.Context);
        var page = CreatePageModel(seriesService, paceService);

        var result = await page.OnGetAsync(series.Id);

        Assert.IsType<PageResult>(result);
        Assert.NotNull(page.Series);

        // Check Series Pace & Estimate
        Assert.NotNull(page.Series.PaceProfile);
        Assert.True(page.Series.PaceProfile.HasSufficientData);
        Assert.Equal(15000, page.Series.PaceProfile.CharactersPerHour);
        Assert.Equal(1000, page.Series.PaceProfile.DailyCharacters);

        // Total remaining: 60k (vol 1) + 90k (vol 2) = 150,000 characters
        Assert.Equal(150000, page.Series.RemainingCharacters);
        Assert.NotNull(page.Series.TimeEstimate);
        Assert.True(page.Series.TimeEstimate.HasSufficientData);
        // Reading time: 150,000 / 15,000 = 10 hours
        Assert.Equal("10h reading", page.Series.TimeEstimate.FormattedReadingTime);
        Assert.Equal("10h", page.Series.TimeEstimate.FormattedReadingTimeCompact);

        // Volume-level estimates:
        // Vol 1: 60,000 remaining / 15,000 = 4 hours
        var vol1 = page.Series.Installments[0];
        Assert.Equal(60000, vol1.RemainingCharacters);
        Assert.NotNull(vol1.TimeEstimate);
        Assert.Equal("4h reading", vol1.TimeEstimate.FormattedReadingTime);
        Assert.Equal("4h", vol1.TimeEstimate.FormattedReadingTimeCompact);

        // Vol 2: 90,000 remaining / 15,000 = 6 hours
        var vol2 = page.Series.Installments[1];
        Assert.Equal(90000, vol2.RemainingCharacters);
        Assert.NotNull(vol2.TimeEstimate);
        Assert.Equal("6h reading", vol2.TimeEstimate.FormattedReadingTime);
        Assert.Equal("6h", vol2.TimeEstimate.FormattedReadingTimeCompact);
    }

    [Fact]
    public async Task OnGetAsync_WithInvalidId_ReturnsNotFound()
    {
        await using var db = await TestDatabase.CreateAsync();
        var service = new SeriesService(db.Context, new StubJitenApiClient());
        var page = CreatePageModel(service);

        var result = await page.OnGetAsync(Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task OnPostLinkWorkAsync_LinksWorkAndRedirects()
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
        var page = CreatePageModel(service);

        var result = await page.OnPostLinkWorkAsync(series.Id, inst.Id, work.Id);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Series/Details", redirect.PageName);
        Assert.Equal(series.Id, redirect.RouteValues?["id"]);
        Assert.True(page.TempData.ContainsKey("SeriesNotice"));

        db.Context.ChangeTracker.Clear();
        var reloadedInst = await db.Context.SeriesInstallments.SingleAsync(i => i.Id == inst.Id);
        Assert.Equal(work.Id, reloadedInst.MediaWorkId);
    }

    [Fact]
    public async Task OnPostUnlinkWorkAsync_UnlinksWorkAndRedirects()
    {
        await using var db = await TestDatabase.CreateAsync();
        var series = new MediaSeries("Spice and Wolf", MediaType.Book);
        var work = new MediaWork("Volume 1 Work");
        db.Context.MediaWorks.Add(work);

        var inst = new SeriesInstallment(series.Id, 1, "Volume 1", 101, 100_000)
        {
            MediaWorkId = work.Id
        };
        series.Installments.Add(inst);
        db.Context.MediaSeries.Add(series);
        await db.Context.SaveChangesAsync();

        var service = new SeriesService(db.Context, new StubJitenApiClient());
        var page = CreatePageModel(service);

        var result = await page.OnPostUnlinkWorkAsync(series.Id, inst.Id);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Series/Details", redirect.PageName);
        Assert.True(page.TempData.ContainsKey("SeriesNotice"));

        db.Context.ChangeTracker.Clear();
        var reloadedInst = await db.Context.SeriesInstallments.SingleAsync(i => i.Id == inst.Id);
        Assert.Null(reloadedInst.MediaWorkId);
    }

    [Fact]
    public async Task OnPostAddInstallmentAsync_AddsInstallmentAndRedirects()
    {
        await using var db = await TestDatabase.CreateAsync();
        var series = new MediaSeries("Spice and Wolf", MediaType.Book);
        db.Context.MediaSeries.Add(series);
        await db.Context.SaveChangesAsync();

        var service = new SeriesService(db.Context, new StubJitenApiClient());
        var page = CreatePageModel(service);
        page.NewInstallment = new DetailsModel.AddInstallmentInput
        {
            Title = "Volume 1",
            CharacterCount = 105_000
        };

        var result = await page.OnPostAddInstallmentAsync(series.Id);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Series/Details", redirect.PageName);
        Assert.True(page.TempData.ContainsKey("SeriesNotice"));

        db.Context.ChangeTracker.Clear();
        var installment = await db.Context.SeriesInstallments.SingleAsync();
        Assert.Equal("Volume 1", installment.Title);
        Assert.Equal(105_000, installment.JitenCharacterCount);
        Assert.Equal(1, installment.SequenceNumber);
    }

    [Fact]
    public async Task OnPostRemoveInstallmentAsync_RemovesInstallmentAndRedirects()
    {
        await using var db = await TestDatabase.CreateAsync();
        var series = new MediaSeries("Spice and Wolf", MediaType.Book);
        var inst = new SeriesInstallment(series.Id, 1, "Volume 1", 101, 100_000);
        series.Installments.Add(inst);
        db.Context.MediaSeries.Add(series);
        await db.Context.SaveChangesAsync();

        var service = new SeriesService(db.Context, new StubJitenApiClient());
        var page = CreatePageModel(service);

        var result = await page.OnPostRemoveInstallmentAsync(series.Id, inst.Id);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Series/Details", redirect.PageName);
        Assert.True(page.TempData.ContainsKey("SeriesNotice"));

        db.Context.ChangeTracker.Clear();
        Assert.Empty(await db.Context.SeriesInstallments.ToListAsync());
    }

    [Fact]
    public async Task DeleteSeries_SuccessfullyDeletesSeriesAndRedirectsToSeriesIndex()
    {
        await using var db = await TestDatabase.CreateAsync();
        var series = new MediaSeries("Spice and Wolf", MediaType.Book);
        var inst = new SeriesInstallment(series.Id, 1, "Volume 1", 101, 100_000);
        series.Installments.Add(inst);

        var work = new MediaWork("Spice and Wolf Vol 1", mediaType: MediaType.Book)
        {
            MediaSeriesId = series.Id
        };
        var log = new ImmersionLog
        {
            MediaWorkId = work.Id,
            Date = new DateOnly(2026, 1, 1),
            CharactersRead = 5000,
            TimeSpentMinutes = 30.0
        };
        work.Logs.Add(log);
        inst.MediaWorkId = work.Id;
        inst.MediaWork = work;

        db.Context.MediaSeries.Add(series);
        db.Context.MediaWorks.Add(work);
        await db.Context.SaveChangesAsync();

        var service = new SeriesService(db.Context, new StubJitenApiClient());
        var page = CreatePageModel(service);

        var result = await page.OnPostDeleteSeriesAsync(series.Id);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Series/Index", redirect.PageName);
        Assert.Equal("Deleted series “Spice and Wolf”.", page.TempData["SeriesNotice"]);

        db.Context.ChangeTracker.Clear();

        Assert.Null(await db.Context.MediaSeries.FindAsync(series.Id));
        Assert.Null(await db.Context.SeriesInstallments.FindAsync(inst.Id));

        var persistedWork = await db.Context.MediaWorks
            .Include(w => w.Logs)
            .SingleAsync(w => w.Id == work.Id);
        Assert.Null(persistedWork.MediaSeriesId);
        Assert.Single(persistedWork.Logs);
    }

    [Fact]
    public async Task DeleteSeries_NonExistentSeries_RedirectsToSeriesIndexWithError()
    {
        await using var db = await TestDatabase.CreateAsync();
        var service = new SeriesService(db.Context, new StubJitenApiClient());
        var page = CreatePageModel(service);

        var missingId = Guid.NewGuid();
        var result = await page.OnPostDeleteSeriesAsync(missingId);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Series/Index", redirect.PageName);
        Assert.True(page.TempData.ContainsKey("SeriesNotice"));
        Assert.Contains("not found", (string)page.TempData["SeriesNotice"]!);
    }

    private static DetailsModel CreatePageModel(ISeriesService seriesService, IReadingPaceService? paceService = null)
    {
        var httpContext = new DefaultHttpContext();
        return new DetailsModel(seriesService, paceService ?? new ReadingPaceService(null))
        {
            PageContext = new PageContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, new TestTempDataProvider())
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

