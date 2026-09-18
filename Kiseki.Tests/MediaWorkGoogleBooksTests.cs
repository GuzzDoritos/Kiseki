using Kiseki.Core.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Kiseki.Tests;

public sealed class MediaWorkGoogleBooksTests
{
    [Fact]
    public void ApplyGoogleBooksCover_ValidInputs_SetsPropertiesAndProvenance()
    {
        var work = new MediaWork("Test Book");
        work.ApplyGoogleBooksCover("https://books.google.com/cover.jpg", "vol_123-abc.01~");

        Assert.Equal("https://books.google.com/cover.jpg", work.CoverUrl);
        Assert.Equal(MediaCoverSource.GoogleBooks, work.CoverSource);
        Assert.Equal("vol_123-abc.01~", work.CoverProviderItemId);
        Assert.True(work.HasCover);
        Assert.False(work.IsCoverProtected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ApplyGoogleBooksCover_ThrowsOnEmptyVolumeId(string? volumeId)
    {
        var work = new MediaWork("Test Book");

        Assert.Throws<ArgumentException>(() =>
            work.ApplyGoogleBooksCover("https://books.google.com/cover.jpg", volumeId!));
    }

    [Theory]
    [InlineData("has spaces")]
    [InlineData("has/slashes")]
    [InlineData("has?query")]
    [InlineData("has#hash")]
    [InlineData("has@at")]
    public void ApplyGoogleBooksCover_ThrowsOnNonUrlSafeVolumeId(string volumeId)
    {
        var work = new MediaWork("Test Book");

        Assert.Throws<ArgumentException>(() =>
            work.ApplyGoogleBooksCover("https://books.google.com/cover.jpg", volumeId));
    }

    [Fact]
    public void ApplyGoogleBooksCover_RejectsUnicodeProviderId()
    {
        var work = new MediaWork("Test Book");

        Assert.Throws<ArgumentException>(() =>
            work.ApplyGoogleBooksCover("https://books.google.com/cover.jpg", "日本語"));
    }

    [Theory]
    [InlineData("https://example.com/cover.jpg")]
    [InlineData("https://books.google.com.attacker.example/cover.jpg")]
    public void ApplyGoogleBooksCover_RejectsDisallowedImageHostWithoutMutation(string coverUrl)
    {
        var work = new MediaWork("Test Book");
        work.LinkToJitenDeck(
            10,
            50_000,
            "https://cdn.jiten.moe/cover.jpg",
            MediaCoverSource.JitenParentFallback);

        Assert.Throws<ArgumentException>(() =>
            work.ApplyGoogleBooksCover(coverUrl, "volume123"));

        Assert.Equal("https://cdn.jiten.moe/cover.jpg", work.CoverUrl);
        Assert.Equal(MediaCoverSource.JitenParentFallback, work.CoverSource);
        Assert.Null(work.CoverProviderItemId);
    }

    [Fact]
    public void ApplyGoogleBooksCover_ThrowsOnOverlongVolumeId()
    {
        var work = new MediaWork("Test Book");
        var overlong = new string('a', 129);

        Assert.Throws<ArgumentException>(() =>
            work.ApplyGoogleBooksCover("https://books.google.com/cover.jpg", overlong));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://books.google.com/cover.jpg")]
    [InlineData("ftp://books.google.com/cover.jpg")]
    [InlineData("not-a-url")]
    public void ApplyGoogleBooksCover_ThrowsOnInvalidOrNonHttpsCoverUrl(string? coverUrl)
    {
        var work = new MediaWork("Test Book");

        Assert.Throws<ArgumentException>(() =>
            work.ApplyGoogleBooksCover(coverUrl!, "vol123"));
    }

    [Fact]
    public void ApplyGoogleBooksCover_OnUserOverrideProtectedCover_ThrowsInvalidOperationException()
    {
        var work = new MediaWork("Test Book");
        work.UpdateCoverUrl("https://example.com/user.jpg");
        Assert.True(work.IsCoverProtected);

        Assert.Throws<InvalidOperationException>(() =>
            work.ApplyGoogleBooksCover("https://books.google.com/cover.jpg", "vol123"));

        Assert.Equal("https://example.com/user.jpg", work.CoverUrl);
        Assert.Equal(MediaCoverSource.UserOverride, work.CoverSource);
        Assert.Null(work.CoverProviderItemId);
    }

    [Fact]
    public void ApplyGoogleBooksCover_OnLegacyUnknownProtectedCover_ThrowsInvalidOperationException()
    {
        var work = new MediaWork("Test Book");
        TestCoverState.SetLegacyUnknown(work, "https://example.com/legacy.jpg");
        Assert.True(work.IsCoverProtected);

        Assert.Throws<InvalidOperationException>(() =>
            work.ApplyGoogleBooksCover("https://books.google.com/cover.jpg", "vol123"));

        Assert.Equal("https://example.com/legacy.jpg", work.CoverUrl);
        Assert.Equal(MediaCoverSource.LegacyUnknown, work.CoverSource);
        Assert.Null(work.CoverProviderItemId);
    }

    [Fact]
    public void UpdateCoverUrl_ClearsCoverProviderItemIdAndSetsUserOverride()
    {
        var work = new MediaWork("Test Book");
        work.ApplyGoogleBooksCover("https://books.google.com/cover.jpg", "vol123");
        Assert.Equal(MediaCoverSource.GoogleBooks, work.CoverSource);
        Assert.Equal("vol123", work.CoverProviderItemId);

        work.UpdateCoverUrl("https://example.com/new.jpg");

        Assert.Equal("https://example.com/new.jpg", work.CoverUrl);
        Assert.Equal(MediaCoverSource.UserOverride, work.CoverSource);
        Assert.Null(work.CoverProviderItemId);
        Assert.True(work.IsCoverProtected);
    }

    [Fact]
    public void RemoveJitenLink_RetainsGoogleBooksCoverAndProviderItemId()
    {
        var work = new MediaWork("Test Book");
        work.ApplyGoogleBooksCover("https://books.google.com/cover.jpg", "vol123");
        // Link to Jiten deck without cover
        work.LinkToJitenDeck(100, 50_000, coverUrl: null, coverSource: MediaCoverSource.None);

        Assert.Equal(100, work.JitenDeckId);
        Assert.Equal(MediaCoverSource.GoogleBooks, work.CoverSource);
        Assert.Equal("https://books.google.com/cover.jpg", work.CoverUrl);
        Assert.Equal("vol123", work.CoverProviderItemId);

        work.RemoveJitenLink();

        Assert.Null(work.JitenDeckId);
        Assert.Equal(MediaCoverSource.GoogleBooks, work.CoverSource);
        Assert.Equal("https://books.google.com/cover.jpg", work.CoverUrl);
        Assert.Equal("vol123", work.CoverProviderItemId);
    }

    [Fact]
    public void LinkToJitenDeck_WithJitenCover_DoesNotOverwriteGoogleBooksCover()
    {
        var work = new MediaWork("Test Book");
        work.ApplyGoogleBooksCover("https://books.google.com/cover.jpg", "vol123");

        work.LinkToJitenDeck(100, 50_000, "https://cdn.jiten.moe/cover.jpg", MediaCoverSource.JitenSpecific);

        Assert.Equal(100, work.JitenDeckId);
        Assert.Equal(50_000, work.TotalCharacters);
        Assert.Equal(MediaCoverSource.GoogleBooks, work.CoverSource);
        Assert.Equal("https://books.google.com/cover.jpg", work.CoverUrl);
        Assert.Equal("vol123", work.CoverProviderItemId);
    }

    [Fact]
    public async Task Persistence_PersistsAndLoadsGoogleBooksCoverSuccessfully()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<Kiseki.Core.ImmersionDbContext>()
            .UseSqlite(connection)
            .Options;

        await using (var context = new Kiseki.Core.ImmersionDbContext(options))
        {
            await context.Database.EnsureCreatedAsync();
            var work = new MediaWork("Google Books Work");
            work.ApplyGoogleBooksCover("https://books.google.com/cover.jpg", "vol_xyz");
            context.MediaWorks.Add(work);
            await context.SaveChangesAsync();
        }

        await using (var context = new Kiseki.Core.ImmersionDbContext(options))
        {
            var loaded = await context.MediaWorks.SingleAsync(w => w.Title == "Google Books Work");
            Assert.Equal("https://books.google.com/cover.jpg", loaded.CoverUrl);
            Assert.Equal(MediaCoverSource.GoogleBooks, loaded.CoverSource);
            Assert.Equal("vol_xyz", loaded.CoverProviderItemId);
        }
    }

    [Fact]
    public async Task Persistence_EnforcesCheckConstraint_CoverProviderItemIdRequiredWhenSourceIsGoogleBooks()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<Kiseki.Core.ImmersionDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new Kiseki.Core.ImmersionDbContext(options);
        await context.Database.EnsureCreatedAsync();

        // Direct SQL inserting CoverSource = 5 (GoogleBooks) with NULL CoverProviderItemId
        var workId = Guid.NewGuid();
        var exception = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(async () =>
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO \"MediaWorks\" (\"Id\", \"Title\", \"MediaType\", \"CoverSource\", \"CoverUrl\", \"CoverProviderItemId\", \"IsCompleted\") VALUES ({workId}, 'Invalid', 0, 5, 'https://example.com/cover.jpg', NULL, 0)");
        });

        Assert.Contains("CHECK constraint failed", exception.Message);
    }

    [Fact]
    public async Task Persistence_EnforcesCheckConstraint_CoverProviderItemIdMustBeNullWhenSourceIsNotGoogleBooks()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<Kiseki.Core.ImmersionDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new Kiseki.Core.ImmersionDbContext(options);
        await context.Database.EnsureCreatedAsync();

        // Direct SQL inserting CoverSource = 1 (JitenSpecific) with non-null CoverProviderItemId
        var workId = Guid.NewGuid();
        var exception = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(async () =>
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO \"MediaWorks\" (\"Id\", \"Title\", \"MediaType\", \"CoverSource\", \"CoverUrl\", \"CoverProviderItemId\", \"IsCompleted\") VALUES ({workId}, 'Invalid', 0, 1, 'https://example.com/cover.jpg', 'vol123', 0)");
        });

        Assert.Contains("CHECK constraint failed", exception.Message);
    }
}
