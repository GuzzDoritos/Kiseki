namespace Kiseki.Core.Models.Pace;

public sealed record ReadingTimeEstimate(
    int RemainingCharacters,
    TimeSpan ReadingTime,
    double CalendarDaysRemaining,
    string FormattedReadingTime,
    string FormattedReadingTimeCompact,
    string FormattedCalendarTime,
    bool IsCompleted,
    bool HasSufficientData,
    int UncountedVolumesCount = 0)
{
    public bool HasUncountedVolumes => UncountedVolumesCount > 0;

    public static ReadingTimeEstimate Completed => new(
        RemainingCharacters: 0,
        ReadingTime: TimeSpan.Zero,
        CalendarDaysRemaining: 0,
        FormattedReadingTime: "Completed",
        FormattedReadingTimeCompact: "Done",
        FormattedCalendarTime: "Completed",
        IsCompleted: true,
        HasSufficientData: true,
        UncountedVolumesCount: 0);

    public static ReadingTimeEstimate CreateUnavailable(int remainingCharacters, int uncountedVolumesCount = 0) => new(
        RemainingCharacters: remainingCharacters,
        ReadingTime: TimeSpan.Zero,
        CalendarDaysRemaining: 0,
        FormattedReadingTime: "--",
        FormattedReadingTimeCompact: "--",
        FormattedCalendarTime: "Log reading time to estimate",
        IsCompleted: false,
        HasSufficientData: false,
        UncountedVolumesCount: uncountedVolumesCount);
}
