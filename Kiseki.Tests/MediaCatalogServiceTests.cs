using Kiseki.Core;
using Kiseki.Core.Entities;
using Kiseki.Core.Models;
using Kiseki.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Kiseki.Tests;

public sealed class MediaCatalogServiceTests
{
    [Fact]
    public async Task CreateAndSeriesAssignment_CreateInstallmentsAndSynchronizeAllLegacyCopies()
    {
        await using var database = await Database.CreateAsync();
        var catalog = new MediaCatalogService(database.Context);
        var first = await catalog.CreateTrackedCopyAsync("Volume 1 ebook", MediaType.Book);
        var firstInstallmentId = first.MediaInstallmentId;
        await database.Context.SaveChangesAsync();

        var second = await catalog.CreateTrackedCopyAsync("Volume 1 paperback", MediaType.Book, firstInstallmentId);
        var series = new MediaSeries("Novel series", MediaType.Book);
        database.Context.MediaSeries.Add(series);
        await catalog.AssignCopyToSeriesAsync(first, series.Id);
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();

        var copies = await database.Context.MediaWorks.AsNoTracking()
            .OrderBy(copy => copy.Title).ToListAsync();
        Assert.All(copies, copy =>
        {
            Assert.Equal(firstInstallmentId, copy.MediaInstallmentId);
            Assert.Equal(series.Id, copy.MediaSeriesId);
        });
        Assert.Equal(series.Id, await database.Context.MediaInstallments
            .Where(installment => installment.Id == firstInstallmentId)
            .Select(installment => installment.MediaSeriesId).SingleAsync());
    }

    [Fact]
    public async Task JitenRelinkAndUnlink_ReplaceThenRemoveCanonicalIdentityWithoutChangingProtectedCover()
    {
        await using var database = await Database.CreateAsync();
        var catalog = new MediaCatalogService(database.Context);
        var copy = await catalog.CreateTrackedCopyAsync("Local", MediaType.Book);
        copy.UpdateCoverUrl("https://example.com/selected.jpg");
        await catalog.LinkToJitenAsync(copy, Selection(deckId: 10, subdeckId: null, title: "Provider title"));
        await database.Context.SaveChangesAsync();

        await catalog.LinkToJitenAsync(copy, Selection(deckId: 20, subdeckId: 21, title: "Provider volume"));
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();

        Assert.Equal(["subdeck:20:21"], await database.Context.InstallmentProviderIdentities.AsNoTracking()
            .Select(identity => identity.NormalizedKey).ToArrayAsync());
        var relinked = await database.Context.MediaWorks.SingleAsync();
        Assert.Equal(20, relinked.JitenDeckId);
        Assert.Equal(21, relinked.JitenSubdeckId);
        Assert.Equal("https://example.com/selected.jpg", relinked.CoverUrl);

        await catalog.UnlinkFromJitenAsync(relinked);
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();
        Assert.Empty(await database.Context.InstallmentProviderIdentities.AsNoTracking().ToListAsync());
        Assert.False((await database.Context.MediaWorks.SingleAsync()).HasJitenLink);
    }

    [Fact]
    public async Task MediaTypeChange_DetachesCopyToACompatibleStandaloneInstallment()
    {
        await using var database = await Database.CreateAsync();
        var catalog = new MediaCatalogService(database.Context);
        var copy = await catalog.CreateTrackedCopyAsync("Book", MediaType.Book);
        var originalInstallmentId = copy.MediaInstallmentId;
        await catalog.LinkToJitenAsync(copy, Selection(deckId: 10, subdeckId: null, title: "Book"));
        await database.Context.SaveChangesAsync();

        await catalog.ChangeCopyMediaTypeAsync(copy, MediaType.Anime);
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();

        var changed = await database.Context.MediaWorks.SingleAsync();
        Assert.Equal(MediaType.Anime, changed.MediaType);
        Assert.NotEqual(originalInstallmentId, changed.MediaInstallmentId);
        Assert.Null(changed.MediaSeriesId);
        Assert.Equal(MediaType.Anime, await database.Context.MediaInstallments
            .Where(installment => installment.Id == changed.MediaInstallmentId)
            .Select(installment => installment.MediaType).SingleAsync());
        Assert.False(changed.HasJitenLink);
        Assert.Empty(await database.Context.InstallmentProviderIdentities.AsNoTracking().ToListAsync());
    }

    private static JitenMediaSelection Selection(int deckId, int? subdeckId, string title) => new(
        deckId, subdeckId, title, string.Empty, string.Empty, 50_000,
        "https://cdn.jiten.moe/cover.jpg", 0, JitenCoverEvidence.Specific);

    private sealed class Database(SqliteConnection connection, ImmersionDbContext context) : IAsyncDisposable
    {
        public ImmersionDbContext Context { get; } = context;

        public static async Task<Database> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var context = new ImmersionDbContext(new DbContextOptionsBuilder<ImmersionDbContext>()
                .UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync();
            return new Database(connection, context);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
