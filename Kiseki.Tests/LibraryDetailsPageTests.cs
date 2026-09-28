using Kiseki.Core;
using Kiseki.Core.Entities;
using Kiseki.Web.Models;
using Kiseki.Web.Pages.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Kiseki.Tests;

public class LibraryDetailsPageTests
{
    [Fact]
    public async Task UpdateTitle_SuccessfullyUpdatesAndTrims()
    {
        await using var database = await TestDatabase.CreateAsync();
        var work = new MediaWork("Old Title");
        database.Context.MediaWorks.Add(work);
        await database.Context.SaveChangesAsync();

        var model = new DetailsModel(database.Context);
        var result = await model.OnPostUpdateTitleAsync(
            work.Id,
            new DetailsModel.UpdateTitleRequest { Title = "  New Updated Title  " },
            CancellationToken.None);

        var jsonResult = Assert.IsType<JsonResult>(result);
        Assert.NotNull(jsonResult.Value);

        var updatedWork = await database.Context.MediaWorks.FindAsync(work.Id);
        Assert.NotNull(updatedWork);
        Assert.Equal("New Updated Title", updatedWork.Title);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UpdateTitle_RejectsEmptyOrWhitespaceTitle(string? emptyTitle)
    {
        await using var database = await TestDatabase.CreateAsync();
        var work = new MediaWork("Existing Title");
        database.Context.MediaWorks.Add(work);
        await database.Context.SaveChangesAsync();

        var model = new DetailsModel(database.Context);
        var result = await model.OnPostUpdateTitleAsync(
            work.Id,
            new DetailsModel.UpdateTitleRequest { Title = emptyTitle },
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);

        var unchangedWork = await database.Context.MediaWorks.FindAsync(work.Id);
        Assert.NotNull(unchangedWork);
        Assert.Equal("Existing Title", unchangedWork.Title);
    }

    [Fact]
    public async Task UpdateTitle_ReturnsNotFoundForUnknownWork()
    {
        await using var database = await TestDatabase.CreateAsync();
        var model = new DetailsModel(database.Context);
        var result = await model.OnPostUpdateTitleAsync(
            Guid.NewGuid(),
            new DetailsModel.UpdateTitleRequest { Title = "Any Title" },
            CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task UpdateCharacterTotal_SuccessfullySetsManualOverrideAndRecalculates()
    {
        await using var database = await TestDatabase.CreateAsync();
        var work = new MediaWork("Test Book")
        {
            Logs =
            [
                new ImmersionLog
                {
                    Date = new DateOnly(2026, 9, 10),
                    CharactersRead = 50_000,
                    TimeSpentMinutes = 120
                }
            ]
        };
        work.LinkToJitenDeck(1234, 100_000);
        database.Context.MediaWorks.Add(work);
        await database.Context.SaveChangesAsync();

        var model = new DetailsModel(database.Context);
        var result = await model.OnPostUpdateCharacterTotalAsync(
            work.Id,
            new DetailsModel.UpdateCharacterTotalRequest { ManualCharacterCount = 200_000 },
            CancellationToken.None);

        var jsonResult = Assert.IsType<JsonResult>(result);
        Assert.NotNull(jsonResult.Value);

        var updatedWork = await database.Context.MediaWorks.FindAsync(work.Id);
        Assert.NotNull(updatedWork);
        Assert.Equal(200_000, updatedWork.ManualCharacterCountOverride);
        Assert.Equal(200_000, updatedWork.TotalCharacters);
        Assert.Equal(25.0, updatedWork.ProgressPercentage);
    }

    [Fact]
    public async Task UpdateCharacterTotal_ClearingOverrideRevertsToJitenCount()
    {
        await using var database = await TestDatabase.CreateAsync();
        var work = new MediaWork("Test Book")
        {
            ManualCharacterCountOverride = 250_000
        };
        work.LinkToJitenDeck(1234, 150_000);
        database.Context.MediaWorks.Add(work);
        await database.Context.SaveChangesAsync();

        var model = new DetailsModel(database.Context);
        var result = await model.OnPostUpdateCharacterTotalAsync(
            work.Id,
            new DetailsModel.UpdateCharacterTotalRequest { ManualCharacterCount = null },
            CancellationToken.None);

        Assert.IsType<JsonResult>(result);

        var updatedWork = await database.Context.MediaWorks.FindAsync(work.Id);
        Assert.NotNull(updatedWork);
        Assert.Null(updatedWork.ManualCharacterCountOverride);
        Assert.Equal(150_000, updatedWork.TotalCharacters);
    }

    [Fact]
    public async Task UpdateCharacterTotal_RejectsNegativeCharacterCount()
    {
        await using var database = await TestDatabase.CreateAsync();
        var work = new MediaWork("Test Book");
        database.Context.MediaWorks.Add(work);
        await database.Context.SaveChangesAsync();

        var model = new DetailsModel(database.Context);
        var result = await model.OnPostUpdateCharacterTotalAsync(
            work.Id,
            new DetailsModel.UpdateCharacterTotalRequest { ManualCharacterCount = -100 },
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);

        var unchangedWork = await database.Context.MediaWorks.FindAsync(work.Id);
        Assert.NotNull(unchangedWork);
        Assert.Null(unchangedWork.ManualCharacterCountOverride);
    }

    [Fact]
    public async Task UpdateCharacterTotal_ReturnsNotFoundForUnknownWork()
    {
        await using var database = await TestDatabase.CreateAsync();
        var model = new DetailsModel(database.Context);
        var result = await model.OnPostUpdateCharacterTotalAsync(
            Guid.NewGuid(),
            new DetailsModel.UpdateCharacterTotalRequest { ManualCharacterCount = 100_000 },
            CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task UpdateCharacterTotal_AutoCompletesWhenReadAmountMatchesOrExceedsTotal()
    {
        await using var database = await TestDatabase.CreateAsync();
        var work = new MediaWork("Test Book")
        {
            IsCompleted = false,
            Logs =
            [
                new ImmersionLog
                {
                    Date = new DateOnly(2026, 9, 10),
                    CharactersRead = 141_818,
                    TimeSpentMinutes = 300
                }
            ]
        };
        database.Context.MediaWorks.Add(work);
        await database.Context.SaveChangesAsync();

        var model = new DetailsModel(database.Context);
        var result = await model.OnPostUpdateCharacterTotalAsync(
            work.Id,
            new DetailsModel.UpdateCharacterTotalRequest { ManualCharacterCount = 141_818 },
            CancellationToken.None);

        Assert.IsType<JsonResult>(result);

        var updatedWork = await database.Context.MediaWorks.FindAsync(work.Id);
        Assert.NotNull(updatedWork);
        Assert.True(updatedWork.IsCompleted);
        Assert.Equal(100.0, updatedWork.ProgressPercentage);
    }

    [Fact]
    public async Task UpdateCharacterTotal_RevertsToNotCompletedWhenTotalIsIncreasedAboveRead()
    {
        await using var database = await TestDatabase.CreateAsync();
        var work = new MediaWork("Test Book")
        {
            IsCompleted = true,
            ManualCharacterCountOverride = 100_000,
            Logs =
            [
                new ImmersionLog
                {
                    Date = new DateOnly(2026, 9, 10),
                    CharactersRead = 100_000,
                    TimeSpentMinutes = 200
                }
            ]
        };
        database.Context.MediaWorks.Add(work);
        await database.Context.SaveChangesAsync();

        var model = new DetailsModel(database.Context);
        var result = await model.OnPostUpdateCharacterTotalAsync(
            work.Id,
            new DetailsModel.UpdateCharacterTotalRequest { ManualCharacterCount = 150_000 },
            CancellationToken.None);

        Assert.IsType<JsonResult>(result);

        var updatedWork = await database.Context.MediaWorks.FindAsync(work.Id);
        Assert.NotNull(updatedWork);
        Assert.False(updatedWork.IsCompleted);
        Assert.Equal(66.66666666666667, updatedWork.ProgressPercentage, 2);
    }

    [Fact]
    public async Task ToggleStatus_TogglesCompletionBetweenTrueAndFalse()
    {
        await using var database = await TestDatabase.CreateAsync();
        var work = new MediaWork("Test Book")
        {
            IsCompleted = false,
            Logs =
            [
                new ImmersionLog
                {
                    Date = new DateOnly(2026, 9, 10),
                    CharactersRead = 50_000,
                    TimeSpentMinutes = 100
                }
            ]
        };
        work.LinkToJitenDeck(100, 100_000);
        database.Context.MediaWorks.Add(work);
        await database.Context.SaveChangesAsync();

        var model = new DetailsModel(database.Context);

        // Toggle to true
        var result1 = await model.OnPostToggleStatusAsync(work.Id, CancellationToken.None);
        Assert.IsType<JsonResult>(result1);

        var updatedWork1 = await database.Context.MediaWorks.FindAsync(work.Id);
        Assert.NotNull(updatedWork1);
        Assert.True(updatedWork1.IsCompleted);
        Assert.Equal(100.0, updatedWork1.ProgressPercentage);

        // Toggle to false
        var result2 = await model.OnPostToggleStatusAsync(work.Id, CancellationToken.None);
        Assert.IsType<JsonResult>(result2);

        var updatedWork2 = await database.Context.MediaWorks.FindAsync(work.Id);
        Assert.NotNull(updatedWork2);
        Assert.False(updatedWork2.IsCompleted);
        Assert.Equal(50.0, updatedWork2.ProgressPercentage);
    }

    [Fact]
    public async Task UpdateCoverUrl_SuccessfullyUpdatesCoverAndSaves()
    {
        await using var database = await TestDatabase.CreateAsync();
        var work = new MediaWork("Test Book");
        database.Context.MediaWorks.Add(work);
        await database.Context.SaveChangesAsync();

        var model = new DetailsModel(database.Context);
        var result = await model.OnPostUpdateCoverUrlAsync(
            work.Id,
            new DetailsModel.UpdateCoverUrlRequest { CoverUrl = "https://cdn.jiten.moe/covers/book.jpg" },
            CancellationToken.None);

        var jsonResult = Assert.IsType<JsonResult>(result);
        Assert.NotNull(jsonResult.Value);

        var updatedWork = await database.Context.MediaWorks.FindAsync(work.Id);
        Assert.NotNull(updatedWork);
        Assert.Equal("https://cdn.jiten.moe/covers/book.jpg", updatedWork.CoverUrl);
        Assert.Equal(MediaCoverSource.UserOverride, updatedWork.CoverSource);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UpdateCoverUrl_RejectsEmptyOrWhitespaceUrl(string? emptyUrl)
    {
        await using var database = await TestDatabase.CreateAsync();
        var work = new MediaWork("Test Book");
        database.Context.MediaWorks.Add(work);
        await database.Context.SaveChangesAsync();

        var model = new DetailsModel(database.Context);
        var result = await model.OnPostUpdateCoverUrlAsync(
            work.Id,
            new DetailsModel.UpdateCoverUrlRequest { CoverUrl = emptyUrl },
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);

        var unchangedWork = await database.Context.MediaWorks.FindAsync(work.Id);
        Assert.NotNull(unchangedWork);
        Assert.Null(unchangedWork.CoverUrl);
        Assert.Equal(MediaCoverSource.None, unchangedWork.CoverSource);
    }

    [Theory]
    [InlineData("http://insecure.com/cover.jpg")]
    [InlineData("ftp://files.com/cover.jpg")]
    [InlineData("not-a-valid-url")]
    public async Task UpdateCoverUrl_RejectsNonHttpsUrl(string invalidUrl)
    {
        await using var database = await TestDatabase.CreateAsync();
        var work = new MediaWork("Test Book");
        database.Context.MediaWorks.Add(work);
        await database.Context.SaveChangesAsync();

        var model = new DetailsModel(database.Context);
        var result = await model.OnPostUpdateCoverUrlAsync(
            work.Id,
            new DetailsModel.UpdateCoverUrlRequest { CoverUrl = invalidUrl },
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);

        var unchangedWork = await database.Context.MediaWorks.FindAsync(work.Id);
        Assert.NotNull(unchangedWork);
        Assert.Null(unchangedWork.CoverUrl);
        Assert.Equal(MediaCoverSource.None, unchangedWork.CoverSource);
    }

    [Fact]
    public async Task UpdateCoverUrl_ReturnsNotFoundForUnknownWork()
    {
        await using var database = await TestDatabase.CreateAsync();
        var model = new DetailsModel(database.Context);
        var result = await model.OnPostUpdateCoverUrlAsync(
            Guid.NewGuid(),
            new DetailsModel.UpdateCoverUrlRequest { CoverUrl = "https://cdn.jiten.moe/covers/book.jpg" },
            CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Theory]
    [InlineData(MediaCoverSource.None, "No cover")]
    [InlineData(MediaCoverSource.LegacyUnknown, "Legacy cover")]
    [InlineData(MediaCoverSource.JitenSpecific, "Jiten volume cover")]
    [InlineData(MediaCoverSource.JitenParentFallback, "Jiten series fallback")]
    [InlineData(MediaCoverSource.UserOverride, "Custom cover")]
    public void CoverSourceLabel_RendersHumanReadableText_WithoutRawEnumNumbers(MediaCoverSource source, string expectedLabel)
    {
        var work = new MediaWork("Test Book");
        if (source == MediaCoverSource.LegacyUnknown) TestCoverState.SetLegacyUnknown(work, "https://example.com/legacy.jpg");
        else if (source == MediaCoverSource.JitenSpecific) work.LinkToJitenDeck(1, 100, "https://example.com/spec.jpg", MediaCoverSource.JitenSpecific);
        else if (source == MediaCoverSource.JitenParentFallback) work.LinkToJitenDeck(1, 100, "https://example.com/fallback.jpg", MediaCoverSource.JitenParentFallback);
        else if (source == MediaCoverSource.UserOverride) work.UpdateCoverUrl("https://example.com/user.jpg");

        var vm = MediaWorkDetailsViewModel.Create(work);

        Assert.Equal(expectedLabel, vm.CoverSourceLabel);
        Assert.False(char.IsDigit(vm.CoverSourceLabel[0]));
    }

    [Fact]
    public async Task OnGetAsync_PopulatesCoverUrlAndCoverSource()
    {
        await using var database = await TestDatabase.CreateAsync();
        var work = new MediaWork("Test Book");
        work.UpdateCoverUrl("https://example.com/cover.jpg");
        database.Context.MediaWorks.Add(work);
        await database.Context.SaveChangesAsync();

        var model = new DetailsModel(database.Context);
        var result = await model.OnGetAsync(work.Id, CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Equal("https://example.com/cover.jpg", model.Work.CoverUrl);
        Assert.Equal(MediaCoverSource.UserOverride, model.Work.CoverSource);
        Assert.Equal("Custom cover", model.Work.CoverSourceLabel);
    }

    [Fact]
    public void CoverMarkup_UsesGenericUrl_ProvenanceLabel_AndFallbackAttributes()
    {
        var webRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "../../../..", "Kiseki.Web"));
        var details = File.ReadAllText(
            Path.Combine(webRoot, "Pages", "Library", "Details.cshtml"));

        Assert.Contains("Model.Work.CoverUrl", details);
        Assert.Contains("Model.Work.CoverSourceLabel", details);
        Assert.Contains("data-metadata-cover-source", details);
        Assert.Contains("loading=\"lazy\"", details);
        Assert.Contains("referrerpolicy=\"no-referrer\"", details);
        Assert.Contains("data-cover-fallback", details);
        Assert.Contains("View on Google Books", details);
        Assert.Contains("https://books.google.com/googlebooks/images/poweredby.png", details);
        Assert.Contains("Powered by Google", details);
        Assert.DoesNotContain("JitenCoverUrl", details);

        foreach (var partialName in new[] { "_MediaWorkRow.cshtml", "_MediaWorkCard.cshtml" })
        {
            var partial = File.ReadAllText(
                Path.Combine(webRoot, "Pages", "Shared", partialName));
            Assert.Contains("Model.CoverUrl", partial);
            Assert.Contains("media-details-overlay", partial);
            Assert.Contains("google-books-item-link", partial);
            Assert.Contains("loading=\"lazy\"", partial);
            Assert.Contains("referrerpolicy=\"no-referrer\"", partial);
            Assert.Contains("data-cover-fallback", partial);
            Assert.DoesNotContain("<a class=\"media-row-link\"", partial);
            Assert.DoesNotContain("<a class=\"media-card-link\"", partial);
            Assert.DoesNotContain("JitenCoverUrl", partial);
        }

        var libraryIndex = File.ReadAllText(
            Path.Combine(webRoot, "Pages", "Library", "Index.cshtml"));
        Assert.Contains("google-books-list-attribution", libraryIndex);
        Assert.Contains("https://books.google.com/googlebooks/images/poweredby.png", libraryIndex);
        Assert.Contains("Powered by Google", libraryIndex);
    }

    [Fact]
    public async Task DeleteSession_SuccessfullyRemovesLog_RedirectsWithNotice()
    {
        await using var database = await TestDatabase.CreateAsync();
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
        database.Context.MediaWorks.Add(work);
        await database.Context.SaveChangesAsync();

        var model = CreatePageModel(database.Context);
        var result = await model.OnPostDeleteSessionAsync(work.Id, log1.Id, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal(work.Id, redirect.RouteValues?["id"]);
        Assert.Equal("Deleted immersion session.", model.TempData["LibraryNotice"]);

        database.Context.ChangeTracker.Clear();
        var updatedWork = await database.Context.MediaWorks
            .Include(w => w.Logs)
            .SingleAsync(w => w.Id == work.Id);

        Assert.Single(updatedWork.Logs);
        Assert.Equal(log2.Id, updatedWork.Logs.First().Id);
        Assert.Equal(2000, updatedWork.CurrentCharactersRead);
    }

    [Fact]
    public async Task DeleteSession_AjaxRequest_ReturnsJsonSuccess()
    {
        await using var database = await TestDatabase.CreateAsync();
        var work = new MediaWork("Test Book", mediaType: MediaType.Book);
        var log = new ImmersionLog
        {
            MediaWorkId = work.Id,
            Date = new DateOnly(2026, 1, 1),
            CharactersRead = 1000,
            TimeSpentMinutes = 20.0
        };
        work.Logs.Add(log);
        database.Context.MediaWorks.Add(work);
        await database.Context.SaveChangesAsync();

        var model = CreatePageModel(database.Context);
        model.HttpContext.Request.Headers.XRequestedWith = "XMLHttpRequest";

        var result = await model.OnPostDeleteSessionAsync(work.Id, log.Id, CancellationToken.None);

        var jsonResult = Assert.IsType<JsonResult>(result);
        Assert.NotNull(jsonResult.Value);
        var json = System.Text.Json.JsonSerializer.Serialize(jsonResult.Value);
        Assert.Contains("\"success\":true", json);

        database.Context.ChangeTracker.Clear();
        var updatedWork = await database.Context.MediaWorks
            .Include(w => w.Logs)
            .SingleAsync(w => w.Id == work.Id);

        Assert.Empty(updatedWork.Logs);
    }

    [Fact]
    public async Task DeleteSession_NonExistentLog_RedirectsWithErrorNotice()
    {
        await using var database = await TestDatabase.CreateAsync();
        var work = new MediaWork("Test Book", mediaType: MediaType.Book);
        database.Context.MediaWorks.Add(work);
        await database.Context.SaveChangesAsync();

        var model = CreatePageModel(database.Context);
        var result = await model.OnPostDeleteSessionAsync(work.Id, Guid.NewGuid(), CancellationToken.None);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal(work.Id, redirect.RouteValues?["id"]);
        Assert.True(model.TempData.ContainsKey("LibraryError"));
        Assert.Contains("not found", (string)model.TempData["LibraryError"]!);
    }

    [Fact]
    public async Task DeleteWork_SuccessfullyDeletesWorkAndRedirectsToLibraryIndex()
    {
        await using var database = await TestDatabase.CreateAsync();
        var series = new MediaSeries("Spice and Wolf", MediaType.Book);
        var installment = new SeriesInstallment(series.Id, sequenceNumber: 1, title: "Vol 1", jitenCharacterCount: 50_000);
        series.Installments.Add(installment);

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
        installment.MediaWorkId = work.Id;
        installment.MediaWork = work;

        database.Context.MediaSeries.Add(series);
        database.Context.MediaWorks.Add(work);
        await database.Context.SaveChangesAsync();

        var model = CreatePageModel(database.Context);
        var result = await model.OnPostDeleteWorkAsync(work.Id, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Library/Index", redirect.PageName);
        Assert.Equal("Deleted “Spice and Wolf Vol 1” from library.", model.TempData["LibraryNotice"]);

        database.Context.ChangeTracker.Clear();

        Assert.Null(await database.Context.MediaWorks.FindAsync(work.Id));
        Assert.Empty(await database.Context.ImmersionLogs.Where(l => l.MediaWorkId == work.Id).ToListAsync());

        var persistedInstallment = await database.Context.SeriesInstallments.FindAsync(installment.Id);
        Assert.NotNull(persistedInstallment);
        Assert.Null(persistedInstallment.MediaWorkId);
    }

    [Fact]
    public async Task DeleteWork_NonExistentWork_RedirectsToLibraryIndexWithError()
    {
        await using var database = await TestDatabase.CreateAsync();
        var model = CreatePageModel(database.Context);
        var missingId = Guid.NewGuid();

        var result = await model.OnPostDeleteWorkAsync(missingId, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Library/Index", redirect.PageName);
        Assert.True(model.TempData.ContainsKey("LibraryError"));
        Assert.Contains("not found", (string)model.TempData["LibraryError"]!);
    }

    private static DetailsModel CreatePageModel(
        Kiseki.Core.ImmersionDbContext context,
        Kiseki.Core.Services.ILibraryManagementService? libraryService = null)
    {
        var httpContext = new DefaultHttpContext();
        return new DetailsModel(context, libraryService)
        {
            PageContext = new PageContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, new TestTempDataProvider())
        };
    }

    private sealed class TestTempDataProvider : ITempDataProvider
    {
        private IDictionary<string, object> _values = new Dictionary<string, object>();

        public IDictionary<string, object> LoadTempData(HttpContext context) => _values;

        public void SaveTempData(HttpContext context, IDictionary<string, object> values) =>
            _values = new Dictionary<string, object>(values);
    }


    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;

        private TestDatabase(Microsoft.Data.Sqlite.SqliteConnection connection, Kiseki.Core.ImmersionDbContext context)
        {
            _connection = connection;
            Context = context;
        }

        public Kiseki.Core.ImmersionDbContext Context { get; }

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<Kiseki.Core.ImmersionDbContext>()
                .UseSqlite(connection)
                .Options;
            var context = new Kiseki.Core.ImmersionDbContext(options);
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
