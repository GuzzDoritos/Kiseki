using Kiseki.Core.Entities;
using Kiseki.Core.Models.Pace;
using Microsoft.EntityFrameworkCore;

namespace Kiseki.Core.Services;

public class ReadingPaceService : IReadingPaceService
{
    public const int RollingWindowDays = 30;
    public const double MinimumRecentMinutesThreshold = 60.0;

    private readonly ImmersionDbContext? _dbContext;

    public ReadingPaceService(ImmersionDbContext? dbContext = null)
    {
        _dbContext = dbContext;
    }

    public async Task<ReadingPaceProfile> GetPaceProfileAsync(
        DateOnly? referenceDate = null,
        CancellationToken cancellationToken = default)
    {
        if (_dbContext == null)
        {
            return ReadingPaceProfile.Unavailable;
        }

        var today = referenceDate ?? DateOnly.FromDateTime(DateTime.Today);
        var startDate = today.AddDays(-(RollingWindowDays - 1));

        var recentLogs = await _dbContext.ImmersionLogs
            .AsNoTracking()
            .Where(l => l.Date >= startDate && l.Date <= today && l.CharactersRead > 0 && l.TimeSpentMinutes > 0)
            .ToListAsync(cancellationToken);

        var recentCharacters = recentLogs.Sum(l => l.CharactersRead);
        var recentMinutes = recentLogs.Sum(l => l.TimeSpentMinutes);

        // If the user has logged at least the minimum threshold of reading time in the rolling window:
        if (recentMinutes >= MinimumRecentMinutesThreshold && recentCharacters > 0)
        {
            var speed = (int)Math.Round(recentCharacters / (recentMinutes / 60.0));
            var dailyChars = (int)Math.Round((double)recentCharacters / RollingWindowDays);
            var dailyMinutes = recentMinutes / RollingWindowDays;

            return new ReadingPaceProfile(
                CharactersPerHour: speed,
                DailyCharacters: dailyChars,
                DailyMinutes: dailyMinutes,
                Source: ReadingPaceSource.Recent30Days,
                SampleDays: RollingWindowDays,
                LogCount: recentLogs.Count,
                TotalCharactersRead: recentCharacters,
                TotalTimeMinutes: recentMinutes);
        }

        // Fallback to all-time immersion logs
        var allLogs = await _dbContext.ImmersionLogs
            .AsNoTracking()
            .Where(l => l.CharactersRead > 0 && l.TimeSpentMinutes > 0)
            .ToListAsync(cancellationToken);

        if (allLogs.Count == 0)
        {
            return ReadingPaceProfile.Unavailable;
        }

        var allCharacters = allLogs.Sum(l => l.CharactersRead);
        var allMinutes = allLogs.Sum(l => l.TimeSpentMinutes);

        if (allMinutes <= 0 || allCharacters <= 0)
        {
            return ReadingPaceProfile.Unavailable;
        }

        var minDate = allLogs.Min(l => l.Date);
        var maxDate = allLogs.Max(l => l.Date);
        var spanDays = Math.Max(1, (maxDate.DayNumber - minDate.DayNumber) + 1);

        var allSpeed = (int)Math.Round(allCharacters / (allMinutes / 60.0));
        var allDailyChars = (int)Math.Round((double)allCharacters / spanDays);
        var allDailyMinutes = allMinutes / spanDays;

        return new ReadingPaceProfile(
            CharactersPerHour: allSpeed,
            DailyCharacters: allDailyChars,
            DailyMinutes: allDailyMinutes,
            Source: ReadingPaceSource.AllTime,
            SampleDays: spanDays,
            LogCount: allLogs.Count,
            TotalCharactersRead: allCharacters,
            TotalTimeMinutes: allMinutes);
    }

    public ReadingTimeEstimate EstimateTime(
        int remainingCharacters,
        ReadingPaceProfile pace,
        bool isCompleted = false,
        int uncountedVolumesCount = 0)
    {
        ArgumentNullException.ThrowIfNull(pace);

        if (isCompleted || remainingCharacters <= 0)
        {
            return ReadingTimeEstimate.Completed;
        }

        if (!pace.HasSufficientData || pace.CharactersPerHour <= 0)
        {
            return ReadingTimeEstimate.CreateUnavailable(remainingCharacters, uncountedVolumesCount);
        }

        var totalReadingHours = (double)remainingCharacters / pace.CharactersPerHour;
        var readingTime = TimeSpan.FromHours(totalReadingHours);

        var calendarDays = pace.DailyCharacters > 0
            ? (double)remainingCharacters / pace.DailyCharacters
            : totalReadingHours / Math.Max(0.01, pace.DailyMinutes / 60.0);

        return new ReadingTimeEstimate(
            RemainingCharacters: remainingCharacters,
            ReadingTime: readingTime,
            CalendarDaysRemaining: calendarDays,
            FormattedReadingTime: FormatReadingTime(readingTime),
            FormattedReadingTimeCompact: FormatReadingTimeCompact(readingTime),
            FormattedCalendarTime: FormatCalendarDuration(calendarDays),
            IsCompleted: false,
            HasSufficientData: true,
            UncountedVolumesCount: uncountedVolumesCount);
    }

    public ReadingTimeEstimate EstimateSeriesTime(
        MediaSeries series,
        ReadingPaceProfile pace)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(pace);

        if (series.Installments.Count == 0 ||
            (series.CompletedInstallmentsCount == series.Installments.Count && series.Installments.Count > 0))
        {
            return ReadingTimeEstimate.Completed;
        }

        var remaining = 0;
        var uncounted = 0;

        foreach (var installment in series.Installments)
        {
            if (installment.IsCompleted)
            {
                continue;
            }

            if (installment.EffectiveTotalCharacters <= 0)
            {
                uncounted++;
            }
            else
            {
                remaining += Math.Max(0, installment.EffectiveTotalCharacters - installment.EffectiveCharactersRead);
            }
        }

        if (remaining == 0 && uncounted > 0)
        {
            return ReadingTimeEstimate.CreateUnavailable(0, uncountedVolumesCount: uncounted);
        }

        if (remaining == 0 && uncounted == 0)
        {
            return ReadingTimeEstimate.Completed;
        }

        return EstimateTime(remaining, pace, isCompleted: false, uncountedVolumesCount: uncounted);
    }

    public ReadingTimeEstimate EstimateInstallmentTime(
        SeriesInstallment installment,
        ReadingPaceProfile pace)
    {
        ArgumentNullException.ThrowIfNull(installment);
        ArgumentNullException.ThrowIfNull(pace);

        if (installment.IsCompleted)
        {
            return ReadingTimeEstimate.Completed;
        }

        if (installment.EffectiveTotalCharacters <= 0)
        {
            return ReadingTimeEstimate.CreateUnavailable(0, uncountedVolumesCount: 1);
        }

        var remaining = Math.Max(0, installment.EffectiveTotalCharacters - installment.EffectiveCharactersRead);
        if (remaining <= 0)
        {
            return ReadingTimeEstimate.Completed;
        }

        return EstimateTime(remaining, pace, isCompleted: false, uncountedVolumesCount: 0);
    }

    public static string FormatReadingTime(TimeSpan time)
    {
        if (time.TotalMinutes <= 0)
        {
            return "0m reading";
        }

        if (time.TotalHours < 1)
        {
            var m = Math.Max(1, (int)Math.Round(time.TotalMinutes));
            return $"{m}m reading";
        }

        if (time.TotalHours <= 24)
        {
            var h = (int)time.TotalHours;
            var m = time.Minutes;
            return m > 0 ? $"{h}h {m:D2}m reading" : $"{h}h reading";
        }

        var totalHours = (int)Math.Round(time.TotalHours);
        return $"{totalHours:N0}h reading";
    }

    public static string FormatReadingTimeCompact(TimeSpan time)
    {
        if (time.TotalMinutes <= 0)
        {
            return "0m";
        }

        if (time.TotalHours < 1)
        {
            var m = Math.Max(1, (int)Math.Round(time.TotalMinutes));
            return $"{m}m";
        }

        if (time.TotalHours <= 24)
        {
            var h = (int)time.TotalHours;
            var m = time.Minutes;
            return m > 0 ? $"{h}h {m:D2}m" : $"{h}h";
        }

        var totalHours = (int)Math.Round(time.TotalHours);
        return $"{totalHours:N0}h";
    }

    public static string FormatCalendarDuration(double days)
    {
        if (days <= 0)
        {
            return "Today";
        }

        if (days < 0.5)
        {
            return "< 1 day";
        }

        if (days < 1.5)
        {
            return "~1 day";
        }

        if (days < 14)
        {
            return $"~{Math.Round(days)} days";
        }

        if (days < 60)
        {
            var weeks = (int)(days / 7.0);
            var remDays = (int)Math.Round(days % 7.0);
            if (remDays == 7)
            {
                weeks++;
                remDays = 0;
            }

            if (remDays == 0)
            {
                return $"~{weeks} {(weeks == 1 ? "week" : "weeks")}";
            }

            return $"~{weeks}w {remDays}d";
        }

        if (days < 365)
        {
            var months = (int)(days / 30.4375);
            var remDays = (int)Math.Round(days - (months * 30.4375));

            if (remDays <= 1)
            {
                return $"~{months} {(months == 1 ? "month" : "months")}";
            }

            return $"~{months} mo {remDays} d";
        }

        var years = (int)(days / 365.25);
        var remainingDaysAfterYears = days - (years * 365.25);
        var remMonths = (int)Math.Round(remainingDaysAfterYears / 30.4375);

        if (remMonths <= 0)
        {
            return $"~{years} {(years == 1 ? "year" : "years")}";
        }

        return $"~{years} yr {remMonths} mo";
    }
}
