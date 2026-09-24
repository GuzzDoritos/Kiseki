using Kiseki.Core;
using Kiseki.Core.DTOs;
using Kiseki.Core.Entities;
using Kiseki.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Kiseki.Tests;

public sealed class JitenCatalogueReconciliationTests
{
    [Fact]
    public async Task Apply_HydratesCatalogueWithoutCreatingCopies_AndPreservesManualAndCopyMetadata()
    {
        await using var database = await Database.CreateAsync();
        var series = new MediaSeries("North Wind", MediaType.Book);
        var existing = new MediaInstallment("Legacy one", MediaType.Book, 100)
        {
            MediaSeries = series,
            CanonicalTitle = "Old provider title",
            TitleOverride = "My title",
            CanonicalCharacterCount = 80_000,
            CharacterCountOverride = 81_234,
            ReleaseStateOverride = ReleaseState.Released,
            ReleaseDateOverride = new DateOnly(2020, 1, 2)
        };
        existing.ProviderIdentities.Add(new InstallmentProviderIdentity
        {
            Provider = "jiten", NormalizedKey = "subdeck:10:101", MediaInstallment = existing,
            ProviderItemId = 101, ParentProviderItemId = 10
        });
        var copy = new MediaWork("My ebook")
        {
            MediaInstallment = existing, MediaInstallmentId = existing.Id,
            MediaSeries = series, MediaSeriesId = series.Id
        };
        copy.ApplyTtsuCover("https://example.test/protected.jpg");
        database.Context.AddRange(series, existing, copy);
        await database.Context.SaveChangesAsync();

        var client = new StubClient(CompleteFetch(10,
            Deck(101, "Provider one", 90_000, "https://cdn.jiten.moe/one.jpg"),
            Deck(102, "Provider two", 95_000, "http://unsafe.example/two.jpg")));
        var store = new InMemoryJitenCatalogueReviewStore();
        var service = new JitenCatalogueReconciliationService(database.Context, client, store);
        var review = await service.PreviewAsync(series.Id, 10);

        Assert.Contains(review.Proposals, item => item.Kind == JitenCatalogueProposalKind.ExactIdentity);
        Assert.Contains(review.Proposals, item => item.Kind == JitenCatalogueProposalKind.Addition);
        Assert.Contains(review.Proposals, item => item.HasManualConflict);
        var approved = service.Approve(review.ReviewId, review.Fingerprint, RecommendedChoices(review));
        var result = await service.ApplyAsync(Guid.NewGuid(), series.Id, review.ReviewId, approved.ApprovalFingerprint!);

        database.Context.ChangeTracker.Clear();
        var installments = await database.Context.MediaInstallments.Include(item => item.ProviderIdentities)
            .OrderBy(item => item.OrderKey).ToListAsync();
        var storedCopy = await database.Context.MediaWorks.SingleAsync();
        Assert.Equal(2, installments.Count);
        Assert.Single(await database.Context.MediaWorks.ToListAsync());
        Assert.Equal(1, result.AddedInstallments);
        Assert.Equal("Provider one", installments[0].CanonicalTitle);
        Assert.Equal("My title", installments[0].TitleOverride);
        Assert.Equal(81_234, installments[0].CharacterCountOverride);
        Assert.Equal(ReleaseState.Released, installments[0].ReleaseStateOverride);
        Assert.Equal("https://example.test/protected.jpg", storedCopy.CoverUrl);
        Assert.Equal(MediaCoverSource.Ttsu, storedCopy.CoverSource);
        Assert.Contains(installments[1].ProviderIdentities, identity => identity.NormalizedKey == "subdeck:10:102");
        Assert.Equal("https://cdn.jiten.moe/parent.jpg", installments[1].CanonicalCoverUrl);
        Assert.Equal(CanonicalCoverSource.ProviderParentFallback, installments[1].CanonicalCoverSource);
    }

    [Fact]
    public async Task Apply_IsIdempotent_AndLostResponseReplayUsesDurableReceipt()
    {
        await using var database = await Database.CreateAsync();
        var series = new MediaSeries("Series", MediaType.Book);
        database.Context.Add(series);
        await database.Context.SaveChangesAsync();
        var fetch = CompleteFetch(20, Deck(201, "Volume 1", 100_000));
        var client = new StubClient(fetch);
        var store = new InMemoryJitenCatalogueReviewStore();
        var service = new JitenCatalogueReconciliationService(database.Context, client, store);
        var review = await service.PreviewAsync(series.Id, 20);
        var approved = service.Approve(review.ReviewId, review.Fingerprint, RecommendedChoices(review));
        var operationId = Guid.NewGuid();
        var first = await service.ApplyAsync(operationId, series.Id, review.ReviewId, approved.ApprovalFingerprint!);

        var repeatReview = await service.PreviewAsync(series.Id, 20);
        var repeatApproved = service.Approve(repeatReview.ReviewId, repeatReview.Fingerprint,
            RecommendedChoices(repeatReview));
        await service.ApplyAsync(Guid.NewGuid(), series.Id, repeatReview.ReviewId, repeatApproved.ApprovalFingerprint!);

        var restarted = new JitenCatalogueReconciliationService(
            database.Context, client, new InMemoryJitenCatalogueReviewStore());
        var replay = await restarted.ApplyAsync(operationId, series.Id, Guid.NewGuid(), "not-the-review");

        Assert.Equal(first, replay);
        Assert.Single(await database.Context.MediaInstallments.ToListAsync());
        Assert.Single(await database.Context.InstallmentProviderIdentities.ToListAsync());
        Assert.Single(await database.Context.InstallmentProviderSnapshots.ToListAsync());
        Assert.Empty(await database.Context.MediaWorks.ToListAsync());
    }

    [Fact]
    public async Task ReceiptAndApprovedReview_AreScopedToTheirOwningSeries()
    {
        await using var database = await Database.CreateAsync();
        var firstSeries = new MediaSeries("First", MediaType.Book);
        var secondSeries = new MediaSeries("Second", MediaType.Book);
        database.Context.AddRange(firstSeries, secondSeries);
        await database.Context.SaveChangesAsync();

        var client = new StubClient(CompleteFetch(21, Deck(211, "First volume", 75_000)));
        var store = new InMemoryJitenCatalogueReviewStore();
        var service = new JitenCatalogueReconciliationService(database.Context, client, store);
        var review = await service.PreviewAsync(firstSeries.Id, 21);
        var approved = service.Approve(review.ReviewId, review.Fingerprint, RecommendedChoices(review));
        var operationId = Guid.NewGuid();
        await service.ApplyAsync(
            operationId, firstSeries.Id, review.ReviewId, approved.ApprovalFingerprint!);

        Assert.NotNull(await service.GetReceiptAsync(operationId, firstSeries.Id));
        Assert.Null(await service.GetReceiptAsync(operationId, secondSeries.Id));

        var exception = await Assert.ThrowsAsync<JitenCatalogueReviewRequiredException>(() => service.ApplyAsync(
            Guid.NewGuid(), secondSeries.Id, review.ReviewId, approved.ApprovalFingerprint!));
        Assert.Contains("does not belong", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await database.Context.MediaInstallments
            .Where(item => item.MediaSeriesId == secondSeries.Id)
            .ToListAsync());
    }

    [Fact]
    public async Task InvalidChoice_IsTypedAsRetryableAgainstTheCurrentReview()
    {
        await using var database = await Database.CreateAsync();
        var series = new MediaSeries("Series", MediaType.Book);
        database.Context.Add(series);
        await database.Context.SaveChangesAsync();
        var service = new JitenCatalogueReconciliationService(
            database.Context,
            new StubClient(CompleteFetch(22, Deck(221, "Volume", 80_000))),
            new InMemoryJitenCatalogueReviewStore());
        var review = await service.PreviewAsync(series.Id, 22);

        var exception = Assert.Throws<JitenCatalogueReviewRequiredException>(() => service.Approve(
            review.ReviewId, review.Fingerprint, []));

        Assert.True(exception.CanRetryCurrentReview);
        Assert.Equal(review, service.GetActiveReview(review.ReviewId));
    }

    [Fact]
    public async Task Apply_RequiresReviewAgain_WhenProviderOrLocalEvidenceChanges()
    {
        await using var database = await Database.CreateAsync();
        var series = new MediaSeries("Series", MediaType.Book);
        var installment = new MediaInstallment("Volume 1", MediaType.Book) { MediaSeries = series };
        database.Context.AddRange(series, installment);
        await database.Context.SaveChangesAsync();
        var firstFetch = CompleteFetch(30, Deck(301, "Volume 1", 100_000));
        var client = new StubClient(firstFetch);
        var store = new InMemoryJitenCatalogueReviewStore();
        var service = new JitenCatalogueReconciliationService(database.Context, client, store);
        var localReview = await service.PreviewAsync(series.Id, 30);
        var localApproved = service.Approve(localReview.ReviewId, localReview.Fingerprint, RecommendedChoices(localReview));
        installment.TitleOverride = "Corrected after review";
        installment.Version = Guid.NewGuid();
        await database.Context.SaveChangesAsync();

        await Assert.ThrowsAsync<JitenCatalogueReviewRequiredException>(() => service.ApplyAsync(
            Guid.NewGuid(), series.Id, localReview.ReviewId, localApproved.ApprovalFingerprint!));

        database.Context.ChangeTracker.Clear();
        var providerReview = await service.PreviewAsync(series.Id, 30);
        var providerApproved = service.Approve(providerReview.ReviewId, providerReview.Fingerprint,
            RecommendedChoices(providerReview));
        client.Fetch = CompleteFetch(30, Deck(301, "Changed upstream", 100_000));
        await Assert.ThrowsAsync<JitenCatalogueReviewRequiredException>(() => service.ApplyAsync(
            Guid.NewGuid(), series.Id, providerReview.ReviewId, providerApproved.ApprovalFingerprint!));
    }

    [Fact]
    public async Task IncompleteFetch_CannotMarkDisappearedIdentityMissing_ButCompleteFetchCan()
    {
        await using var database = await Database.CreateAsync();
        var series = new MediaSeries("Series", MediaType.Book);
        var missing = new MediaInstallment("Volume 2", MediaType.Book) { MediaSeries = series };
        missing.ProviderIdentities.Add(new InstallmentProviderIdentity
        {
            Provider = "jiten", NormalizedKey = "subdeck:40:402", MediaInstallment = missing,
            ProviderItemId = 402, ParentProviderItemId = 40
        });
        database.Context.AddRange(series, missing);
        await database.Context.SaveChangesAsync();
        var client = new StubClient(new JitenDeckCatalogueFetchResult(
            Detail(40, Deck(401, "Volume 1", 90_000)), false, 2, 1, "pagination stopped"));
        var store = new InMemoryJitenCatalogueReviewStore();
        var service = new JitenCatalogueReconciliationService(database.Context, client, store);

        var incomplete = await service.PreviewAsync(series.Id, 40);
        var absent = Assert.Single(incomplete.Proposals, item => item.ProviderKey == "subdeck:40:402");
        Assert.Equal(JitenCatalogueProposalKind.AbsenceUnverified, absent.Kind);
        Assert.DoesNotContain(JitenCatalogueChoiceAction.MarkMissing, absent.AllowedActions);
        Assert.Throws<JitenCatalogueReviewRequiredException>(() => service.Approve(incomplete.ReviewId,
            incomplete.Fingerprint, incomplete.Proposals.Select(item => new JitenCatalogueChoice(item.ProposalId,
                item == absent ? JitenCatalogueChoiceAction.MarkMissing : item.RecommendedAction)).ToArray()));

        client.Fetch = CompleteFetch(40, Deck(401, "Volume 1", 90_000));
        var complete = await service.PreviewAsync(series.Id, 40);
        var approved = service.Approve(complete.ReviewId, complete.Fingerprint, RecommendedChoices(complete));
        await service.ApplyAsync(Guid.NewGuid(), series.Id, complete.ReviewId, approved.ApprovalFingerprint!);
        database.Context.ChangeTracker.Clear();
        Assert.NotNull((await database.Context.InstallmentProviderIdentities
            .SingleAsync(item => item.NormalizedKey == "subdeck:40:402")).MissingSinceUtc);
        Assert.Equal(2, await database.Context.MediaInstallments.CountAsync());
    }

    [Fact]
    public async Task Preview_SurfacesDuplicateLegacyClaimsAndAmbiguousProviderEditions()
    {
        await using var database = await Database.CreateAsync();
        var series = new MediaSeries("Series", MediaType.Book);
        foreach (var suffix in new[] { "paper", "ebook" })
        {
            var installment = new MediaInstallment("Same Volume", MediaType.Book) { MediaSeries = series };
            var copy = new MediaWork(suffix, jitenDeckId: 50, mediaType: MediaType.Book)
            {
                MediaInstallment = installment, MediaInstallmentId = installment.Id,
                MediaSeries = series, MediaSeriesId = series.Id
            };
            copy.LinkToJitenSubdeck(50, 501, 80_000);
            database.Context.AddRange(installment, copy);
        }
        database.Context.Add(series);
        await database.Context.SaveChangesAsync();
        var client = new StubClient(CompleteFetch(50,
            Deck(501, "Same Volume", 80_000), Deck(502, "Same Volume", 82_000)));
        var service = new JitenCatalogueReconciliationService(database.Context,
            client,
            new InMemoryJitenCatalogueReviewStore());

        var review = await service.PreviewAsync(series.Id, 50);
        var claim = Assert.Single(review.Proposals, item => item.ProviderKey == "subdeck:50:501");
        Assert.Equal(JitenCatalogueProposalKind.AmbiguousAssociation, claim.Kind);
        Assert.Equal(2, claim.CandidateInstallmentIds.Count);
        Assert.True(claim.HasUncertainOrder);
        Assert.Contains(review.Proposals, item => item.ProviderKey == "subdeck:50:502" && item.HasUncertainOrder);

        client.Fetch = CompleteFetch(50,
            Deck(503, "Duplicate", 70_000), Deck(503, "Duplicate", 70_000));
        var duplicateReview = await service.PreviewAsync(series.Id, 50);
        Assert.Contains(duplicateReview.Proposals, item =>
            item.ProviderKey == "subdeck:50:503" &&
            item.Kind == JitenCatalogueProposalKind.DuplicateProviderIdentity &&
            item.AllowedActions.SequenceEqual([JitenCatalogueChoiceAction.Ignore]));
    }

    [Fact]
    public async Task ReviewExpires_AndConcurrentRefreshesCreateOneIdentity()
    {
        await using var inMemory = await Database.CreateAsync();
        var expiringSeries = new MediaSeries("Expiring", MediaType.Book);
        inMemory.Context.Add(expiringSeries);
        await inMemory.Context.SaveChangesAsync();
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero));
        var expiringService = new JitenCatalogueReconciliationService(inMemory.Context,
            new StubClient(CompleteFetch(60, Deck(601, "Volume 1", 80_000))),
            new InMemoryJitenCatalogueReviewStore(), clock);
        var expiringReview = await expiringService.PreviewAsync(expiringSeries.Id, 60);
        clock.Advance(TimeSpan.FromMinutes(21));
        Assert.Throws<JitenCatalogueReviewRequiredException>(() => expiringService.Approve(
            expiringReview.ReviewId, expiringReview.Fingerprint, RecommendedChoices(expiringReview)));

        var path = Path.Combine(Path.GetTempPath(), $"kiseki-jiten-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<ImmersionDbContext>().UseSqlite($"Data Source={path}").Options;
            Guid seriesId;
            await using (var setup = new ImmersionDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                var series = new MediaSeries("Concurrent", MediaType.Book);
                setup.Add(series);
                await setup.SaveChangesAsync();
                seriesId = series.Id;
            }

            await using var firstContext = new ImmersionDbContext(options);
            await using var secondContext = new ImmersionDbContext(options);
            var store = new InMemoryJitenCatalogueReviewStore();
            var fetch = CompleteFetch(70, Deck(701, "Volume 1", 90_000));
            var firstService = new JitenCatalogueReconciliationService(firstContext, new StubClient(fetch), store);
            var secondService = new JitenCatalogueReconciliationService(secondContext, new StubClient(fetch), store);
            var firstReview = await firstService.PreviewAsync(seriesId, 70);
            var secondReview = await secondService.PreviewAsync(seriesId, 70);
            var firstApproved = firstService.Approve(firstReview.ReviewId, firstReview.Fingerprint,
                RecommendedChoices(firstReview));
            var secondApproved = secondService.Approve(secondReview.ReviewId, secondReview.Fingerprint,
                RecommendedChoices(secondReview));

            static async Task<bool> TryApplyAsync(JitenCatalogueReconciliationService service,
                Guid mediaSeriesId, JitenCatalogueReview review)
            {
                try
                {
                    await service.ApplyAsync(Guid.NewGuid(), mediaSeriesId, review.ReviewId,
                        review.ApprovalFingerprint!);
                    return true;
                }
                catch (JitenCatalogueReviewRequiredException)
                {
                    return false;
                }
            }

            var outcomes = await Task.WhenAll(
                TryApplyAsync(firstService, seriesId, firstApproved),
                TryApplyAsync(secondService, seriesId, secondApproved));
            Assert.Single(outcomes, success => success);
            await using var verify = new ImmersionDbContext(options);
            Assert.Equal(1, await verify.MediaInstallments.CountAsync());
            Assert.Equal(1, await verify.InstallmentProviderIdentities.CountAsync());
            Assert.Equal(1, await verify.JitenCatalogueRefreshReceipts.CountAsync());
            Assert.Empty(await verify.MediaWorks.ToListAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static IReadOnlyList<JitenCatalogueChoice> RecommendedChoices(JitenCatalogueReview review) =>
        review.Proposals.Select(proposal => new JitenCatalogueChoice(proposal.ProposalId,
            proposal.RecommendedAction,
            proposal.RecommendedAction == JitenCatalogueChoiceAction.LinkExisting
                ? proposal.CandidateInstallmentIds.First()
                : null)).ToArray();

    private static JitenDeckDTO Deck(int id, string title, int characters, string cover = "") => new()
    {
        DeckId = id, ParentDeckId = null, OriginalTitle = title, CharacterCount = characters,
        CoverName = cover
    };

    private static JitenDeckDetailDTO Detail(int parentId, params JitenDeckDTO[] children) => new()
    {
        MainDeck = new JitenDeckDTO
        {
            DeckId = parentId, OriginalTitle = "Parent", ChildrenDeckCount = children.Length,
            CoverName = "https://cdn.jiten.moe/parent.jpg"
        },
        SubDecks = children.ToList()
    };

    private static JitenDeckCatalogueFetchResult CompleteFetch(int parentId, params JitenDeckDTO[] children) =>
        new(Detail(parentId, children), true, children.Length, children.Length);

    private sealed class StubClient(JitenDeckCatalogueFetchResult fetch) : IJitenApiClient
    {
        public JitenDeckCatalogueFetchResult Fetch { get; set; } = fetch;
        public Task<JitenDeckCatalogueFetchResult> GetDeckCatalogueAsync(int deckId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Fetch);
        }
        public Task<Kiseki.Core.DTOs.JitenDeckDetailDTO?> GetDeckDetailAsync(int deckId,
            CancellationToken cancellationToken = default) => Task.FromResult(Fetch.Detail);
        public Task<IReadOnlyList<JitenDeckDTO>> SearchBooksAsync(string query,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<JitenDeckDTO>>([]);
        public Task<Kiseki.Core.DTOs.JitenFranchiseDTO?> GetFranchiseAsync(int deckId,
            CancellationToken cancellationToken = default) => Task.FromResult<Kiseki.Core.DTOs.JitenFranchiseDTO?>(null);
    }

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
            return new(connection, context);
        }
        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
