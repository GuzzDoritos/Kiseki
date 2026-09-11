namespace Kiseki.Core.Models;

public enum TtsuDayAction { Added, Updated, Unchanged, Stale, Conflict }
public sealed record TtsuDailySnapshot(DateOnly Date, int Characters, double Minutes, long? Revision);
public sealed record TtsuStoredDay(Guid Id, int Characters, double Minutes, long? Revision);
public sealed record TtsuDayPlan(
    DateOnly Date, IReadOnlyList<TtsuStoredDay> Existing, IReadOnlyList<TtsuDailySnapshot> Incoming,
    TtsuDayAction Action, TtsuDailySnapshot? Accepted, Guid? RetainedLogId, string? ReviewReason)
{
    public long CharacterDelta => (Accepted?.Characters ?? Existing.SingleOrDefault(x => x.Id == RetainedLogId)?.Characters ?? Existing.Sum(x => (long)x.Characters)) - Existing.Sum(x => (long)x.Characters);
    public double MinuteDelta => (Accepted?.Minutes ?? Existing.SingleOrDefault(x => x.Id == RetainedLogId)?.Minutes ?? Existing.Sum(x => x.Minutes)) - Existing.Sum(x => x.Minutes);
}
public sealed record TtsuImportPlan(
    Guid? TargetId, string TargetTitle, string Fingerprint, IReadOnlyList<TtsuDayPlan> Days,
    long CurrentCharacters, double CurrentMinutes, string? Error, long AssignedCharacters = 0, double AssignedMinutes = 0)
{
    public bool CanApply => Error is null && Days.All(day => day.Action != TtsuDayAction.Conflict);
    public long ResultCharacters => CurrentCharacters + AssignedCharacters + Days.Sum(day => day.CharacterDelta);
    public double ResultMinutes => CurrentMinutes + AssignedMinutes + Days.Sum(day => day.MinuteDelta);
    public int Count(TtsuDayAction action) => Days.Count(day => day.Action == action);
}
public sealed record TtsuMatch(Guid? WorkId, string Reason, bool IsAmbiguous = false);
public sealed record TtsuTarget(Guid Id, string Title);
public sealed record TtsuImportRequest(
    DTOs.TtsuBookContainer Book, Guid? TargetId, IReadOnlyDictionary<DateOnly, string> Resolutions,
    string ExpectedFingerprint, IReadOnlyList<Guid>? OrphanLogIds = null);

public sealed class TtsuImportReviewRequiredException(string message) : Exception(message);
