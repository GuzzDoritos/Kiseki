using Kiseki.Core.Entities;

namespace Kiseki.Core.Services;

public interface ISeriesService
{
    Task<MediaSeries> CreateManualSeriesAsync(
        string title,
        MediaType mediaType = MediaType.Book,
        CancellationToken cancellationToken = default);

    Task<MediaSeries> CreateSeriesFromJitenDeckAsync(
        int deckId,
        CancellationToken cancellationToken = default);

    Task<MediaSeries?> GetSeriesDetailsAsync(
        Guid seriesId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MediaWork>> GetAvailableLibraryWorksAsync(
        Guid seriesId,
        CancellationToken cancellationToken = default);

    Task LinkWorkToInstallmentAsync(
        Guid installmentId,
        Guid mediaWorkId,
        CancellationToken cancellationToken = default);

    Task UnlinkWorkFromInstallmentAsync(
        Guid installmentId,
        CancellationToken cancellationToken = default);

    Task<SeriesInstallment> AddInstallmentAsync(
        Guid seriesId,
        string title,
        int characterCount = 0,
        int? jitenSubdeckId = null,
        string? coverUrl = null,
        CancellationToken cancellationToken = default);

    Task RemoveInstallmentAsync(
        Guid installmentId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MediaSeries>> GetAllSeriesAsync(
        MediaType? mediaType = null,
        string? search = null,
        CancellationToken cancellationToken = default);
}

