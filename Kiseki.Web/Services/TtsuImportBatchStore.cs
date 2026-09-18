using Kiseki.Core.DTOs;
using Kiseki.Web.Models;
using Microsoft.Extensions.Caching.Memory;

namespace Kiseki.Web.Services;

public interface ITtsuImportBatchStore
{
    TtsuImportBatch Store(IReadOnlyList<TtsuBookContainer> books);
    bool TryGet(Guid batchId, out TtsuImportBatch batch);
    void Remove(Guid batchId);
}

public sealed class TtsuImportBatchStore : ITtsuImportBatchStore
{
    public static readonly TimeSpan SlidingLifetime = TimeSpan.FromMinutes(20);
    public static readonly TimeSpan MaxAbsoluteLifetime = TimeSpan.FromHours(2);

    private readonly IMemoryCache _cache;
    private readonly TimeProvider _timeProvider;

    public TtsuImportBatchStore(IMemoryCache cache, TimeProvider? timeProvider = null)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public TtsuImportBatch Store(IReadOnlyList<TtsuBookContainer> books)
    {
        ArgumentNullException.ThrowIfNull(books);

        var batch = new TtsuImportBatch(
            Guid.NewGuid(),
            books.Select(book => new TtsuImportBatchBook(Guid.NewGuid(), book)).ToList());

        var now = _timeProvider.GetUtcNow();
        _cache.Set(
            CacheKey(batch.Id),
            batch,
            new MemoryCacheEntryOptions
            {
                SlidingExpiration = SlidingLifetime,
                AbsoluteExpiration = now.Add(MaxAbsoluteLifetime)
            });

        return batch;
    }

    public bool TryGet(Guid batchId, out TtsuImportBatch batch)
    {
        if (_cache.TryGetValue(CacheKey(batchId), out TtsuImportBatch? storedBatch) &&
            storedBatch is not null)
        {
            batch = storedBatch;
            return true;
        }

        batch = null!;
        return false;
    }

    public void Remove(Guid batchId)
    {
        _cache.Remove(CacheKey(batchId));
    }

    private static string CacheKey(Guid batchId) => $"ttsu-import:{batchId:N}";
}
