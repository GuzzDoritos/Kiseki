using Kiseki.Core.Entities;
using Kiseki.Core.Models.Pace;

namespace Kiseki.Core.Services;

public interface IReadingPaceService
{
    Task<ReadingPaceProfile> GetPaceProfileAsync(
        DateOnly? referenceDate = null,
        CancellationToken cancellationToken = default);

    ReadingTimeEstimate EstimateTime(
        int remainingCharacters,
        ReadingPaceProfile pace,
        bool isCompleted = false,
        int uncountedVolumesCount = 0);

    ReadingTimeEstimate EstimateSeriesTime(
        MediaSeries series,
        ReadingPaceProfile pace);

    ReadingTimeEstimate EstimateInstallmentTime(
        SeriesInstallment installment,
        ReadingPaceProfile pace);
}
