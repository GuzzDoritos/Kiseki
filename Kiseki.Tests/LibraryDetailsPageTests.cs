using Kiseki.Core;
using Kiseki.Core.Entities;
using Kiseki.Web.Pages.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Kiseki.Tests;

public class LibraryDetailsPageTests
{
    [Fact]
    public async Task UpdateTitle_SuccessfullyUpdatesAndTrims()
    {
        await using var database = await TestDatabase.CreateAsync();
        var work = new MediaWork("Old Title");
        database.Context.MediaWorks.Add(work);
        await database.Context.SaveChangesAsync();

        var model = new DetailsModel(database.Context);
        var result = await model.OnPostUpdateTitleAsync(
            work.Id,
            new DetailsModel.UpdateTitleRequest { Title = "  New Updated Title  " },
            CancellationToken.None);

        var jsonResult = Assert.IsType<JsonResult>(result);
        Assert.NotNull(jsonResult.Value);

        var updatedWork = await database.Context.MediaWorks.FindAsync(work.Id);
        Assert.NotNull(updatedWork);
        Assert.Equal("New Updated Title", updatedWork.Title);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UpdateTitle_RejectsEmptyOrWhitespaceTitle(string? emptyTitle)
    {
        await using var database = await TestDatabase.CreateAsync();
        var work = new MediaWork("Existing Title");
        database.Context.MediaWorks.Add(work);
        await database.Context.SaveChangesAsync();

        var model = new DetailsModel(database.Context);
        var result = await model.OnPostUpdateTitleAsync(
            work.Id,
            new DetailsModel.UpdateTitleRequest { Title = emptyTitle },
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);

        var unchangedWork = await database.Context.MediaWorks.FindAsync(work.Id);
        Assert.NotNull(unchangedWork);
        Assert.Equal("Existing Title", unchangedWork.Title);
    }

    [Fact]
    public async Task UpdateTitle_ReturnsNotFoundForUnknownWork()
    {
        await using var database = await TestDatabase.CreateAsync();
        var model = new DetailsModel(database.Context);
        var result = await model.OnPostUpdateTitleAsync(
            Guid.NewGuid(),
            new DetailsModel.UpdateTitleRequest { Title = "Any Title" },
            CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task UpdateCharacterTotal_SuccessfullySetsManualOverrideAndRecalculates()
    {
        await using var database = await TestDatabase.CreateAsync();
        var work = new MediaWork("Test Book")
        {
            Logs =
            [
                new ImmersionLog
                {
                    Date = new DateOnly(2026, 9, 10),
                    CharactersRead = 50_000,
                    TimeSpentMinutes = 120
                }
            ]
        };
        work.LinkToJitenDeck(1234, 100_000);
        database.Context.MediaWorks.Add(work);
        await database.Context.SaveChangesAsync();

        var model = new DetailsModel(database.Context);
        var result = await model.OnPostUpdateCharacterTotalAsync(
            work.Id,
            new DetailsModel.UpdateCharacterTotalRequest { ManualCharacterCount = 200_000 },
            CancellationToken.None);

        var jsonResult = Assert.IsType<JsonResult>(result);
        Assert.NotNull(jsonResult.Value);

        var updatedWork = await database.Context.MediaWorks.FindAsync(work.Id);
        Assert.NotNull(updatedWork);
        Assert.Equal(200_000, updatedWork.ManualCharacterCountOverride);
        Assert.Equal(200_000, updatedWork.TotalCharacters);
        Assert.Equal(25.0, updatedWork.ProgressPercentage);
    }

    [Fact]
    public async Task UpdateCharacterTotal_ClearingOverrideRevertsToJitenCount()
    {
        await using var database = await TestDatabase.CreateAsync();
        var work = new MediaWork("Test Book")
        {
            ManualCharacterCountOverride = 250_000
        };
        work.LinkToJitenDeck(1234, 150_000);
        database.Context.MediaWorks.Add(work);
        await database.Context.SaveChangesAsync();

        var model = new DetailsModel(database.Context);
        var result = await model.OnPostUpdateCharacterTotalAsync(
            work.Id,
            new DetailsModel.UpdateCharacterTotalRequest { ManualCharacterCount = null },
            CancellationToken.None);

        Assert.IsType<JsonResult>(result);

        var updatedWork = await database.Context.MediaWorks.FindAsync(work.Id);
        Assert.NotNull(updatedWork);
        Assert.Null(updatedWork.ManualCharacterCountOverride);
        Assert.Equal(150_000, updatedWork.TotalCharacters);
    }

    [Fact]
    public async Task UpdateCharacterTotal_RejectsNegativeCharacterCount()
    {
        await using var database = await TestDatabase.CreateAsync();
        var work = new MediaWork("Test Book");
        database.Context.MediaWorks.Add(work);
        await database.Context.SaveChangesAsync();

        var model = new DetailsModel(database.Context);
        var result = await model.OnPostUpdateCharacterTotalAsync(
            work.Id,
            new DetailsModel.UpdateCharacterTotalRequest { ManualCharacterCount = -100 },
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);

        var unchangedWork = await database.Context.MediaWorks.FindAsync(work.Id);
        Assert.NotNull(unchangedWork);
        Assert.Null(unchangedWork.ManualCharacterCountOverride);
    }

    [Fact]
    public async Task UpdateCharacterTotal_ReturnsNotFoundForUnknownWork()
    {
        await using var database = await TestDatabase.CreateAsync();
        var model = new DetailsModel(database.Context);
        var result = await model.OnPostUpdateCharacterTotalAsync(
            Guid.NewGuid(),
            new DetailsModel.UpdateCharacterTotalRequest { ManualCharacterCount = 100_000 },
            CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task UpdateCharacterTotal_AutoCompletesWhenReadAmountMatchesOrExceedsTotal()
    {
        await using var database = await TestDatabase.CreateAsync();
        var work = new MediaWork("Test Book")
        {
            IsCompleted = false,
            Logs =
            [
                new ImmersionLog
                {
                    Date = new DateOnly(2026, 9, 10),
                    CharactersRead = 141_818,
                    TimeSpentMinutes = 300
                }
            ]
        };
        database.Context.MediaWorks.Add(work);
        await database.Context.SaveChangesAsync();

        var model = new DetailsModel(database.Context);
        var result = await model.OnPostUpdateCharacterTotalAsync(
            work.Id,
            new DetailsModel.UpdateCharacterTotalRequest { ManualCharacterCount = 141_818 },
            CancellationToken.None);

        Assert.IsType<JsonResult>(result);

        var updatedWork = await database.Context.MediaWorks.FindAsync(work.Id);
        Assert.NotNull(updatedWork);
        Assert.True(updatedWork.IsCompleted);
        Assert.Equal(100.0, updatedWork.ProgressPercentage);
    }

    [Fact]
    public async Task UpdateCharacterTotal_RevertsToNotCompletedWhenTotalIsIncreasedAboveRead()
    {
        await using var database = await TestDatabase.CreateAsync();
        var work = new MediaWork("Test Book")
        {
            IsCompleted = true,
            ManualCharacterCountOverride = 100_000,
            Logs =
            [
                new ImmersionLog
                {
                    Date = new DateOnly(2026, 9, 10),
                    CharactersRead = 100_000,
                    TimeSpentMinutes = 200
                }
            ]
        };
        database.Context.MediaWorks.Add(work);
        await database.Context.SaveChangesAsync();

        var model = new DetailsModel(database.Context);
        var result = await model.OnPostUpdateCharacterTotalAsync(
            work.Id,
            new DetailsModel.UpdateCharacterTotalRequest { ManualCharacterCount = 150_000 },
            CancellationToken.None);

        Assert.IsType<JsonResult>(result);

        var updatedWork = await database.Context.MediaWorks.FindAsync(work.Id);
        Assert.NotNull(updatedWork);
        Assert.False(updatedWork.IsCompleted);
        Assert.Equal(66.66666666666667, updatedWork.ProgressPercentage, 2);
    }

    [Fact]
    public async Task ToggleStatus_TogglesCompletionBetweenTrueAndFalse()
    {
        await using var database = await TestDatabase.CreateAsync();
        var work = new MediaWork("Test Book")
        {
            IsCompleted = false,
            Logs =
            [
                new ImmersionLog
                {
                    Date = new DateOnly(2026, 9, 10),
                    CharactersRead = 50_000,
                    TimeSpentMinutes = 100
                }
            ]
        };
        work.LinkToJitenDeck(100, 100_000);
        database.Context.MediaWorks.Add(work);
        await database.Context.SaveChangesAsync();

        var model = new DetailsModel(database.Context);

        // Toggle to true
        var result1 = await model.OnPostToggleStatusAsync(work.Id, CancellationToken.None);
        Assert.IsType<JsonResult>(result1);

        var updatedWork1 = await database.Context.MediaWorks.FindAsync(work.Id);
        Assert.NotNull(updatedWork1);
        Assert.True(updatedWork1.IsCompleted);
        Assert.Equal(100.0, updatedWork1.ProgressPercentage);

        // Toggle to false
        var result2 = await model.OnPostToggleStatusAsync(work.Id, CancellationToken.None);
        Assert.IsType<JsonResult>(result2);

        var updatedWork2 = await database.Context.MediaWorks.FindAsync(work.Id);
        Assert.NotNull(updatedWork2);
        Assert.False(updatedWork2.IsCompleted);
        Assert.Equal(50.0, updatedWork2.ProgressPercentage);
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;

        private TestDatabase(Microsoft.Data.Sqlite.SqliteConnection connection, Kiseki.Core.ImmersionDbContext context)
        {
            _connection = connection;
            Context = context;
        }

        public Kiseki.Core.ImmersionDbContext Context { get; }

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<Kiseki.Core.ImmersionDbContext>()
                .UseSqlite(connection)
                .Options;
            var context = new Kiseki.Core.ImmersionDbContext(options);
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
