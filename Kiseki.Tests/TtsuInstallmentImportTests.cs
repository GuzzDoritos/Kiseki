using Kiseki.Core.Entities;
using Kiseki.Core.Models;
using Kiseki.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Kiseki.Tests;

public sealed class TtsuInstallmentImportTests
{
    [Fact]
    public async Task ExactProviderCatalogueEntry_CreatesOneLinkedCopy_AndReplayIsIdempotent()
    {
        await using var db = await ImportDatabase.CreateAsync();
        var installment = AddJitenInstallment(db, "Volume 1", deckId: 10, subdeckId: 101);
        await db.Context.SaveChangesAsync();

        var book = TtsuMergeServiceTests.BookWithTitle(
            "Local Volume 1",
            TtsuMergeServiceTests.Entry(120, 1));
        book.ProgressEntries.Add(TtsuMergeServiceTests.Progress(500, 0.5, 1));
        var selection = TtsuMergeServiceTests.CreateSelection(10, 101, "Volume 1");
        var identity = TtsuProviderIdentityHint.FromJiten(selection);

        var targetReview = await db.Service.ReviewTargetAsync(book, identity);
        var choice = Assert.IsType<TtsuImportTargetChoice>(targetReview.SuggestedChoice);
        Assert.Equal(TtsuCopyIntent.NewCopyUnderExistingInstallment, choice.Intent);
        Assert.Equal(installment.Id, choice.InstallmentId);

        var plan = await db.Service.PreviewTargetAsync(book, choice, providerIdentity: identity);
        var operationId = Guid.NewGuid();
        var request = new TtsuImportRequest(
            book,
            null,
            new Dictionary<DateOnly, string>(),
            plan.Fingerprint,
            Metadata: new TtsuMetadataImportRequest(selection),
            TargetChoice: choice,
            ProviderIdentity: identity);

        var first = await db.Service.ApplyAsync(operationId, [request]);
        var replay = await db.Service.ApplyAsync(operationId, [request]);

        Assert.Equal(first.Id, replay.Id);
        db.Context.ChangeTracker.Clear();
        Assert.Single(await db.Context.MediaInstallments.ToListAsync());
        var copy = await db.Context.MediaWorks.Include(work => work.Logs).SingleAsync();
        Assert.Equal(installment.Id, copy.MediaInstallmentId);
        Assert.Equal(1000, copy.TtsuCharacterCount);
        Assert.Single(copy.Logs);
        Assert.NotNull(await db.Context.TtsuBindings.SingleOrDefaultAsync(binding => binding.MediaWorkId == copy.Id));
        Assert.Single(await db.Context.TtsuImportReceipts.ToListAsync());
    }

    [Fact]
    public async Task BoundEditionFromAnotherSource_ProducesANewCopyWithoutMergingHistory()
    {
        await using var db = await ImportDatabase.CreateAsync();
        var installment = AddJitenInstallment(db, "Volume 1", deckId: 10, subdeckId: 101);
        var catalog = new MediaCatalogService(db.Context);
        var oldCopy = await catalog.CreateTrackedCopyAsync("Old ebook", MediaType.Book, installment.Id);
        var oldLog = new ImmersionLog
        {
            Date = new DateOnly(2026, 1, 1),
            CharactersRead = 900,
            TimeSpentMinutes = 10,
            Source = "ttsu",
            MediaWorkId = oldCopy.Id,
            TtsuBindingId = oldCopy.Id
        };
        oldCopy.Logs.Add(oldLog);
        db.Context.TtsuBindings.Add(new TtsuBinding
        {
            MediaWorkId = oldCopy.Id,
            OriginalTitle = "different source",
            FolderHint = "old-folder"
        });
        await db.Context.SaveChangesAsync();

        var book = TtsuMergeServiceTests.BookWithTitle(
            "Volume 1",
            TtsuMergeServiceTests.Entry(100, 1));
        book.FolderHint = "new-folder";
        var selection = TtsuMergeServiceTests.CreateSelection(10, 101, "Volume 1");
        var identity = TtsuProviderIdentityHint.FromJiten(selection);
        var review = await db.Service.ReviewTargetAsync(book, identity);

        Assert.Equal(TtsuCopyIntent.NewCopyUnderExistingInstallment, review.SuggestedChoice!.Intent);
        var plan = await db.Service.PreviewTargetAsync(book, review.SuggestedChoice, providerIdentity: identity);
        await db.Service.ApplyAsync(Guid.NewGuid(), [new(
            book,
            null,
            new Dictionary<DateOnly, string>(),
            plan.Fingerprint,
            Metadata: new TtsuMetadataImportRequest(selection),
            TargetChoice: review.SuggestedChoice,
            ProviderIdentity: identity)]);

        db.Context.ChangeTracker.Clear();
        var copies = await db.Context.MediaWorks.Include(work => work.Logs).OrderBy(work => work.Id).ToListAsync();
        Assert.Equal(2, copies.Count);
        Assert.All(copies, copy => Assert.Equal(installment.Id, copy.MediaInstallmentId));
        Assert.Equal(900, copies.Single(copy => copy.Id == oldCopy.Id).Logs.Single().CharactersRead);
        Assert.Equal(100, copies.Single(copy => copy.Id != oldCopy.Id).Logs.Single().CharactersRead);
        Assert.Equal(2, await db.Context.TtsuBindings.CountAsync());
    }

    [Fact]
    public async Task AmbiguousTitleCopies_RequireAnExplicitChoice()
    {
        await using var db = await ImportDatabase.CreateAsync();
        var catalog = new MediaCatalogService(db.Context);
        await catalog.CreateTrackedCopyAsync("Same Book", MediaType.Book);
        await catalog.CreateTrackedCopyAsync("Same Book", MediaType.Book);
        await db.Context.SaveChangesAsync();

        var book = TtsuMergeServiceTests.BookWithTitle("Same Book", TtsuMergeServiceTests.Entry(10, 1));
        var review = await db.Service.ReviewTargetAsync(book);

        Assert.True(review.IsAmbiguous);
        Assert.Null(review.SuggestedChoice);
        Assert.Equal(2, review.CopyCandidates.Count);
    }

    [Fact]
    public async Task DuplicateLegacyProviderClaims_RequireExplicitCanonicalChoice()
    {
        await using var db = await ImportDatabase.CreateAsync();
        var catalog = new MediaCatalogService(db.Context);
        var first = await catalog.CreateTrackedCopyAsync("First edition", MediaType.Book);
        var second = await catalog.CreateTrackedCopyAsync("Second edition", MediaType.Book);
        first.LinkToJitenSubdeck(10, 101, 50_000);
        second.LinkToJitenSubdeck(10, 101, 50_000);
        await db.Context.SaveChangesAsync();

        var book = TtsuMergeServiceTests.BookWithTitle("Unrelated source title", TtsuMergeServiceTests.Entry(10, 1));
        var review = await db.Service.ReviewTargetAsync(
            book,
            new TtsuProviderIdentityHint("jiten", "subdeck:10:101"));

        Assert.True(review.IsAmbiguous);
        Assert.Null(review.SuggestedChoice);
        Assert.Equal(2, review.CopyCandidates.Count);
        Assert.Contains("legacy", review.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ChangedInstallmentVersion_InvalidatesNewCopyReviewWithoutWrites()
    {
        await using var db = await ImportDatabase.CreateAsync();
        var installment = new MediaInstallment("Volume 1", MediaType.Book);
        db.Context.MediaInstallments.Add(installment);
        await db.Context.SaveChangesAsync();
        var book = TtsuMergeServiceTests.BookWithTitle("Volume 1", TtsuMergeServiceTests.Entry(10, 1));
        var choice = TtsuImportTargetChoice.NewCopy(installment.Id);
        var plan = await db.Service.PreviewTargetAsync(book, choice);

        installment.Version = Guid.NewGuid();
        await db.Context.SaveChangesAsync();

        await Assert.ThrowsAsync<TtsuImportReviewRequiredException>(() => db.Service.ApplyAsync(
            Guid.NewGuid(),
            [new(book, null, new Dictionary<DateOnly, string>(), plan.Fingerprint, TargetChoice: choice)]));
        Assert.Empty(await db.Context.MediaWorks.ToListAsync());
        Assert.Empty(await db.Context.TtsuImportReceipts.ToListAsync());
    }

    [Fact]
    public async Task CompetingBinding_InvalidatesExistingCopyReview()
    {
        await using var db = await ImportDatabase.CreateAsync();
        var catalog = new MediaCatalogService(db.Context);
        var copy = await catalog.CreateTrackedCopyAsync("Book", MediaType.Book);
        await db.Context.SaveChangesAsync();
        var book = TtsuMergeServiceTests.Book(TtsuMergeServiceTests.Entry(10, 1));
        var choice = TtsuImportTargetChoice.ExistingCopy(copy.Id);
        var plan = await db.Service.PreviewTargetAsync(book, choice);

        db.Context.TtsuBindings.Add(new TtsuBinding
        {
            MediaWorkId = copy.Id,
            OriginalTitle = "competing source",
            FolderHint = "other-folder"
        });
        await db.Context.SaveChangesAsync();

        await Assert.ThrowsAsync<TtsuImportReviewRequiredException>(() => db.Service.ApplyAsync(
            Guid.NewGuid(),
            [new(book, copy.Id, new Dictionary<DateOnly, string>(), plan.Fingerprint, TargetChoice: choice)]));
        Assert.Empty(await db.Context.ImmersionLogs.ToListAsync());
        Assert.Empty(await db.Context.TtsuImportReceipts.ToListAsync());
    }

    [Fact]
    public async Task DuplicateSourceWithinOneBatch_IsRejectedBeforeCreatingCopies()
    {
        await using var db = await ImportDatabase.CreateAsync();
        var first = TtsuMergeServiceTests.Book(TtsuMergeServiceTests.Entry(10, 1));
        var second = TtsuMergeServiceTests.Book(TtsuMergeServiceTests.Entry(20, 2));
        var choice = TtsuImportTargetChoice.NewInstallment();
        var firstPlan = await db.Service.PreviewTargetAsync(first, choice);
        var secondPlan = await db.Service.PreviewTargetAsync(second, choice);

        await Assert.ThrowsAsync<TtsuImportReviewRequiredException>(() => db.Service.ApplyAsync(
            Guid.NewGuid(),
            [
                new(first, null, new Dictionary<DateOnly, string>(), firstPlan.Fingerprint, TargetChoice: choice),
                new(second, null, new Dictionary<DateOnly, string>(), secondPlan.Fingerprint, TargetChoice: choice)
            ]));

        Assert.Empty(await db.Context.MediaWorks.ToListAsync());
        Assert.Empty(await db.Context.MediaInstallments.ToListAsync());
        Assert.Empty(await db.Context.TtsuImportReceipts.ToListAsync());
    }

    private static MediaInstallment AddJitenInstallment(
        ImportDatabase db,
        string title,
        int deckId,
        int subdeckId)
    {
        var installment = new MediaInstallment(title, MediaType.Book);
        installment.ProviderIdentities.Add(new InstallmentProviderIdentity
        {
            Provider = "jiten",
            NormalizedKey = $"subdeck:{deckId}:{subdeckId}",
            MediaInstallment = installment,
            ProviderItemId = subdeckId,
            ParentProviderItemId = deckId
        });
        db.Context.MediaInstallments.Add(installment);
        return installment;
    }
}
