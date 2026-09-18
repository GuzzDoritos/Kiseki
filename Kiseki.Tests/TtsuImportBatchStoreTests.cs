using Kiseki.Core.DTOs;
using Kiseki.Web.Services;
using Microsoft.Extensions.Caching.Memory;

namespace Kiseki.Tests;

public sealed class TtsuImportBatchStoreTests
{
    private sealed class TestTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _utcNow = initial;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }

    [Fact]
    public void Store_And_TryGet_ReturnsStoredBatch()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new TtsuImportBatchStore(cache);

        var book = new TtsuBookContainer { Title = "Book 1" };
        var batch = store.Store([book]);

        Assert.NotEqual(Guid.Empty, batch.Id);
        Assert.Single(batch.Books);
        Assert.Equal("Book 1", batch.Books[0].Book.Title);

        var found = store.TryGet(batch.Id, out var retrieved);
        Assert.True(found);
        Assert.NotNull(retrieved);
        Assert.Equal(batch.Id, retrieved.Id);
    }

    [Fact]
    public void TryGet_UnknownBatchId_ReturnsFalse()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new TtsuImportBatchStore(cache);

        var found = store.TryGet(Guid.NewGuid(), out var retrieved);
        Assert.False(found);
        Assert.Null(retrieved);
    }

    [Fact]
    public void Remove_EvictsBatchImmediately()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new TtsuImportBatchStore(cache);

        var batch = store.Store([new TtsuBookContainer { Title = "Test" }]);
        Assert.True(store.TryGet(batch.Id, out _));

        store.Remove(batch.Id);
        Assert.False(store.TryGet(batch.Id, out _));
    }

    [Fact]
    public void BatchStore_Lifetimes_AreBounded()
    {
        Assert.Equal(TimeSpan.FromMinutes(20), TtsuImportBatchStore.SlidingLifetime);
        Assert.Equal(TimeSpan.FromHours(2), TtsuImportBatchStore.MaxAbsoluteLifetime);
    }
}

