using Kiseki.Core;
using Kiseki.Core.Entities;
using Kiseki.Web.Pages;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Kiseki.Tests;

public sealed class DashboardPageTests
{
    [Fact]
    public async Task OnGetAsync_CalculatesWeekAndYearStats_AndActiveWorks()
    {
        await using var database = await TestDatabase.CreateAsync();

        var today = DateOnly.FromDateTime(DateTime.Today);
        int daysFromMonday = ((int)today.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        var monday = today.AddDays(-daysFromMonday);
        var sunday = monday.AddDays(6);

        // Active work
        var activeWork = new MediaWork("Active Work") { IsCompleted = false };
        // Completed work
        var completedWork = new MediaWork("Completed Work") { IsCompleted = true };

        database.Context.MediaWorks.AddRange(activeWork, completedWork);

        // Log inside current week: 1 hour, 2 minutes, 3 seconds = 62.05 minutes
        var weekLog1 = new ImmersionLog
        {
            Date = monday,
            CharactersRead = 500,
            TimeSpentMinutes = 62.05
        };

        var weekLog2 = new ImmersionLog
        {
            Date = sunday,
            CharactersRead = 750,
            TimeSpentMinutes = 30.0
        };

        var yearOnlyLog = new ImmersionLog
        {
            Date = new DateOnly(today.Year, 1, 1),
            CharactersRead = 1000,
            TimeSpentMinutes = 45.0
        };

        // Log from previous year
        var previousYearLog = new ImmersionLog
        {
            Date = new DateOnly(today.Year - 1, 12, 31),
            CharactersRead = 5000,
            TimeSpentMinutes = 200.0
        };

        database.Context.ImmersionLogs.AddRange(weekLog1, weekLog2, yearOnlyLog, previousYearLog);
        await database.Context.SaveChangesAsync();

        var model = new IndexModel(database.Context);
        await model.OnGetAsync();

        // Week: 500 + 750 = 1250 characters
        int expectedWeekCount = (yearOnlyLog.Date >= monday && yearOnlyLog.Date <= sunday) ? 2250 : 1250;
        Assert.Equal(expectedWeekCount, model.WeekCount);

        // Week Time: 62.05 + 30.0 = 92.05 mins (01h32m)
        double expectedWeekMinutes = 92.05 + ((yearOnlyLog.Date >= monday && yearOnlyLog.Date <= sunday) ? 45.0 : 0.0);
        var expectedWeekTimeSpan = TimeSpan.FromSeconds(Math.Round(expectedWeekMinutes * 60));
        Assert.Equal($"{(int)expectedWeekTimeSpan.TotalHours:D2}h{expectedWeekTimeSpan.Minutes:D2}m", model.WeekTime);

        // Year: weekLog1 + weekLog2 + yearOnlyLog = 2250 characters
        Assert.Equal(2250, model.YearCount);

        // Year Time: 62.05 + 30.0 + 45.0 = 137.05 minutes = 8223 seconds = 02h17m
        Assert.Equal("02h17m", model.YearTime);

        // 1 active work, 1 completed work
        Assert.Equal(1, model.ActiveWorksCount);
    }

    [Fact]
    public async Task OnGetAsync_EmptyDatabase_ReturnsZeroAndFormattedZeroTime()
    {
        await using var database = await TestDatabase.CreateAsync();

        var model = new IndexModel(database.Context);
        await model.OnGetAsync();

        Assert.Equal(0, model.WeekCount);
        Assert.Equal("00h00m", model.WeekTime);
        Assert.Equal(0, model.YearCount);
        Assert.Equal("00h00m", model.YearTime);
        Assert.Equal(0, model.ActiveWorksCount);
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
