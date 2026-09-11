using Kiseki.Core.Models;

namespace Kiseki.Web.Models;

public sealed record TtsuBookPreviewViewModel(Guid BookKey, string Title, string? FolderHint,
    string MatchReason, TtsuImportPlan Plan)
{
    public Guid? ExistingMediaWorkId => Plan.TargetId;
    public bool ExistsInLibrary => ExistingMediaWorkId.HasValue;
    public long CharactersRead => Plan.ResultCharacters;
    public static string FormatDuration(double seconds)
    {
        var minutes = Math.Max(0, (long)Math.Round(seconds / 60d));
        return minutes >= 60 ? $"{minutes / 60:N0}h {minutes % 60}m" : $"{minutes}m";
    }
}

public sealed record TtsuOrphanViewModel(Guid Id, DateOnly Date, int Characters, double Minutes);
public sealed class TtsuBookSelectionInput
{
    public Guid BookKey { get; set; }
    public bool Selected { get; set; }
    public TtsuImportMode Mode { get; set; }
    public Guid? TargetId { get; set; }
    public Guid ReviewToken { get; set; }
    public List<TtsuDayResolutionInput> Days { get; set; } = [];
    public List<Guid> OrphanLogIds { get; set; } = [];
}
public sealed class TtsuDayResolutionInput
{
    public DateOnly Date { get; set; }
    public string Choice { get; set; } = string.Empty;
}
public enum TtsuImportMode { Merge, Create }
