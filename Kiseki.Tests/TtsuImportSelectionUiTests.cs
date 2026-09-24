using System.Text;
using Kiseki.Core;
using Kiseki.Core.DTOs;
using Kiseki.Core.Entities;
using Kiseki.Core.Models;
using Kiseki.Core.Models.Metadata;
using Kiseki.Core.Services;
using Kiseki.Core.Services.Metadata;
using Kiseki.Web.Models;
using Kiseki.Web.Pages.Import;
using Kiseki.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Kiseki.Tests;

public sealed class TtsuImportSelectionUiTests
{
    [Fact]
    public async Task CatalogueOnly_Volume_SuggestedAsNewCopy_AndConfirmedLeavesOneInstallmentAndAddsLibraryCopy()
    {
        // 1. Catalogue-only "Test Book", canonical identity "subdeck:10:15", zero copies:
        // show "New copy under Test Book" as the provider-backed suggestion;
        // confirmation leaves one installment and adds one Library copy.
        await using var database = await TestDatabase.CreateAsync();
        var installment = new MediaInstallment("Test Book", MediaType.Book);
        installment.ProviderIdentities.Add(new InstallmentProviderIdentity
        {
            Provider = "jiten",
            NormalizedKey = "subdeck:10:15",
            MediaInstallment = installment,
            ProviderItemId = 15,
            ParentProviderItemId = 10
        });
        database.Context.MediaInstallments.Add(installment);
        await database.Context.SaveChangesAsync();

        var candidate = CreateCandidate(10, subdeckId: 15, title: "Test Book");
        var matchService = new StubJitenMatchService
        {
            Handler = (requests, _, _) => Task.FromResult<IReadOnlyList<JitenMatchOutcome>>([
                CreateMatchedOutcome(requests.Single().CorrelationId, MatchConfidence.High, CreateScored(candidate))
            ])
        };
        var resolver = new StubJitenSelectionResolver
        {
            Handler = (deckId, subdeckId, _) => Task.FromResult(JitenSelectionResult.Succeeded(
                new JitenMediaSelection(
                    deckId,
                    subdeckId,
                    "Test Book",
                    string.Empty,
                    string.Empty,
                    50_000,
                    null,
                    0)))
        };

        using var fixture = File.OpenRead(GetFixturePath());
        var model = CreateModel(database.Context, matchService, resolver);
        model.AutoMatchMetadata = true;
        model.FolderFiles = [StatisticsFile(fixture)];
        await model.OnPostPreviewAsync(CancellationToken.None);
        await EnrichAllPendingAsync(model);

        // Assert review state
        Assert.Single(model.Books);
        var previewBook = model.Books[0];
        var plan = previewBook.Plan;
        Assert.NotNull(plan.TargetReview);
        var review = plan.TargetReview!;
        Assert.False(review.IsAmbiguous);
        Assert.NotNull(review.SuggestedChoice);
        Assert.Equal(TtsuCopyIntent.NewCopyUnderExistingInstallment, review.SuggestedChoice!.Intent);
        Assert.Equal(installment.Id, review.SuggestedChoice.InstallmentId);

        // Assert candidate records
        Assert.Empty(review.CopyCandidates);
        var instCandidate = Assert.Single(review.InstallmentCandidates);
        Assert.Equal(installment.Id, instCandidate.InstallmentId);
        Assert.Equal(0, instCandidate.CopyCount); // Catalogue only
        Assert.Contains("jiten:subdeck:10:15", instCandidate.ProviderKeys);

        // Assert selection model preselection
        var selection = Assert.Single(model.Selections);
        Assert.Equal(TtsuCopyIntent.NewCopyUnderExistingInstallment, selection.CopyIntent);
        Assert.Equal(installment.Id, selection.InstallmentId);
        Assert.Equal(TtsuImportMode.Create, selection.Mode);
        Assert.True(plan.CanApply);

        // Confirm
        var result = await model.OnPostConfirmAsync(CancellationToken.None);
        Assert.IsType<RedirectToPageResult>(result);

        database.Context.ChangeTracker.Clear();
        // Leaves exactly one installment and adds one copy
        var installments = await database.Context.MediaInstallments.ToListAsync();
        Assert.Single(installments);
        Assert.Equal(installment.Id, installments[0].Id);

        var copies = await database.Context.MediaWorks.Include(w => w.Logs).ToListAsync();
        Assert.Single(copies);
        Assert.Equal(installment.Id, copies[0].MediaInstallmentId);
        Assert.Equal(2, copies[0].Logs.Count);
        Assert.NotNull(await database.Context.TtsuBindings.SingleOrDefaultAsync(b => b.MediaWorkId == copies[0].Id));
    }

    [Fact]
    public async Task Reread_OldBoundCopyExists_DifferentSourceFolder_SuggestsNewCopyUnderInstallment_AndPreservesSeparateHistories()
    {
        // 2. Volume 1 has an old bound copy with a 900-character log and exact identity "subdeck:10:101";
        // a different TTSU folder shows "New copy under Volume 1", and confirmation leaves two bindings
        // and two separate histories.
        await using var database = await TestDatabase.CreateAsync();
        var installment = new MediaInstallment("Volume 1", MediaType.Book);
        installment.ProviderIdentities.Add(new InstallmentProviderIdentity
        {
            Provider = "jiten",
            NormalizedKey = "subdeck:10:101",
            MediaInstallment = installment,
            ProviderItemId = 101,
            ParentProviderItemId = 10
        });
        database.Context.MediaInstallments.Add(installment);

        var catalog = new MediaCatalogService(database.Context);
        var oldCopy = await catalog.CreateTrackedCopyAsync("Old edition", MediaType.Book, installment.Id);
        oldCopy.Logs.Add(new ImmersionLog
        {
            Date = new DateOnly(2026, 1, 1),
            CharactersRead = 900,
            TimeSpentMinutes = 15,
            Source = "ttsu",
            MediaWorkId = oldCopy.Id,
            TtsuBindingId = oldCopy.Id
        });
        database.Context.TtsuBindings.Add(new TtsuBinding
        {
            MediaWorkId = oldCopy.Id,
            OriginalTitle = "Volume 1",
            FolderHint = "old-folder"
        });
        await database.Context.SaveChangesAsync();

        var candidate = CreateCandidate(10, subdeckId: 101, title: "Volume 1");
        var matchService = new StubJitenMatchService
        {
            Handler = (requests, _, _) => Task.FromResult<IReadOnlyList<JitenMatchOutcome>>([
                CreateMatchedOutcomeWithTitle(requests.Single().CorrelationId, MatchConfidence.High, "Volume 1", CreateScored(candidate))
            ])
        };
        var resolver = new StubJitenSelectionResolver
        {
            Handler = (deckId, subdeckId, _) => Task.FromResult(JitenSelectionResult.Succeeded(
                new JitenMediaSelection(
                    deckId,
                    subdeckId,
                    "Volume 1",
                    string.Empty,
                    string.Empty,
                    60_000,
                    null,
                    0)))
        };

        var model = CreateModel(database.Context, matchService, resolver);
        model.AutoMatchMetadata = true;
        // Different folder hint, title matches Volume 1
        model.FolderFiles = [StatisticsFileWithTitle("Volume 1", folderTitle: "new-folder")];
        await model.OnPostPreviewAsync(CancellationToken.None);
        await EnrichAllPendingAsync(model);

        // Assert review suggests new copy under Volume 1
        var review = model.Books.Single().Plan.TargetReview!;
        Assert.NotNull(review.SuggestedChoice);
        Assert.Equal(TtsuCopyIntent.NewCopyUnderExistingInstallment, review.SuggestedChoice!.Intent);
        Assert.Equal(installment.Id, review.SuggestedChoice.InstallmentId);

        // Old copy is flagged with binding conflict (bound to old-folder)
        var copyCandidate = Assert.Single(review.CopyCandidates);
        Assert.Equal(oldCopy.Id, copyCandidate.WorkId);
        Assert.True(copyCandidate.HasBinding);
        Assert.False(copyCandidate.BindingMatchesSource); // Conflict!
        Assert.Equal(900, copyCandidate.LifetimeCharacters);

        // Confirm
        var result = await model.OnPostConfirmAsync(CancellationToken.None);
        if (result is PageResult)
        {
            var errors = string.Join("; ", model.ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
            Assert.Fail("OnPostConfirmAsync returned PageResult with errors: " + errors);
        }
        Assert.IsType<RedirectToPageResult>(result);

        database.Context.ChangeTracker.Clear();
        // Two copies, both linked to the installment, with separate histories
        var copies = await database.Context.MediaWorks
            .Include(w => w.Logs)
            .OrderBy(w => w.Id)
            .ToListAsync();
        Assert.Equal(2, copies.Count);
        Assert.All(copies, c => Assert.Equal(installment.Id, c.MediaInstallmentId));

        var retainedOldCopy = copies.Single(c => c.Id == oldCopy.Id);
        Assert.Equal(900, retainedOldCopy.Logs.Single().CharactersRead);

        var newCopy = copies.Single(c => c.Id != oldCopy.Id);
        Assert.Equal(18_610, newCopy.Logs.Sum(l => l.CharactersRead));

        Assert.Equal(2, await database.Context.TtsuBindings.CountAsync());
    }

    [Fact]
    public async Task AmbiguousTargets_TwoUnboundCopies_RendersAmbiguity_DoesNotPreselectWinner_AndBlocksConfirmationUntilExplicitChoice()
    {
        // 3. Two unbound copies titled "Same Book", or two legacy copies claiming subdeck:10:101:
        // render an ambiguity callout, do not preselect a winner, and disable confirmation until
        // the reviewed explicit choice has no Core error.
        await using var database = await TestDatabase.CreateAsync();
        var catalog = new MediaCatalogService(database.Context);
        var firstCopy = await catalog.CreateTrackedCopyAsync("Same Book", MediaType.Book);
        var secondCopy = await catalog.CreateTrackedCopyAsync("Same Book", MediaType.Book);
        await database.Context.SaveChangesAsync();

        var model = CreateModel(database.Context);
        model.FolderFiles = [StatisticsFileWithTitle("Same Book", folderTitle: "Same Book")];
        await model.OnPostPreviewAsync(CancellationToken.None);

        var preview = model.Books.Single();
        var review = preview.Plan.TargetReview!;
        Assert.True(review.IsAmbiguous);
        Assert.Null(review.SuggestedChoice);
        Assert.Equal(2, review.CopyCandidates.Count);

        // No winner preselected
        var selection = model.Selections.Single();
        Assert.Null(selection.CopyIntent);
        Assert.Null(selection.TargetId);
        Assert.Null(selection.InstallmentId);

        // Plan has error and CanApply is false
        Assert.NotNull(preview.Plan.Error);
        Assert.False(preview.Plan.CanApply);

        // Attempting to confirm without an explicit choice is rejected
        var confirmResult = await model.OnPostConfirmAsync(CancellationToken.None);
        Assert.IsType<PageResult>(confirmResult);
        Assert.False(model.ModelState.IsValid);

        // Clear ModelState before the next handler invocation on the same model instance
        model.ModelState.Clear();

        // Now user makes an explicit choice: ExistingCopy pointing to firstCopy
        model.Selections[0].CopyIntent = TtsuCopyIntent.ExistingCopy;
        model.Selections[0].TargetId = firstCopy.Id;
        model.Selections[0].Mode = TtsuImportMode.Merge;

        // Submits Review
        var reviewResult = await model.OnPostReviewAsync(CancellationToken.None);
        Assert.IsType<PageResult>(reviewResult);

        var refreshedPlan = model.Books.Single().Plan;
        Assert.Null(refreshedPlan.Error);
        Assert.True(refreshedPlan.CanApply);

        // Now confirmation succeeds
        var finalResult = await model.OnPostConfirmAsync(CancellationToken.None);
        if (finalResult is PageResult)
        {
            var errors = string.Join("; ", model.ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
            Assert.Fail("OnPostConfirmAsync returned PageResult with errors: " + errors);
        }
        Assert.IsType<RedirectToPageResult>(finalResult);

        database.Context.ChangeTracker.Clear();
        var mergedFirst = await database.Context.MediaWorks.Include(w => w.Logs).SingleAsync(w => w.Id == firstCopy.Id);
        Assert.Equal(2, mergedFirst.Logs.Count);
        Assert.Equal(18_610, mergedFirst.CurrentCharactersRead);
    }

    [Fact]
    public async Task TargetSelection_UnboundCopyShowsLifetimeChars_BoundCopyFromOtherSourceRejectsOverwrite()
    {
        // 4. A unique unbound copy displays Existing copy, its lifetime activity, and whether it is already bound.
        // A bound copy from another source cannot be chosen as an overwrite target; present the Core error
        // and separate-copy action.
        await using var database = await TestDatabase.CreateAsync();
        var catalog = new MediaCatalogService(database.Context);
        var boundCopy = await catalog.CreateTrackedCopyAsync("Bound Book", MediaType.Book);
        boundCopy.Logs.Add(new ImmersionLog
        {
            Date = new DateOnly(2026, 1, 1),
            CharactersRead = 500,
            TimeSpentMinutes = 10,
            Source = "ttsu",
            MediaWorkId = boundCopy.Id,
            TtsuBindingId = boundCopy.Id
        });
        database.Context.TtsuBindings.Add(new TtsuBinding
        {
            MediaWorkId = boundCopy.Id,
            OriginalTitle = "Bound Book",
            FolderHint = "other-source"
        });
        await database.Context.SaveChangesAsync();

        var model = CreateModel(database.Context);
        model.FolderFiles = [StatisticsFileWithTitle("Bound Book", folderTitle: "Bound Book")];
        await model.OnPostPreviewAsync(CancellationToken.None);

        var preview = model.Books.Single();
        var review = preview.Plan.TargetReview!;
        var candidate = Assert.Single(review.CopyCandidates);
        Assert.Equal(boundCopy.Id, candidate.WorkId);
        Assert.True(candidate.HasBinding);
        Assert.False(candidate.BindingMatchesSource); // Conflict
        Assert.Equal(500, candidate.LifetimeCharacters);

        // User forcibly chooses ExistingCopy pointing to the bound copy and refreshes review
        model.Selections[0].CopyIntent = TtsuCopyIntent.ExistingCopy;
        model.Selections[0].TargetId = boundCopy.Id;
        model.Selections[0].Mode = TtsuImportMode.Merge;
        await model.OnPostReviewAsync(CancellationToken.None);

        var conflictingPlan = model.Books.Single().Plan;
        Assert.NotNull(conflictingPlan.Error);
        Assert.Contains("different TTSU source", conflictingPlan.Error);
        Assert.False(conflictingPlan.CanApply);

        // Attempting to confirm the conflicting choice fails
        var confirmResult = await model.OnPostConfirmAsync(CancellationToken.None);
        Assert.IsType<PageResult>(confirmResult);
        Assert.False(model.ModelState.IsValid);

        // Clear ModelState before valid review
        model.ModelState.Clear();

        // User changes choice to NewCopyUnderExistingInstallment
        model.Selections[0].CopyIntent = TtsuCopyIntent.NewCopyUnderExistingInstallment;
        model.Selections[0].InstallmentId = boundCopy.MediaInstallmentId;
        model.Selections[0].TargetId = null;
        model.Selections[0].Mode = TtsuImportMode.Create;
        await model.OnPostReviewAsync(CancellationToken.None);

        var validPlan = model.Books.Single().Plan;
        Assert.Null(validPlan.Error);
        Assert.True(validPlan.CanApply);

        var finalResult = await model.OnPostConfirmAsync(CancellationToken.None);
        if (finalResult is PageResult)
        {
            var errors = string.Join("; ", model.ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
            Assert.Fail("OnPostConfirmAsync returned PageResult with errors: " + errors);
        }
        Assert.IsType<RedirectToPageResult>(finalResult);

        database.Context.ChangeTracker.Clear();
        var copies = await database.Context.MediaWorks.Where(w => w.MediaInstallmentId == boundCopy.MediaInstallmentId).ToListAsync();
        Assert.Equal(2, copies.Count);
    }

    [Fact]
    public async Task StaleReview_ModifiedTargetChoice_RequiresReviewAgain_AndReplayReturnsCommittedReceipt()
    {
        // 5. Changing copy/installment selection, metadata candidate, cover edition, orphan assignment,
        // daily resolution, or bookmark choice requires Review again. Expired batches show re-upload guidance;
        // stale target/binding/provider evidence shows review-again guidance; replayed committed operation IDs
        // show the existing success notice without another write.
        await using var database = await TestDatabase.CreateAsync();
        var catalog = new MediaCatalogService(database.Context);
        var firstCopy = await catalog.CreateTrackedCopyAsync("Target One", MediaType.Book);
        var secondCopy = await catalog.CreateTrackedCopyAsync("Target Two", MediaType.Book);
        await database.Context.SaveChangesAsync();

        using var fixture = File.OpenRead(GetFixturePath());
        var model = CreateModel(database.Context);
        model.FolderFiles = [StatisticsFile(fixture)];
        await model.OnPostPreviewAsync(CancellationToken.None);

        // User chooses firstCopy and refreshes review
        model.Selections[0].CopyIntent = TtsuCopyIntent.ExistingCopy;
        model.Selections[0].TargetId = firstCopy.Id;
        model.Selections[0].Mode = TtsuImportMode.Merge;
        await model.OnPostReviewAsync(CancellationToken.None);

        // Now simulate the user changing target to secondCopy without reviewing (stale token)
        model.Selections[0].TargetId = secondCopy.Id;
        var staleConfirmResult = await model.OnPostConfirmAsync(CancellationToken.None);

        // Confirmation rejects stale choice and requires review again
        Assert.IsType<PageResult>(staleConfirmResult);
        Assert.False(model.ModelState.IsValid);
        Assert.Contains(model.ModelState[string.Empty]!.Errors, e => e.ErrorMessage.Contains("Review the import again"));

        // Clear ModelState before subsequent valid review/confirm on the same model instance
        model.ModelState.Clear();

        // Refresh review with secondCopy
        await model.OnPostReviewAsync(CancellationToken.None);
        Assert.True(model.Books.Single().Plan.CanApply);

        // Confirm succeeds
        var confirmResult = await model.OnPostConfirmAsync(CancellationToken.None);
        if (confirmResult is PageResult)
        {
            var errors = string.Join("; ", model.ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
            Assert.Fail("OnPostConfirmAsync returned PageResult with errors: " + errors);
        }
        Assert.IsType<RedirectToPageResult>(confirmResult);

        database.Context.ChangeTracker.Clear();
        Assert.Equal(18_610, (await database.Context.MediaWorks.Include(w => w.Logs).SingleAsync(w => w.Id == secondCopy.Id)).CurrentCharactersRead);
        Assert.Equal(0, (await database.Context.MediaWorks.Include(w => w.Logs).SingleAsync(w => w.Id == firstCopy.Id)).CurrentCharactersRead);

        // Replay same batch ID: returns committed receipt without repeating work
        var replayModel = CreateModel(database.Context);
        replayModel.BatchId = model.BatchId;
        var replayResult = await replayModel.OnPostConfirmAsync(CancellationToken.None);
        Assert.IsType<RedirectToPageResult>(replayResult);

        // No new logs or works created on replay
        database.Context.ChangeTracker.Clear();
        Assert.Equal(2, await database.Context.MediaWorks.CountAsync());
    }

    [Fact]
    public async Task MultipleCopiesUnderInstallment_DisplaysAllCandidateCopies_AndAllowsExplicitSelection()
    {
        // An installment has multiple existing copies (e.g. Copy 1 and Copy 2).
        // TargetReview lists candidate copies under that installment with their lifetime stats and bindings.
        // User explicitly selects Copy 2 as ExistingCopy.
        // Confirmation updates Copy 2 while leaving Copy 1's history untouched.
        await using var database = await TestDatabase.CreateAsync();
        var installment = new MediaInstallment("Multi Volume", MediaType.Book);
        database.Context.MediaInstallments.Add(installment);

        var catalog = new MediaCatalogService(database.Context);
        var copyOne = await catalog.CreateTrackedCopyAsync("Multi Volume", MediaType.Book, installment.Id);
        copyOne.Logs.Add(new ImmersionLog
        {
            Date = new DateOnly(2026, 1, 1),
            CharactersRead = 5_000,
            TimeSpentMinutes = 60,
            Source = "ttsu",
            MediaWorkId = copyOne.Id,
            TtsuBindingId = copyOne.Id
        });
        database.Context.TtsuBindings.Add(new TtsuBinding
        {
            MediaWorkId = copyOne.Id,
            OriginalTitle = TtsuBookImporter.NormalizeTitle("Multi Volume"),
            FolderHint = "copy-one-source"
        });

        var copyTwo = await catalog.CreateTrackedCopyAsync("Multi Volume", MediaType.Book, installment.Id);
        copyTwo.Logs.Add(new ImmersionLog
        {
            Date = new DateOnly(2026, 2, 1),
            CharactersRead = 12_000,
            TimeSpentMinutes = 150,
            Source = "ttsu",
            MediaWorkId = copyTwo.Id,
            TtsuBindingId = copyTwo.Id
        });
        database.Context.TtsuBindings.Add(new TtsuBinding
        {
            MediaWorkId = copyTwo.Id,
            OriginalTitle = TtsuBookImporter.NormalizeTitle("Multi Volume"),
            FolderHint = "multi-source"
        });
        await database.Context.SaveChangesAsync();

        var model = CreateModel(database.Context);
        model.FolderFiles = [StatisticsFileWithTitle("Multi Volume", folderTitle: "multi-source")];
        await model.OnPostPreviewAsync(CancellationToken.None);

        var preview = model.Books.Single();
        var review = preview.Plan.TargetReview!;
        Assert.NotNull(review);

        // Copy candidates list both copies
        Assert.Contains(review.CopyCandidates, c => c.WorkId == copyOne.Id && c.LifetimeCharacters == 5_000 && !c.BindingMatchesSource);
        Assert.Contains(review.CopyCandidates, c => c.WorkId == copyTwo.Id && c.LifetimeCharacters == 12_000 && c.BindingMatchesSource);

        // Preselection targets copyTwo because its binding matches the incoming folderHint
        var selection = model.Selections.Single();
        Assert.Equal(TtsuCopyIntent.ExistingCopy, selection.CopyIntent);
        Assert.Equal(copyTwo.Id, selection.TargetId);

        // Confirm
        var result = await model.OnPostConfirmAsync(CancellationToken.None);
        Assert.IsType<RedirectToPageResult>(result);

        database.Context.ChangeTracker.Clear();
        // Verify copyOne is untouched (still 5,000 characters read)
        var updatedCopyOne = await database.Context.MediaWorks.Include(w => w.Logs).SingleAsync(w => w.Id == copyOne.Id);
        Assert.Equal(5_000, updatedCopyOne.CurrentCharactersRead);
        Assert.Single(updatedCopyOne.Logs);

        // Verify copyTwo has the new logs merged (12,000 + 18,610)
        var updatedCopyTwo = await database.Context.MediaWorks.Include(w => w.Logs).SingleAsync(w => w.Id == copyTwo.Id);
        Assert.Equal(12_000 + 18_610, updatedCopyTwo.CurrentCharactersRead);
        Assert.Equal(3, updatedCopyTwo.Logs.Count);
    }

    [Fact]
    public async Task MultipleBooksInBatch_DistinctTargetChoices_ImportsBothAtomicallyWithSeparateHistories()
    {
        // Batch with two books:
        // Book 1 targets a catalogue-only installment (NewCopyUnderExistingInstallment)
        // Book 2 targets an existing unbound copy (ExistingCopy)
        // Both are confirmed in a single atomic transaction, preserving separate histories.
        await using var database = await TestDatabase.CreateAsync();
        var installment = new MediaInstallment("Catalogue Installment", MediaType.Book);
        database.Context.MediaInstallments.Add(installment);

        var catalog = new MediaCatalogService(database.Context);
        var existingCopy = await catalog.CreateTrackedCopyAsync("Existing Book", MediaType.Book);
        existingCopy.Logs.Add(new ImmersionLog
        {
            Date = new DateOnly(2026, 1, 15),
            CharactersRead = 3_000,
            TimeSpentMinutes = 40,
            Source = "ttsu",
            MediaWorkId = existingCopy.Id
        });
        await database.Context.SaveChangesAsync();

        var model = CreateModel(database.Context);
        model.FolderFiles =
        [
            StatisticsFileWithTitle("Catalogue Installment", folderTitle: "Book A"),
            StatisticsFileWithTitle("Existing Book", folderTitle: "Book B")
        ];
        await model.OnPostPreviewAsync(CancellationToken.None);

        Assert.Equal(2, model.Books.Count);
        Assert.Equal(2, model.Selections.Count);

        // Book 0: Catalogue Installment -> choose NewCopyUnderExistingInstallment
        model.Selections[0].CopyIntent = TtsuCopyIntent.NewCopyUnderExistingInstallment;
        model.Selections[0].InstallmentId = installment.Id;
        model.Selections[0].TargetId = null;
        model.Selections[0].Mode = TtsuImportMode.Create;

        // Book 1: Existing Book -> choose ExistingCopy
        model.Selections[1].CopyIntent = TtsuCopyIntent.ExistingCopy;
        model.Selections[1].TargetId = existingCopy.Id;
        model.Selections[1].InstallmentId = null;
        model.Selections[1].Mode = TtsuImportMode.Merge;

        // Submit review
        var reviewResult = await model.OnPostReviewAsync(CancellationToken.None);
        Assert.IsType<PageResult>(reviewResult);

        Assert.True(model.Books[0].Plan.CanApply);
        Assert.True(model.Books[1].Plan.CanApply);

        // Confirm both
        var confirmResult = await model.OnPostConfirmAsync(CancellationToken.None);
        if (confirmResult is PageResult)
        {
            var errors = string.Join("; ", model.ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
            Assert.Fail("OnPostConfirmAsync returned PageResult with errors: " + errors);
        }
        Assert.IsType<RedirectToPageResult>(confirmResult);

        database.Context.ChangeTracker.Clear();
        // Book 0 created a new copy under installment
        var newCopy = await database.Context.MediaWorks
            .Include(w => w.Logs)
            .SingleAsync(w => w.MediaInstallmentId == installment.Id);
        Assert.Equal("Catalogue Installment", newCopy.Title);
        Assert.Equal(18_610, newCopy.CurrentCharactersRead);

        // Book 1 merged into existingCopy
        var updatedExisting = await database.Context.MediaWorks
            .Include(w => w.Logs)
            .SingleAsync(w => w.Id == existingCopy.Id);
        Assert.Equal(3_000 + 18_610, updatedExisting.CurrentCharactersRead);
        Assert.Equal(3, updatedExisting.Logs.Count);

        // Both bindings created independently
        Assert.Equal(2, await database.Context.TtsuBindings.CountAsync());
    }

    [Fact]
    public void RazorMarkup_ContainsRequiredAccessibilityAndBatch4BContractElements()
    {
        var candidatePaths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Kiseki.Web", "Pages", "Import", "Ttsu.cshtml"),
            Path.Combine(AppContext.BaseDirectory, "Kiseki.Web", "Pages", "Import", "Ttsu.cshtml")
        };
        var razorPath = candidatePaths.FirstOrDefault(File.Exists);
        if (razorPath is null)
        {
            var root = Directory.GetCurrentDirectory();
            while (root is not null && !File.Exists(Path.Combine(root, "Kiseki.slnx")))
            {
                root = Directory.GetParent(root)?.FullName;
            }
            if (root is not null)
            {
                razorPath = Path.Combine(root, "Kiseki.Web", "Pages", "Import", "Ttsu.cshtml");
            }
        }

        Assert.NotNull(razorPath);
        Assert.True(File.Exists(razorPath));

        var markup = File.ReadAllText(razorPath);

        // Required fieldset / legend
        Assert.Contains("fieldset class=\"ttsu-target-fieldset\"", markup);
        Assert.Contains("legend class=\"ttsu-target-legend\"", markup);
        Assert.Contains("Target installment &amp; copy", markup);
        Assert.Contains("role=\"radiogroup\"", markup);

        // 3 distinct actions
        Assert.Contains("value=\"@TtsuCopyIntent.ExistingCopy\"", markup);
        Assert.Contains("value=\"@TtsuCopyIntent.NewCopyUnderExistingInstallment\"", markup);
        Assert.Contains("value=\"@TtsuCopyIntent.NewInstallmentAndCopy\"", markup);
        Assert.Contains("Update existing copy", markup);
        Assert.Contains("New copy under existing installment", markup);
        Assert.Contains("New installment &amp; copy", markup);

        // Ambiguity and conflict callouts
        Assert.Contains("ttsu-target-ambiguous", markup);
        Assert.Contains("role=\"alert\"", markup);
        Assert.Contains("Bound to another source (conflict)", markup);
        Assert.Contains("Catalogue only (0 copies)", markup);

        // Accessible buttons, descriptors, and live hints
        Assert.Contains("data-ttsu-confirm-btn", markup);
        Assert.Contains("data-ttsu-review-btn", markup);
        Assert.Contains("data-ttsu-mode-input", markup);
        Assert.Contains("data-ttsu-target-select", markup);
        Assert.Contains("data-ttsu-installment-select", markup);
        Assert.Contains("data-ttsu-confirm-hint", markup);
        Assert.Contains("disabled=\"@(currentIntent != TtsuCopyIntent.ExistingCopy", markup);
        Assert.Contains("disabled=\"@(currentIntent != TtsuCopyIntent.NewCopyUnderExistingInstallment", markup);
        Assert.Contains("aria-describedby=\"intent_existing_desc_", markup);
        Assert.Contains("aria-describedby=\"intent_newcopy_desc_", markup);
        Assert.Contains("aria-describedby=\"intent_newinst_desc_", markup);

        var repositoryRoot = Directory.GetParent(Directory.GetParent(Directory.GetParent(Directory.GetParent(razorPath)!.FullName)!.FullName)!.FullName)!.FullName;
        var scriptPath = Path.Combine(repositoryRoot, "Kiseki.Web", "wwwroot", "js", "site.js");
        Assert.True(File.Exists(scriptPath));
        var script = File.ReadAllText(scriptPath);
        Assert.Contains("targetSelect.disabled = !isActive", script);
        Assert.Contains("if (!isActive) targetSelect.value = ''", script);
        Assert.Contains("installmentSelect.disabled = !isActive", script);
        Assert.Contains("if (!isActive) installmentSelect.value = ''", script);
    }

    #region Helpers

    private static JitenMatchCandidate CreateCandidate(
        int deckId,
        int? subdeckId = null,
        string title = "Test Match",
        int characters = 50_000,
        string? coverUrl = "https://example.com/cover.jpg",
        JitenCoverEvidence coverEvidence = JitenCoverEvidence.Specific) =>
        new()
        {
            DeckId = deckId,
            SubdeckId = subdeckId,
            OriginalTitle = title,
            CharacterCount = characters,
            CoverUrl = coverUrl,
            CoverEvidence = coverEvidence
        };

    private static ScoredCandidate CreateScored(
        JitenMatchCandidate candidate,
        int score = 100) =>
        new()
        {
            Candidate = candidate,
            TotalScore = score,
            TitleScore = 50,
            VolumeScore = 30,
            CharacterCountScore = 20,
            MatchedTitle = MatchedTitleVariant.Original,
            Evidence = ["Exact title match"]
        };

    private static JitenMatchOutcome CreateMatchedOutcome(
        Guid correlationId,
        MatchConfidence confidence,
        params ScoredCandidate[] candidates) =>
        CreateMatchedOutcomeWithTitle(correlationId, confidence, "Test Book", candidates);

    private static JitenMatchOutcome CreateMatchedOutcomeWithTitle(
        Guid correlationId,
        MatchConfidence confidence,
        string title,
        params ScoredCandidate[] candidates)
    {
        var parsed = new ParsedMediaTitle
        {
            OriginalTitle = title,
            ComparisonTitle = title.ToLowerInvariant(),
            BaseTitle = title
        };
        var result = new JitenMatchResult
        {
            ParsedTitle = parsed,
            Confidence = confidence,
            Candidates = candidates,
            Evidence = ["Confidence: " + confidence]
        };
        return JitenMatchOutcome.Matched(correlationId, result);
    }

    private static TtsuModel CreateModel(
        ImmersionDbContext context,
        StubJitenMatchService? matchService = null,
        IJitenSelectionResolver? selectionResolver = null,
        ITtsuImportBatchStore? batchStore = null)
    {
        var httpContext = new DefaultHttpContext();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var model = new TtsuModel(
            new TtsuDataLoader(),
            batchStore ?? new TtsuImportBatchStore(cache),
            context,
            matchService ?? new StubJitenMatchService(),
            selectionResolver ?? new StubJitenSelectionResolver())
        {
            PageContext = new PageContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, new TestTempDataProvider())
        };

        return model;
    }

    private static async Task EnrichAllPendingAsync(TtsuModel model, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var result = await model.OnPostEnrichNextAsync(model.BatchId, cancellationToken);
            if (result is JsonResult json && json.Value is not null)
            {
                var completeProp = json.Value.GetType().GetProperty("complete");
                if (completeProp?.GetValue(json.Value) is true)
                {
                    break;
                }
            }
            else
            {
                break;
            }
        }

        await model.OnPostReviewAsync(cancellationToken, fromEnrichment: true);
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

    private static FormFile StatisticsFileWithTitle(string title, string folderTitle)
    {
        var fixture = File.ReadAllText(GetFixturePath());
        var json = fixture.Replace("Test Book", title, StringComparison.Ordinal);
        var bytes = Encoding.UTF8.GetBytes(json);
        var stream = new MemoryStream(bytes);
        return new FormFile(
            stream,
            0,
            bytes.Length,
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

    private sealed class StubJitenMatchService : IJitenMatchService
    {
        public Func<IReadOnlyList<JitenMatchRequest>, TimeSpan, CancellationToken, Task<IReadOnlyList<JitenMatchOutcome>>>? Handler { get; set; }

        public Task<IReadOnlyList<JitenMatchOutcome>> MatchBatchAsync(
            IReadOnlyList<JitenMatchRequest> requests,
            TimeSpan timeBudget,
            CancellationToken cancellationToken = default)
        {
            if (Handler is not null)
            {
                return Handler(requests, timeBudget, cancellationToken);
            }

            return Task.FromResult<IReadOnlyList<JitenMatchOutcome>>(
                requests.Select(r => JitenMatchOutcome.NoCandidates(r.CorrelationId)).ToList());
        }
    }

    private sealed class StubJitenSelectionResolver : IJitenSelectionResolver
    {
        public Func<int, int?, CancellationToken, Task<JitenSelectionResult>>? Handler { get; set; }

        public Task<JitenSelectionResult> ResolveAsync(
            int parentDeckId,
            int? subdeckId = null,
            CancellationToken cancellationToken = default)
        {
            if (Handler is not null)
            {
                return Handler(parentDeckId, subdeckId, cancellationToken);
            }

            return Task.FromResult(JitenSelectionResult.Failed(
                JitenSelectionStatus.DeckNotFound,
                "No stub handler configured"));
        }

        public JitenSelectionResult Resolve(
            JitenDeckDetailDTO detail,
            int parentDeckId,
            int? subdeckId = null)
        {
            return JitenSelectionResult.Failed(
                JitenSelectionStatus.DeckNotFound,
                "Not supported in stub");
        }
    }

    #endregion
}
