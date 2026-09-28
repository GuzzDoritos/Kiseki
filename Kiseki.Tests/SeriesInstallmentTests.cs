using Kiseki.Core;
using Kiseki.Core.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Kiseki.Tests;

public class SeriesInstallmentTests
{
    [Fact]
    public void UnlinkedInstallment_UsesJitenCharacterCount_AndZeroRead()
    {
        var seriesId = Guid.NewGuid();
        var installment = new SeriesInstallment(
            seriesId,
            sequenceNumber: 1,
            title: "Volume 1",
            jitenSubdeckId: 101,
            jitenCharacterCount: 120_000,
            coverUrl: "https://example.com/vol1.jpg");

        Assert.Equal(120_000, installment.EffectiveTotalCharacters);
        Assert.Equal(0, installment.EffectiveCharactersRead);
        Assert.False(installment.IsCompleted);
        Assert.Equal(0.0, installment.ProgressPercentage);
        Assert.Equal("https://example.com/vol1.jpg", installment.EffectiveCoverUrl);
    }

    [Fact]
    public void LinkedInstallment_UsesMediaWorkCharacters_WhenAvailable()
    {
        var seriesId = Guid.NewGuid();
        var installment = new SeriesInstallment(
            seriesId,
            sequenceNumber: 1,
            title: "Volume 1",
            jitenSubdeckId: 101,
            jitenCharacterCount: 120_000,
            coverUrl: "https://example.com/jiten-vol1.jpg");

        var work = new MediaWork("Volume 1");
        work.UpdateCoverUrl("https://books.google.com/covers/custom.jpg");
        // Give work a TTSU character count of 125,000 (overrides Jiten's 120,000)
        work.UpdateTtsuCharacterCount(125_000);
        work.Logs.Add(new ImmersionLog { CharactersRead = 50_000 });

        installment.MediaWork = work;
        installment.MediaWorkId = work.Id;

        Assert.Equal(125_000, installment.EffectiveTotalCharacters);
        Assert.Equal(50_000, installment.EffectiveCharactersRead);
        Assert.False(installment.IsCompleted);
        Assert.Equal(40.0, installment.ProgressPercentage);
        // Cover precedence: prefers work's cover over installment's jiten cover
        Assert.Equal("https://books.google.com/covers/custom.jpg", installment.EffectiveCoverUrl);
    }

    [Fact]
    public void LinkedInstallment_WhenWorkIsCompleted_CountsFullCharacters()
    {
        var seriesId = Guid.NewGuid();
        var installment = new SeriesInstallment(
            seriesId,
            sequenceNumber: 1,
            title: "Volume 1",
            jitenSubdeckId: 101,
            jitenCharacterCount: 100_000);

        var work = new MediaWork("Volume 1")
        {
            IsCompleted = true
        };
        work.Logs.Add(new ImmersionLog { CharactersRead = 80_000 });

        installment.MediaWork = work;

        Assert.Equal(100_000, installment.EffectiveTotalCharacters);
        Assert.Equal(100_000, installment.EffectiveCharactersRead);
        Assert.True(installment.IsCompleted);
        Assert.Equal(100.0, installment.ProgressPercentage);
    }

    [Fact]
    public void MediaSeries_CalculatesAggregateProgressAcrossInstallments()
    {
        var series = new MediaSeries("Spice and Wolf", MediaType.Book);

        var vol1 = new SeriesInstallment(series.Id, 1, "Volume 1", 101, 100_000, "https://example.com/vol1.jpg");
        var work1 = new MediaWork("Volume 1") { IsCompleted = true };
        vol1.MediaWork = work1;

        var vol2 = new SeriesInstallment(series.Id, 2, "Volume 2", 102, 100_000, "https://example.com/vol2.jpg");
        var work2 = new MediaWork("Volume 2");
        work2.Logs.Add(new ImmersionLog { CharactersRead = 50_000 });
        vol2.MediaWork = work2;

        var vol3 = new SeriesInstallment(series.Id, 3, "Volume 3", 103, 100_000); // Unread, unlinked

        series.Installments.AddRange([vol1, vol2, vol3]);

        Assert.Equal(300_000, series.TotalCharacters);
        Assert.Equal(150_000, series.CurrentCharactersRead);
        Assert.Equal(1, series.CompletedInstallmentsCount);
        Assert.Equal(50.0, series.ProgressPercentage);
        // Inherits cover from first installment with cover
        Assert.Equal("https://example.com/vol1.jpg", series.EffectiveCoverUrl);
    }

    [Fact]
    public async Task ImmersionDbContext_PersistsSeriesAndInstallments_WithCascadeAndSetNull()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ImmersionDbContext>()
            .UseSqlite(connection)
            .Options;

        await using (var context = new ImmersionDbContext(options))
        {
            await context.Database.EnsureCreatedAsync();

            var work = new MediaWork("Volume 1");
            context.MediaWorks.Add(work);

            var series = new MediaSeries("Test Series", MediaType.Book)
            {
                CoverUrl = "https://example.com/series.jpg"
            };
            var installment = new SeriesInstallment(series.Id, 1, "Volume 1", 101, 100_000)
            {
                MediaWorkId = work.Id
            };
            series.Installments.Add(installment);

            context.MediaSeries.Add(series);
            await context.SaveChangesAsync();
        }

        // Verify loaded correctly
        await using (var context = new ImmersionDbContext(options))
        {
            var loadedSeries = await context.MediaSeries
                .Include(s => s.Installments)
                .ThenInclude(i => i.MediaWork)
                .SingleAsync();

            Assert.Single(loadedSeries.Installments);
            Assert.NotNull(loadedSeries.Installments[0].MediaWork);
            Assert.Equal("Volume 1", loadedSeries.Installments[0].MediaWork!.Title);

            // Delete the MediaWork -> Installment should NOT be deleted, MediaWorkId set to null
            var workToDelete = await context.MediaWorks.SingleAsync();
            context.MediaWorks.Remove(workToDelete);
            await context.SaveChangesAsync();
        }

        await using (var context = new ImmersionDbContext(options))
        {
            var installmentAfterWorkDeleted = await context.SeriesInstallments.SingleAsync();
            Assert.Null(installmentAfterWorkDeleted.MediaWorkId);
            Assert.Equal("Volume 1", installmentAfterWorkDeleted.Title);

            // Delete the Series -> Installment should be cascade deleted
            var seriesToDelete = await context.MediaSeries.SingleAsync();
            context.MediaSeries.Remove(seriesToDelete);
            await context.SaveChangesAsync();
        }

        await using (var context = new ImmersionDbContext(options))
        {
            Assert.Empty(await context.SeriesInstallments.ToListAsync());
        }
    }
}
