using System.Text.Json;
using Kiseki.Core.DTOs;
using Kiseki.Core.Services;

namespace Kiseki.Tests;

public class TtsuSessionMapperTests
{
    [Fact]
    public void ToImmersionLog_MapsDateAndConvertsSecondsToMinutes()
    {
        const string json = """
            {
              "title": "Test book",
              "dateKey": "2026-08-05",
              "charactersRead": 10370,
              "readingTime": 2920.925,
              "lastStatisticModified": 1785984891008
            }
            """;
        var entry = JsonSerializer.Deserialize<TtsuReaderDTO>(json);

        var log = Assert.IsType<TtsuReaderDTO>(entry).ToImmersionLog();

        Assert.Equal(new DateOnly(2026, 8, 5), log.Date);
        Assert.Equal(10370, log.CharactersRead);
        Assert.Equal(2920.925 / 60d, log.TimeSpentMinutes, precision: 10);
        Assert.Equal("ttsu", log.Source);
    }

    [Fact]
    public void ToImmersionLog_RejectsAnUnexpectedDateFormat()
    {
        var entry = new TtsuReaderDTO { DateKey = "08/05/2026" };

        var exception = Assert.Throws<FormatException>(entry.ToImmersionLog);

        Assert.Contains("yyyy-MM-dd", exception.Message);
    }
}
