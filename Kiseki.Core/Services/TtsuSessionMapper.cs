using System.Globalization;
using Kiseki.Core.DTOs;
using Kiseki.Core.Entities;

namespace Kiseki.Core.Services;

public static class TtsuSessionMapper
{
    private const string DateFormat = "yyyy-MM-dd";

    public static ImmersionLog ToImmersionLog(this TtsuReaderDTO entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (!DateOnly.TryParseExact(
                entry.DateKey,
                DateFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
        {
            throw new FormatException(
                $"TTSU dateKey '{entry.DateKey}' is not in the expected {DateFormat} format.");
        }

        return new ImmersionLog
        {
            Date = date,
            CharactersRead = entry.CharactersRead,
            TimeSpentMinutes = entry.ReadingTime / 60d,
            Source = "ttsu"
        };
    }
}
