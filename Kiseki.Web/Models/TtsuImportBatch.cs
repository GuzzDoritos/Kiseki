using Kiseki.Core.DTOs;

namespace Kiseki.Web.Models;

public sealed record TtsuImportBatch(
    Guid Id,
    IReadOnlyList<TtsuImportBatchBook> Books)
{
    public System.Collections.Concurrent.ConcurrentDictionary<Guid, TtsuReviewedPlan> Reviews { get; } = new();
}

public sealed record TtsuReviewedPlan(Guid BookKey, string Fingerprint);

public sealed record TtsuImportBatchBook(
    Guid Key,
    TtsuBookContainer Book);
