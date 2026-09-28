using Kiseki.Core;
using Kiseki.Core.Entities;
using Kiseki.Core.Models.Pace;
using Kiseki.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Kiseki.Tests;

public sealed class ReadingPaceServiceTests
{
    [Fact]
    public async Task GetPaceProfileAsync_ReturnsUnavailable_WhenNoLogsExist()
    {
        await using var db = await TestDatabase.CreateAsync();
        var service = new ReadingPaceService(db.Context);

        var profile = await service.GetPaceProfileAsync(new DateOnly(2026, 9, 28));

        Assert.Equal(ReadingPaceSource.Unavailable, profile.Source);
        Assert.False(profile.HasSufficientData);
        Assert.Equal(0, profile.CharactersPerHour);
    }

    [Fact]
    public async Task GetPaceProfileAsync_UsesRecent30Days_WhenRecentMinutesMeetThreshold()
    {
        await using var db = await TestDatabase.CreateAsync();
        var today = new DateOnly(2026, 9, 28);

        // Add 120 minutes of reading in the last 10 days (15,000 characters total)
        db.Context.ImmersionLogs.AddRange([
            new ImmersionLog { Date = today.AddDays(-2), CharactersRead = 7500, TimeSpentMinutes = 60 },
            new ImmersionLog { Date = today.AddDays(-5), CharactersRead = 7500, TimeSpentMinutes = 60 },
            // Old log older than 30 days should be ignored for recent window
            new ImmersionLog { Date = today.AddDays(-40), CharactersRead = 50000, TimeSpentMinutes = 500 }
        ]);
        await db.Context.SaveChangesAsync();

        var service = new ReadingPaceService(db.Context);
        var profile = await service.GetPaceProfileAsync(today);

        Assert.Equal(ReadingPaceSource.Recent30Days, profile.Source);
        Assert.True(profile.HasSufficientData);
        Assert.Equal(30, profile.SampleDays);
        Assert.Equal(2, profile.LogCount);
        Assert.Equal(15_000, profile.TotalCharactersRead);
        Assert.Equal(120.0, profile.TotalTimeMinutes);
        // Speed = 15,000 / (120 / 60) = 7,500 ch/h
        Assert.Equal(7500, profile.CharactersPerHour);
        // Daily pace = 15,000 / 30 = 500 ch/day
        Assert.Equal(500, profile.DailyCharacters);
        // Daily minutes = 120 / 30 = 4.0 min/day
        Assert.Equal(4.0, profile.DailyMinutes);
    }

    [Fact]
    public async Task GetPaceProfileAsync_FallsBackToAllTime_WhenRecentMinutesBelowThreshold()
    {
        await using var db = await TestDatabase.CreateAsync();
        var today = new DateOnly(2026, 9, 28);

        // Only 30 minutes in recent 30 days (< 60 threshold)
        db.Context.ImmersionLogs.AddRange([
            new ImmersionLog { Date = today.AddDays(-2), CharactersRead = 5000, TimeSpentMinutes = 30 },
            // An older log from 60 days ago
            new ImmersionLog { Date = today.AddDays(-60), CharactersRead = 45000, TimeSpentMinutes = 180 }
        ]);
        await db.Context.SaveChangesAsync();

        var service = new ReadingPaceService(db.Context);
        var profile = await service.GetPaceProfileAsync(today);

        Assert.Equal(ReadingPaceSource.AllTime, profile.Source);
        Assert.True(profile.HasSufficientData);
        Assert.Equal(2, profile.LogCount);
        Assert.Equal(50_000, profile.TotalCharactersRead);
        Assert.Equal(210.0, profile.TotalTimeMinutes);
        // Speed = 50,000 / (210 / 60) = 14,286 ch/h
        Assert.Equal(14286, profile.CharactersPerHour);
        // Span = (today - 2) - (today - 60) + 1 = 59 days
        Assert.Equal(59, profile.SampleDays);
    }

    [Fact]
    public async Task GetPaceProfileAsync_FiltersOutZeroTimeAndZeroCharacterLogs()
    {
        await using var db = await TestDatabase.CreateAsync();
        var today = new DateOnly(2026, 9, 28);

        db.Context.ImmersionLogs.AddRange([
            new ImmersionLog { Date = today.AddDays(-1), CharactersRead = 10000, TimeSpentMinutes = 60 },
            new ImmersionLog { Date = today.AddDays(-2), CharactersRead = 0, TimeSpentMinutes = 30 },
            new ImmersionLog { Date = today.AddDays(-3), CharactersRead = 5000, TimeSpentMinutes = 0 }
        ]);
        await db.Context.SaveChangesAsync();

        var service = new ReadingPaceService(db.Context);
        var profile = await service.GetPaceProfileAsync(today);

        Assert.Equal(ReadingPaceSource.Recent30Days, profile.Source);
        Assert.Equal(1, profile.LogCount);
        Assert.Equal(10_000, profile.TotalCharactersRead);
        Assert.Equal(60.0, profile.TotalTimeMinutes);
        Assert.Equal(10000, profile.CharactersPerHour);
    }

    [Fact]
    public void EstimateTime_ReturnsCompleted_WhenIsCompletedOrZeroRemaining()
    {
        var service = new ReadingPaceService(null!);
        var pace = new ReadingPaceProfile(15000, 5000, 20.0, ReadingPaceSource.Recent30Days, 30, 10, 150000, 600);

        var completedResult = service.EstimateTime(0, pace, isCompleted: false);
        Assert.True(completedResult.IsCompleted);
        Assert.Equal("Completed", completedResult.FormattedReadingTime);
        Assert.Equal("Completed", completedResult.FormattedCalendarTime);

        var flagResult = service.EstimateTime(10000, pace, isCompleted: true);
        Assert.True(flagResult.IsCompleted);
    }

    [Fact]
    public void EstimateTime_ReturnsUnavailable_WhenPaceUnavailable()
    {
        var service = new ReadingPaceService(null!);
        var unavailablePace = ReadingPaceProfile.Unavailable;

        var estimate = service.EstimateTime(50000, unavailablePace, uncountedVolumesCount: 2);

        Assert.False(estimate.HasSufficientData);
        Assert.Equal("--", estimate.FormattedReadingTime);
        Assert.Equal("Log reading time to estimate", estimate.FormattedCalendarTime);
        Assert.Equal(2, estimate.UncountedVolumesCount);
    }

    [Fact]
    public void EstimateTime_CalculatesReadingHoursAndCalendarDaysCorrectly()
    {
        var service = new ReadingPaceService(null!);
        // Speed: 20,000 ch/h, Daily: 5,000 ch/day
        var pace = new ReadingPaceProfile(20000, 5000, 15.0, ReadingPaceSource.Recent30Days, 30, 20, 150000, 450);

        // Remaining: 50,000 characters
        // Reading time: 50,000 / 20,000 = 2.5 hours -> "2h 30m reading"
        // Calendar days: 50,000 / 5,000 = 10.0 days -> "~10 days"
        var estimate = service.EstimateTime(50000, pace);

        Assert.True(estimate.HasSufficientData);
        Assert.Equal(50000, estimate.RemainingCharacters);
        Assert.Equal(TimeSpan.FromHours(2.5), estimate.ReadingTime);
        Assert.Equal(10.0, estimate.CalendarDaysRemaining);
        Assert.Equal("2h 30m reading", estimate.FormattedReadingTime);
        Assert.Equal("2h 30m", estimate.FormattedReadingTimeCompact);
        Assert.Equal("~10 days", estimate.FormattedCalendarTime);
    }

    [Fact]
    public void EstimateInstallmentTime_HandlesCompletedAndUnreadAndInProgress()
    {
        var service = new ReadingPaceService(null!);
        var pace = new ReadingPaceProfile(15000, 5000, 20.0, ReadingPaceSource.Recent30Days, 30, 10, 150000, 600);

        var seriesId = Guid.NewGuid();

        // 1. Completed installment
        var work1 = new MediaWork("Vol 1") { IsCompleted = true, JitenCharacterCount = 100_000 };
        var inst1 = new SeriesInstallment(seriesId, 1, "Vol 1", 1, 100_000) { MediaWork = work1, MediaWorkId = work1.Id };
        var est1 = service.EstimateInstallmentTime(inst1, pace);
        Assert.True(est1.IsCompleted);

        // 2. In progress installment (30,000 read of 105,000 -> 75,000 remaining)
        // Reading time: 75,000 / 15,000 = 5.0 hours
        // Calendar time: 75,000 / 5,000 = 15.0 days -> "~2w 1d"
        var work2 = new MediaWork("Vol 2") { JitenCharacterCount = 105_000 };
        work2.Logs.Add(new ImmersionLog { CharactersRead = 30000, TimeSpentMinutes = 120 });
        var inst2 = new SeriesInstallment(seriesId, 2, "Vol 2", 2, 105_000) { MediaWork = work2, MediaWorkId = work2.Id };
        var est2 = service.EstimateInstallmentTime(inst2, pace);
        Assert.False(est2.IsCompleted);
        Assert.Equal(75000, est2.RemainingCharacters);
        Assert.Equal("5h reading", est2.FormattedReadingTime);
        Assert.Equal("5h", est2.FormattedReadingTimeCompact);
        Assert.Equal("~2w 1d", est2.FormattedCalendarTime);

        // 3. Unlinked volume with Jiten character count (100,000 remaining)
        var inst3 = new SeriesInstallment(seriesId, 3, "Vol 3", 3, 100_000);
        var est3 = service.EstimateInstallmentTime(inst3, pace);
        Assert.Equal(100000, est3.RemainingCharacters);
        Assert.Equal("6h 40m reading", est3.FormattedReadingTime);

        // 4. Volume with no character count (0 total)
        var inst4 = new SeriesInstallment(seriesId, 4, "Vol 4", null, 0);
        var est4 = service.EstimateInstallmentTime(inst4, pace);
        Assert.False(est4.HasSufficientData);
        Assert.True(est4.HasUncountedVolumes);
    }

    [Fact]
    public void EstimateSeriesTime_CalculatesSumOfRemainingCharactersAcrossInstallments()
    {
        var service = new ReadingPaceService(null!);
        var pace = new ReadingPaceProfile(20000, 10000, 30.0, ReadingPaceSource.Recent30Days, 30, 10, 300000, 900);

        var series = new MediaSeries("Test Series", MediaType.Book);

        // Vol 1: completed (100k)
        var work1 = new MediaWork("Vol 1") { IsCompleted = true, JitenCharacterCount = 100_000 };
        series.Installments.Add(new SeriesInstallment(series.Id, 1, "Vol 1", 1, 100_000) { MediaWork = work1, MediaWorkId = work1.Id });

        // Vol 2: in progress (60k read of 100k -> 40k remaining)
        var work2 = new MediaWork("Vol 2") { JitenCharacterCount = 100_000 };
        work2.Logs.Add(new ImmersionLog { CharactersRead = 60000, TimeSpentMinutes = 180 });
        series.Installments.Add(new SeriesInstallment(series.Id, 2, "Vol 2", 2, 100_000) { MediaWork = work2, MediaWorkId = work2.Id });

        // Vol 3: unread (110k remaining)
        series.Installments.Add(new SeriesInstallment(series.Id, 3, "Vol 3", 3, 110_000));

        // Vol 4: uncounted (0 chars)
        series.Installments.Add(new SeriesInstallment(series.Id, 4, "Vol 4", null, 0));

        // Total remaining: 40k + 110k = 150,000 characters
        // Reading time: 150,000 / 20,000 = 7.5 hours -> "7h 30m reading"
        // Calendar time: 150,000 / 10,000 = 15 days -> "~2w 1d"
        var estimate = service.EstimateSeriesTime(series, pace);

        Assert.Equal(150000, estimate.RemainingCharacters);
        Assert.Equal("7h 30m reading", estimate.FormattedReadingTime);
        Assert.Equal("~2w 1d", estimate.FormattedCalendarTime);
        Assert.Equal(1, estimate.UncountedVolumesCount);
        Assert.True(estimate.HasUncountedVolumes);
    }

    [Fact]
    public void EstimateSeriesTime_ReturnsCompleted_WhenAllInstallmentsAreDone()
    {
        var service = new ReadingPaceService(null!);
        var pace = new ReadingPaceProfile(20000, 10000, 30.0, ReadingPaceSource.Recent30Days, 30, 10, 300000, 900);

        var series = new MediaSeries("Finished Series", MediaType.Book);
        var work = new MediaWork("Vol 1") { IsCompleted = true, JitenCharacterCount = 100_000 };
        series.Installments.Add(new SeriesInstallment(series.Id, 1, "Vol 1", 1, 100_000) { MediaWork = work, MediaWorkId = work.Id });

        var estimate = service.EstimateSeriesTime(series, pace);

        Assert.True(estimate.IsCompleted);
        Assert.Equal(0, estimate.RemainingCharacters);
        Assert.Equal("Completed", estimate.FormattedReadingTime);
        Assert.Equal("Completed", estimate.FormattedCalendarTime);
    }

    [Theory]
    [InlineData(0, "Today")]
    [InlineData(0.3, "< 1 day")]
    [InlineData(1.0, "~1 day")]
    [InlineData(5.0, "~5 days")]
    [InlineData(14.0, "~2 weeks")]
    [InlineData(25.0, "~3w 4d")]
    [InlineData(70.0, "~2 mo 9 d")]
    [InlineData(400.0, "~1 yr 1 mo")]
    public void FormatCalendarDuration_FormatsRangesCorrectly(double days, string expected)
    {
        var formatted = ReadingPaceService.FormatCalendarDuration(days);
        Assert.Equal(expected, formatted);
    }

    [Theory]
    [InlineData(0.5, "30m reading", "30m")]
    [InlineData(1.0, "1h reading", "1h")]
    [InlineData(2.5, "2h 30m reading", "2h 30m")]
    [InlineData(48.0, "48h reading", "48h")]
    [InlineData(125.0, "125h reading", "125h")]
    public void FormatReadingTime_FormatsSpansCorrectly(double hours, string expectedStandard, string expectedCompact)
    {
        var time = TimeSpan.FromHours(hours);
        Assert.Equal(expectedStandard, ReadingPaceService.FormatReadingTime(time));
        Assert.Equal(expectedCompact, ReadingPaceService.FormatReadingTimeCompact(time));
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private TestDatabase(SqliteConnection connection, ImmersionDbContext context)
        {
            _connection = connection;
            Context = context;
        }

        public ImmersionDbContext Context { get; }

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ImmersionDbContext>()
                .UseSqlite(connection)
                .Options;
            var context = new ImmersionDbContext(options);
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
