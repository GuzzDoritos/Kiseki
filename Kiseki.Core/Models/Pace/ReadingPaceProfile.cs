namespace Kiseki.Core.Models.Pace;

public enum ReadingPaceSource
{
    Recent30Days,
    AllTime,
    Unavailable
}

public sealed record ReadingPaceProfile(
    int CharactersPerHour,
    int DailyCharacters,
    double DailyMinutes,
    ReadingPaceSource Source,
    int SampleDays,
    int LogCount,
    int TotalCharactersRead,
    double TotalTimeMinutes)
{
    public bool HasSufficientData => Source != ReadingPaceSource.Unavailable && CharactersPerHour > 0;

    public string SourceDescription => Source switch
    {
        ReadingPaceSource.Recent30Days => "last 30 days",
        ReadingPaceSource.AllTime => "all-time average",
        _ => "no data"
    };

    public static ReadingPaceProfile Unavailable => new(
        CharactersPerHour: 0,
        DailyCharacters: 0,
        DailyMinutes: 0,
        Source: ReadingPaceSource.Unavailable,
        SampleDays: 0,
        LogCount: 0,
        TotalCharactersRead: 0,
        TotalTimeMinutes: 0);
}
