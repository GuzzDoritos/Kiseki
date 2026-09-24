using Kiseki.Core;
using Kiseki.Core.DTOs;
using Kiseki.Core.Entities;
using Kiseki.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Kiseki.Tests;

public sealed class FranchiseCatalogueAndTopologyTests
{
    [Fact]
    public async Task ManualCommands_MoveUnassignAndDelete_PreserveSeriesInstallmentsAndCopies()
    {
        await using var database = await TestDatabase.CreateAsync();
        var first = await new FranchiseCatalogueService(database.Context).CreateAsync(
            new CreateFranchiseCommand("Main story"));
        var second = await new FranchiseCatalogueService(database.Context).CreateAsync(
            new CreateFranchiseCommand("Other story"));
        var series = new MediaSeries("Novel line", MediaType.Book);
        var installment = new MediaInstallment("Volume 1", MediaType.Book, 100) { MediaSeries = series };
        var copy = new MediaWork("Paperback", mediaType: MediaType.Book)
        {
            MediaInstallment = installment,
            MediaInstallmentId = installment.Id,
            MediaSeries = series,
            MediaSeriesId = series.Id
        };
        database.Context.AddRange(series, installment, copy);
        await database.Context.SaveChangesAsync();

        var service = new FranchiseCatalogueService(database.Context);
        await service.MoveSeriesAsync(new MoveSeriesToFranchiseCommand(first.Id, series.Id));
        await service.MoveSeriesAsync(new MoveSeriesToFranchiseCommand(second.Id, series.Id));
        await service.UnassignSeriesAsync(new UnassignSeriesFromFranchiseCommand(second.Id, series.Id));
        await service.MoveSeriesAsync(new MoveSeriesToFranchiseCommand(first.Id, series.Id));
        await service.DeleteAsync(new DeleteFranchiseCommand(first.Id));

        database.Context.ChangeTracker.Clear();
        var saved = await database.Context.MediaSeries.SingleAsync(item => item.Id == series.Id);
        Assert.Null(saved.FranchiseId);
        Assert.Single(await database.Context.MediaInstallments.Where(item => item.MediaSeriesId == series.Id).ToListAsync());
        Assert.Single(await database.Context.MediaWorks.Where(item => item.MediaInstallmentId == installment.Id).ToListAsync());
        Assert.Equal([second.Id], (await database.Context.Franchises.ToListAsync()).Select(item => item.Id));
    }

    [Fact]
    public async Task FranchiseDetails_ReportsNativeBookSummaryWithoutCrossMediaPercentage()
    {
        await using var database = await TestDatabase.CreateAsync();
        var franchise = new Franchise("Mixed");
        var book = new MediaSeries("Books", MediaType.Book) { Franchise = franchise };
        var anime = new MediaSeries("Anime", MediaType.Anime) { Franchise = franchise };
        var volume = new MediaInstallment("Book 1", MediaType.Book, 100)
        {
            MediaSeries = book,
            CanonicalCharacterCount = 100_000,
            ReleaseState = ReleaseState.Released
        };
        var work = new MediaWork("Book copy", mediaType: MediaType.Book)
        {
            MediaInstallment = volume,
            MediaInstallmentId = volume.Id,
            MediaSeries = book,
            MediaSeriesId = book.Id,
            IsCompleted = true
        };
        database.Context.AddRange(franchise, book, anime, volume, work);
        await database.Context.SaveChangesAsync();

        var details = await new FranchiseCatalogueService(database.Context).GetDetailsAsync(franchise.Id);

        Assert.NotNull(details);
        Assert.Equal(2, details.Series.Count);
        var bookSummary = Assert.Single(details.Series, item => item.SeriesId == book.Id);
        Assert.Equal(FranchiseProgressUnit.Characters, bookSummary.ProgressUnit);
        Assert.NotNull(bookSummary.BookProgress);
        Assert.Equal(100d, bookSummary.BookProgress!.ProgressPercentage);
        var animeSummary = Assert.Single(details.Series, item => item.SeriesId == anime.Id);
        Assert.Equal(FranchiseProgressUnit.NotAvailable, animeSummary.ProgressUnit);
        Assert.Null(animeSummary.BookProgress);
        Assert.DoesNotContain(typeof(FranchiseCatalogueDetails).GetProperties(), item =>
            item.Name.Contains("Progress", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CompleteGraph_RequiresSeparateExplicitNodeChoices_AndDoesNotCollapseLines()
    {
        await using var database = await TestDatabase.CreateAsync();
        var franchise = new Franchise("Franchise");
        var existing = new MediaSeries("Manual main title", MediaType.Book) { JitenDeckId = 10 };
        database.Context.AddRange(franchise, existing);
        await database.Context.SaveChangesAsync();
        var client = new StubClient(Graph(false,
            Node(10, "Main novels", 4),
            Node(20, "Side novels", 4),
            Node(30, "Anime", 2),
            new JitenFranchiseEdgeDTO { SourceDeckId = 10, TargetDeckId = 20, RelationshipType = 1 },
            new JitenFranchiseEdgeDTO { SourceDeckId = 20, TargetDeckId = 30, RelationshipType = 2 }));
        var service = new JitenFranchiseTopologyService(database.Context, client,
            new InMemoryJitenFranchiseTopologyReviewStore());

        var review = await service.PreviewAsync(franchise.Id, 10);

        Assert.True(review.IsCompleteGraph);
        Assert.Equal(2, review.Edges.Count);
        var main = Assert.Single(review.Proposals, item => item.Node.DeckId == 10);
        Assert.Equal(existing.Id, main.ExactSeriesId);
        var side = Assert.Single(review.Proposals, item => item.Node.DeckId == 20);
        Assert.Equal(JitenFranchiseNodeProposalKind.NewBookSeries, side.Kind);
        Assert.Equal(JitenFranchiseNodeAction.KeepUnresolved, side.RecommendedAction);
        var anime = Assert.Single(review.Proposals, item => item.Node.DeckId == 30);
        Assert.Equal(JitenFranchiseNodeClassification.Unknown, anime.Node.Classification);
        Assert.DoesNotContain(JitenFranchiseNodeAction.LinkExistingSeries, anime.AllowedActions);

        var approved = service.Approve(review.ReviewId, review.Fingerprint,
        [
            new(main.ProposalId, JitenFranchiseNodeAction.LinkExistingSeries, existing.Id),
            new(side.ProposalId, JitenFranchiseNodeAction.CreateSeparateBookSeries),
            new(anime.ProposalId, JitenFranchiseNodeAction.KeepUnresolved)
        ]);
        var result = await service.ApplyAsync(Guid.NewGuid(), franchise.Id, approved.ReviewId,
            approved.ApprovalFingerprint!);

        Assert.Equal(1, result.CreatedSeries);
        Assert.Equal(2, result.LinkedSeries);
        Assert.Equal(1, result.UnresolvedNodes);
        database.Context.ChangeTracker.Clear();
        var saved = await database.Context.MediaSeries.OrderBy(item => item.JitenDeckId).ToListAsync();
        Assert.Equal(2, saved.Count);
        Assert.All(saved, item => Assert.Equal(franchise.Id, item.FranchiseId));
        Assert.NotEqual(saved[0].Id, saved[1].Id);
        Assert.Equal([10, 20], saved.Select(item => item.JitenDeckId));
        Assert.Equal("Manual main title", saved.Single(item => item.Id == existing.Id).Title);
        var unresolved = await database.Context.JitenFranchiseGraphNodeStates.SingleAsync(item => item.DeckId == 30);
        Assert.Equal(JitenFranchiseNodeResolution.Unresolved, unresolved.Resolution);
    }

    [Fact]
    public async Task TruncatedGraph_PreservesMembershipAndPersistsIgnoreAcrossRefreshes()
    {
        await using var database = await TestDatabase.CreateAsync();
        var franchise = new Franchise("Franchise");
        var existing = new MediaSeries("Existing", MediaType.Book) { JitenDeckId = 10 };
        database.Context.AddRange(franchise, existing);
        await database.Context.SaveChangesAsync();
        var client = new StubClient(Graph(true, Node(10, "Existing", 4), Node(20, "Side", 4)));
        var store = new InMemoryJitenFranchiseTopologyReviewStore();
        var service = new JitenFranchiseTopologyService(database.Context, client, store);

        var review = await service.PreviewAsync(franchise.Id, 10);
        Assert.False(review.IsCompleteGraph);
        Assert.True(review.IsTruncated);
        Assert.All(review.Proposals, proposal =>
            Assert.DoesNotContain(JitenFranchiseNodeAction.LinkExistingSeries, proposal.AllowedActions));

        var ignored = Assert.Single(review.Proposals, item => item.Node.DeckId == 20);
        var approved = service.Approve(review.ReviewId, review.Fingerprint,
            review.Proposals.Select(item => new JitenFranchiseTopologyChoice(item.ProposalId,
                item.Node.DeckId == 20 ? JitenFranchiseNodeAction.Ignore : JitenFranchiseNodeAction.KeepUnresolved)).ToArray());
        await service.ApplyAsync(Guid.NewGuid(), franchise.Id, approved.ReviewId, approved.ApprovalFingerprint!);

        database.Context.ChangeTracker.Clear();
        Assert.Null((await database.Context.MediaSeries.SingleAsync(item => item.Id == existing.Id)).FranchiseId);
        var state = await database.Context.JitenFranchiseGraphNodeStates.SingleAsync(item => item.DeckId == 20);
        Assert.Equal(JitenFranchiseNodeResolution.Ignored, state.Resolution);

        var refreshed = await service.PreviewAsync(franchise.Id, 10);
        Assert.Equal(JitenFranchiseNodeProposalKind.IgnoredDecision,
            Assert.Single(refreshed.Proposals, item => item.Node.DeckId == 20).Kind);
    }

    [Fact]
    public async Task StaleProviderOrLocalReview_ChangesNothing_AndReplayReturnsReceipt()
    {
        await using var database = await TestDatabase.CreateAsync();
        var franchise = new Franchise("Franchise");
        database.Context.Add(franchise);
        await database.Context.SaveChangesAsync();
        var client = new StubClient(Graph(false, Node(10, "Main", 4)));
        var store = new InMemoryJitenFranchiseTopologyReviewStore();
        var service = new JitenFranchiseTopologyService(database.Context, client, store);
        var review = await service.PreviewAsync(franchise.Id, 10);
        var proposal = Assert.Single(review.Proposals);
        var approved = service.Approve(review.ReviewId, review.Fingerprint,
            [new(proposal.ProposalId, JitenFranchiseNodeAction.CreateSeparateBookSeries)]);

        client.Graph = Graph(false, Node(10, "Changed title", 4));
        await Assert.ThrowsAsync<JitenFranchiseTopologyReviewRequiredException>(() =>
            service.ApplyAsync(Guid.NewGuid(), franchise.Id, approved.ReviewId, approved.ApprovalFingerprint!));
        Assert.Empty(await database.Context.MediaSeries.ToListAsync());

        client.Graph = Graph(false, Node(10, "Main", 4));
        review = await service.PreviewAsync(franchise.Id, 10);
        proposal = Assert.Single(review.Proposals);
        approved = service.Approve(review.ReviewId, review.Fingerprint,
            [new(proposal.ProposalId, JitenFranchiseNodeAction.CreateSeparateBookSeries)]);
        franchise.SetTitle("Locally changed");
        await database.Context.SaveChangesAsync();
        await Assert.ThrowsAsync<JitenFranchiseTopologyReviewRequiredException>(() =>
            service.ApplyAsync(Guid.NewGuid(), franchise.Id, approved.ReviewId, approved.ApprovalFingerprint!));
        franchise.SetTitle("Franchise");
        await database.Context.SaveChangesAsync();

        review = await service.PreviewAsync(franchise.Id, 10);
        proposal = Assert.Single(review.Proposals);
        approved = service.Approve(review.ReviewId, review.Fingerprint,
            [new(proposal.ProposalId, JitenFranchiseNodeAction.CreateSeparateBookSeries)]);
        var operationId = Guid.NewGuid();
        var first = await service.ApplyAsync(operationId, franchise.Id, approved.ReviewId, approved.ApprovalFingerprint!);

        // Receipt lookup occurs before review lookup, so a lost response is safe even after a restart.
        var restarted = new JitenFranchiseTopologyService(database.Context, client,
            new InMemoryJitenFranchiseTopologyReviewStore());
        var replay = await restarted.ApplyAsync(operationId, franchise.Id, Guid.NewGuid(), "not-needed");
        Assert.Equal(first, replay);
        Assert.Single(await database.Context.MediaSeries.ToListAsync());
        Assert.Single(await database.Context.JitenFranchiseTopologyReceipts.ToListAsync());
    }

    private static JitenFranchiseNodeDTO Node(int id, string title, int mediaType) => new()
    {
        DeckId = id,
        OriginalTitle = title,
        MediaType = mediaType,
        CharacterCount = 1_000,
        ChildrenDeckCount = 1
    };

    private static JitenFranchiseDTO Graph(bool truncated, params JitenFranchiseNodeDTO[] nodes) => new()
    {
        Truncated = truncated,
        Nodes = nodes.ToList()
    };

    private static JitenFranchiseDTO Graph(bool truncated, JitenFranchiseNodeDTO first,
        JitenFranchiseNodeDTO second, JitenFranchiseNodeDTO third, params JitenFranchiseEdgeDTO[] edges) => new()
    {
        Truncated = truncated,
        Nodes = [first, second, third],
        Edges = edges.ToList()
    };

    private sealed class StubClient(JitenFranchiseDTO graph) : IJitenApiClient
    {
        public JitenFranchiseDTO Graph { get; set; } = graph;
        public Task<JitenFranchiseDTO?> GetFranchiseAsync(int deckId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<JitenFranchiseDTO?>(Graph);
        }

        public Task<JitenDeckDetailDTO?> GetDeckDetailAsync(int deckId, CancellationToken cancellationToken = default) =>
            Task.FromResult<JitenDeckDetailDTO?>(null);
        public Task<IReadOnlyList<JitenDeckDTO>> SearchBooksAsync(string query, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<JitenDeckDTO>>([]);
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public ImmersionDbContext Context { get; }

        private TestDatabase(SqliteConnection connection, ImmersionDbContext context)
        {
            _connection = connection;
            Context = context;
        }

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var context = new ImmersionDbContext(new DbContextOptionsBuilder<ImmersionDbContext>()
                .UseSqlite(connection).Options);
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
