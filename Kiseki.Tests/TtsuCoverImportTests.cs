using Kiseki.Core;
using Kiseki.Core.DTOs;
using Kiseki.Core.Entities;
using Kiseki.Core.Models;
using Kiseki.Core.Models.Metadata;
using Kiseki.Core.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Kiseki.Web.Pages.Import;
using Kiseki.Web.Services;

namespace Kiseki.Tests;

public sealed class TtsuCoverImportTests
{
    [Theory]
    [InlineData("cover_123.jpeg", true)]
    [InlineData("cover_abc.jpg", true)]
    [InlineData("sub/dir/cover_xyz.jpeg", true)]
    [InlineData("COVER_UPPERCASE.JPEG", true)]
    [InlineData("cover_something.png", false)]
    [InlineData("cover_something.json", false)]
    [InlineData("statistics.json", false)]
    [InlineData("progress_1_1.json", false)]
    [InlineData("my_cover_1.jpeg", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void IsCoverFilename_ValidatesCoverFiles(string? filename, bool expected)
    {
        var result = TtsuDataLoader.IsCoverFilename(filename);
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task LoadDirectoryAsync_DetectsAndAssociatesFolderCover()
    {
        var loader = new TtsuDataLoader();
        var rootPath = Path.Combine(Path.GetTempPath(), "Kiseki.Tests", Guid.NewGuid().ToString("N"));
        var bookPath = Directory.CreateDirectory(Path.Combine(rootPath, "Cover Book")).FullName;

        try
        {
            var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ttsu-statistics.json");
            var fixture = await File.ReadAllTextAsync(fixturePath);
            await File.WriteAllTextAsync(Path.Combine(bookPath, "statistics.json"), fixture);
            var coverPath = Path.Combine(bookPath, "cover_123.jpeg");
            await File.WriteAllBytesAsync(coverPath, [0xFF, 0xD8, 0xFF, 0xE0]);

            var books = await loader.LoadDirectoryAsync(rootPath);

            var book = Assert.Single(books);
            Assert.Equal("Test Book", book.Title);
            Assert.Equal(coverPath, book.CoverImage);
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }

    [Fact]
    public void MediaWork_ApplyTtsuCover_SetsProvenanceAndProtectsCover()
    {
        var work = new MediaWork("Test Book");
        work.ApplyTtsuCover("/covers/test_cover.jpeg");

        Assert.Equal("/covers/test_cover.jpeg", work.CoverUrl);
        Assert.Equal(MediaCoverSource.Ttsu, work.CoverSource);
        Assert.True(work.IsCoverProtected);

        // Jiten link should NOT overwrite the TTSU cover
        work.LinkToJitenDeck(10, 50_000, "https://cdn.jiten.moe/jiten_cover.jpg", MediaCoverSource.JitenSpecific);
        Assert.Equal("/covers/test_cover.jpeg", work.CoverUrl);
        Assert.Equal(MediaCoverSource.Ttsu, work.CoverSource);
        Assert.Equal(10, work.JitenDeckId);

        // Google Books cover application should be rejected because cover is protected
        Assert.Throws<InvalidOperationException>(() =>
            work.ApplyGoogleBooksCover("https://books.google.com/cover.jpg", "vol123"));
    }

    [Fact]
    public async Task ApplyAsync_Bifurcation_PrioritizesFolderCoverAheadOfJiten()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ImmersionDbContext>().UseSqlite(connection).Options;
        await using var context = new ImmersionDbContext(options);
        await context.Database.EnsureCreatedAsync();

        var service = new TtsuImportService(context);

        var book = new TtsuBookContainer
        {
            Title = "Bifurcation Book",
            CoverImage = "/covers/local_folder_cover.jpeg",
            Entries =
            [
                new() { DateKey = "2026-09-20", CharactersRead = 500, ReadingTime = 300 }
            ]
        };

        var plan = await service.PreviewAsync(book, null);

        var jitenDeck = new JitenDeckDTO
        {
            DeckId = 42,
            OriginalTitle = "Bifurcation Book",
            CoverName = "https://cdn.jiten.moe/fallback_jiten_cover.jpg",
            CharacterCount = 60_000
        };
        var selection = JitenMediaSelection.FromDeck(jitenDeck);

        var request = new TtsuImportRequest(
            book,
            null,
            new Dictionary<DateOnly, string>(),
            plan.Fingerprint,
            Metadata: new TtsuMetadataImportRequest(selection));

        var receipt = await service.ApplyAsync(Guid.NewGuid(), [request]);

        Assert.Equal(1, receipt.Books);
        Assert.Equal(1, receipt.MetadataLinks);

        context.ChangeTracker.Clear();
        var work = await context.MediaWorks.SingleAsync();

        // Local folder cover MUST take precedence over Jiten cover!
        Assert.Equal("/covers/local_folder_cover.jpeg", work.CoverUrl);
        Assert.Equal(MediaCoverSource.Ttsu, work.CoverSource);
        Assert.Equal(42, work.JitenDeckId);
        Assert.Equal(60_000, work.JitenCharacterCount);
    }

    [Fact]
    public async Task ApplyAsync_Bifurcation_FallsBackToJitenWhenNoFolderCover()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ImmersionDbContext>().UseSqlite(connection).Options;
        await using var context = new ImmersionDbContext(options);
        await context.Database.EnsureCreatedAsync();

        var service = new TtsuImportService(context);

        var book = new TtsuBookContainer
        {
            Title = "No Cover Book",
            CoverImage = string.Empty, // No folder cover!
            Entries =
            [
                new() { DateKey = "2026-09-20", CharactersRead = 500, ReadingTime = 300 }
            ]
        };

        var plan = await service.PreviewAsync(book, null);

        var jitenDeck = new JitenDeckDTO
        {
            DeckId = 99,
            OriginalTitle = "No Cover Book",
            CoverName = "https://cdn.jiten.moe/fallback_jiten_cover.jpg",
            CharacterCount = 75_000
        };
        var selection = JitenMediaSelection.FromDeck(jitenDeck);

        var request = new TtsuImportRequest(
            book,
            null,
            new Dictionary<DateOnly, string>(),
            plan.Fingerprint,
            Metadata: new TtsuMetadataImportRequest(selection));

        var receipt = await service.ApplyAsync(Guid.NewGuid(), [request]);

        Assert.Equal(1, receipt.Books);
        Assert.Equal(1, receipt.MetadataLinks);

        context.ChangeTracker.Clear();
        var work = await context.MediaWorks.SingleAsync();

        // Since no folder cover was present, falls back to Jiten cover!
        Assert.Equal("https://cdn.jiten.moe/fallback_jiten_cover.jpg", work.CoverUrl);
        Assert.Equal(MediaCoverSource.JitenSpecific, work.CoverSource);
        Assert.Equal(99, work.JitenDeckId);
    }

    [Fact]
    public void NormalizeCoverUrl_HandlesRootRelativeAndHttpsUrls()
    {
        var work = new MediaWork("Test Book");
        work.ApplyTtsuCover("/covers/my_cover.jpeg");
        Assert.Equal("/covers/my_cover.jpeg", work.CoverUrl);

        work.UpdateCoverUrl("https://example.com/valid.jpg");
        Assert.Equal("https://example.com/valid.jpg", work.CoverUrl);

        Assert.Throws<ArgumentException>(() => work.UpdateCoverUrl("nocover.jpg"));
        Assert.Throws<ArgumentException>(() => work.UpdateCoverUrl("http://insecure.com/cover.jpg"));
        Assert.Throws<ArgumentException>(() => work.UpdateCoverUrl(""));
    }

    [Fact]
    public async Task WebPreviewAndConfirm_UploadsAndPersistsFolderCover()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ImmersionDbContext>().UseSqlite(connection).Options;
        await using var context = new ImmersionDbContext(options);
        await context.Database.EnsureCreatedAsync();

        var tempWebRoot = Path.Combine(Path.GetTempPath(), "Kiseki.WebTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempWebRoot);

        try
        {
            var envMock = new StubHostEnvironment { WebRootPath = tempWebRoot };
            var batchStore = new TtsuImportBatchStore(new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));
            var dataLoader = new TtsuDataLoader();
            var matchService = new TtsuImportPageTests.StubJitenMatchService();
            var resolver = new TtsuImportPageTests.StubJitenSelectionResolver();

            var httpContext = new DefaultHttpContext();
            var model = new TtsuModel(
                dataLoader,
                batchStore,
                context,
                matchService,
                resolver,
                environment: envMock)
            {
                PageContext = new Microsoft.AspNetCore.Mvc.RazorPages.PageContext { HttpContext = httpContext },
                TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(httpContext, new TestTempDataProvider())
            };

            var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ttsu-statistics.json");
            using var statStream = File.OpenRead(fixturePath);
            using var coverStream = new MemoryStream([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10]);

            var statFile = new FormFile(statStream, 0, statStream.Length, "FolderFiles", "ttu-reader-data/Test Book/statistics.json");
            var coverFile = new FormFile(coverStream, 0, coverStream.Length, "FolderFiles", "ttu-reader-data/Test Book/cover_test.jpeg");

            model.FolderFiles = [statFile, coverFile];
            await model.OnPostPreviewAsync(CancellationToken.None);

            var book = Assert.Single(model.Books);
            Assert.NotNull(book.CoverUrl);
            Assert.StartsWith("/covers/cover_", book.CoverUrl);
            Assert.EndsWith(".jpeg", book.CoverUrl);

            // Verify file was written to disk
            var writtenFile = Path.Combine(tempWebRoot, book.CoverUrl.TrimStart('/'));
            Assert.True(File.Exists(writtenFile));

            // Confirm import
            await model.OnPostConfirmAsync(CancellationToken.None);

            context.ChangeTracker.Clear();
            var work = await context.MediaWorks.SingleAsync();
            Assert.Equal(book.CoverUrl, work.CoverUrl);
            Assert.Equal(MediaCoverSource.Ttsu, work.CoverSource);
        }
        finally
        {
            if (Directory.Exists(tempWebRoot))
            {
                Directory.Delete(tempWebRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task WebPreview_WithStatisticsProgressAndCover_AssociatesCoverAndProgressCorrectly()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ImmersionDbContext>().UseSqlite(connection).Options;
        await using var context = new ImmersionDbContext(options);
        await context.Database.EnsureCreatedAsync();

        var tempWebRoot = Path.Combine(Path.GetTempPath(), "Kiseki.WebTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempWebRoot);

        try
        {
            var envMock = new StubHostEnvironment { WebRootPath = tempWebRoot };
            var batchStore = new TtsuImportBatchStore(new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));
            var dataLoader = new TtsuDataLoader();
            var matchService = new TtsuImportPageTests.StubJitenMatchService();
            var resolver = new TtsuImportPageTests.StubJitenSelectionResolver();

            var httpContext = new DefaultHttpContext();
            var model = new TtsuModel(
                dataLoader,
                batchStore,
                context,
                matchService,
                resolver,
                environment: envMock)
            {
                PageContext = new Microsoft.AspNetCore.Mvc.RazorPages.PageContext { HttpContext = httpContext },
                TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(httpContext, new TestTempDataProvider())
            };

            var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ttsu-statistics.json");
            using var statStream = File.OpenRead(fixturePath);
            using var coverStream = new MemoryStream([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10]);
            var progressJson = System.Text.Encoding.UTF8.GetBytes("{\"exploredCharCount\":12000,\"progress\":0.24}");
            using var progressStream = new MemoryStream(progressJson);

            var statFile = new FormFile(statStream, 0, statStream.Length, "FolderFiles", "backup/Book A/statistics_1_6.json");
            var progressFile = new FormFile(progressStream, 0, progressStream.Length, "FolderFiles", "backup/Book A/progress_1_6_2026.json");
            var coverFile = new FormFile(coverStream, 0, coverStream.Length, "FolderFiles", "backup/Book A/cover_1_6.jpeg");

            model.FolderFiles = [statFile, progressFile, coverFile];
            await model.OnPostPreviewAsync(CancellationToken.None);

            var book = Assert.Single(model.Books);
            Assert.NotNull(book.CoverUrl);
            Assert.StartsWith("/covers/cover_", book.CoverUrl);
            Assert.EndsWith(".jpeg", book.CoverUrl);
            Assert.Equal(12000, book.CurrentPosition);

            // Confirm import
            await model.OnPostConfirmAsync(CancellationToken.None);

            context.ChangeTracker.Clear();
            var work = await context.MediaWorks.SingleAsync();
            Assert.Equal(book.CoverUrl, work.CoverUrl);
            Assert.Equal(MediaCoverSource.Ttsu, work.CoverSource);
        }
        finally
        {
            if (Directory.Exists(tempWebRoot))
            {
                Directory.Delete(tempWebRoot, recursive: true);
            }
        }
    }

    private sealed class StubHostEnvironment : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Kiseki.Web";
        public string WebRootPath { get; set; } = string.Empty;
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed class TestTempDataProvider : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        private Dictionary<string, object> _values = [];
        public IDictionary<string, object> LoadTempData(HttpContext context) => _values;
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) => _values = new(values);
    }
}
