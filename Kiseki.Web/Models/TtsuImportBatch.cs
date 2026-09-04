using Kiseki.Core.DTOs;

namespace Kiseki.Web.Models;

public sealed record TtsuImportBatch(
    Guid Id,
    IReadOnlyList<TtsuImportBatchBook> Books);

public sealed record TtsuImportBatchBook(
    Guid Key,
    TtsuBookContainer Book);
