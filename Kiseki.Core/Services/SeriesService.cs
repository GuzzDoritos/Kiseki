using Kiseki.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Kiseki.Core.Services;

public class SeriesService : ISeriesService
{
    private const int MaxCoverUrlLength = 2048;
    private readonly ImmersionDbContext _dbContext;
    private readonly IJitenApiClient _jitenApiClient;

    public SeriesService(
        ImmersionDbContext dbContext,
        IJitenApiClient jitenApiClient)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _jitenApiClient = jitenApiClient ?? throw new ArgumentNullException(nameof(jitenApiClient));
    }

    public async Task<MediaSeries> CreateManualSeriesAsync(
        string title,
        MediaType mediaType = MediaType.Book,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Title cannot be empty.", nameof(title));
        }

        var series = new MediaSeries(title.Trim(), mediaType);
        _dbContext.MediaSeries.Add(series);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return series;
    }

    public async Task<MediaSeries> CreateSeriesFromJitenDeckAsync(
        int deckId,
        CancellationToken cancellationToken = default)
    {
        if (deckId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(deckId), "Deck ID must be greater than zero.");
        }

        var detail = await _jitenApiClient.GetDeckDetailAsync(deckId, cancellationToken);
        if (detail?.MainDeck == null)
        {
            throw new InvalidOperationException($"Jiten deck detail could not be retrieved for deck ID {deckId}.");
        }

        var mainDeck = detail.MainDeck;
        var seriesTitle = FirstNonEmpty(
            mainDeck.OriginalTitle,
            mainDeck.EnglishTitle,
            mainDeck.RomajiTitle) ?? $"Deck {deckId}";

        var seriesCoverUrl = NormalizeCoverUrl(mainDeck.CoverName);

        var series = new MediaSeries(seriesTitle, MediaType.Book)
        {
            JitenDeckId = deckId,
            CoverUrl = seriesCoverUrl
        };

        if (detail.SubDecks.Count > 0)
        {
            for (var i = 0; i < detail.SubDecks.Count; i++)
            {
                var subdeck = detail.SubDecks[i];
                var installmentTitle = FirstNonEmpty(
                    subdeck.OriginalTitle,
                    subdeck.EnglishTitle,
                    subdeck.RomajiTitle) ?? $"Volume {i + 1}";

                var installmentCover = NormalizeCoverUrl(subdeck.CoverName) ?? seriesCoverUrl;

                var installment = new SeriesInstallment(
                    series.Id,
                    sequenceNumber: i + 1,
                    title: installmentTitle,
                    jitenSubdeckId: subdeck.DeckId,
                    jitenCharacterCount: subdeck.CharacterCount,
                    coverUrl: installmentCover);

                series.Installments.Add(installment);
            }
        }
        else
        {
            var installment = new SeriesInstallment(
                series.Id,
                sequenceNumber: 1,
                title: seriesTitle,
                jitenSubdeckId: mainDeck.DeckId,
                jitenCharacterCount: mainDeck.CharacterCount,
                coverUrl: seriesCoverUrl);

            series.Installments.Add(installment);
        }

        // Frictionless auto-matching: match existing library works by Jiten ID
        var candidateWorks = await _dbContext.MediaWorks
            .Where(w => w.MediaSeriesId == null && (w.JitenSubdeckId != null || w.JitenDeckId != null))
            .ToListAsync(cancellationToken);

        foreach (var installment in series.Installments)
        {
            if (!installment.JitenSubdeckId.HasValue)
            {
                continue;
            }

            var subId = installment.JitenSubdeckId.Value;
            var match = candidateWorks.FirstOrDefault(w =>
                (w.JitenSubdeckId.HasValue && w.JitenSubdeckId.Value == subId) ||
                (!w.JitenSubdeckId.HasValue && w.JitenDeckId == subId));

            if (match != null)
            {
                installment.MediaWorkId = match.Id;
                installment.MediaWork = match;
                match.MediaSeriesId = series.Id;
                candidateWorks.Remove(match);
            }
        }

        _dbContext.MediaSeries.Add(series);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return series;
    }

    public async Task<MediaSeries?> GetSeriesDetailsAsync(
        Guid seriesId,
        CancellationToken cancellationToken = default)
    {
        var series = await _dbContext.MediaSeries
            .Include(s => s.Installments)
            .ThenInclude(i => i.MediaWork!)
            .ThenInclude(w => w.Logs)
            .FirstOrDefaultAsync(s => s.Id == seriesId, cancellationToken);

        if (series != null)
        {
            series.Installments = series.Installments.OrderBy(i => i.SequenceNumber).ToList();
        }

        return series;
    }

    public async Task<IReadOnlyList<MediaWork>> GetAvailableLibraryWorksAsync(
        Guid seriesId,
        CancellationToken cancellationToken = default)
    {
        var assignedWorkIds = await _dbContext.SeriesInstallments
            .Where(i => i.MediaSeriesId == seriesId && i.MediaWorkId != null)
            .Select(i => i.MediaWorkId!.Value)
            .ToListAsync(cancellationToken);

        return await _dbContext.MediaWorks
            .AsNoTracking()
            .Include(w => w.Logs)
            .Where(w => w.MediaType == MediaType.Book && !assignedWorkIds.Contains(w.Id))
            .OrderBy(w => w.Title)
            .ToListAsync(cancellationToken);
    }

    public async Task LinkWorkToInstallmentAsync(
        Guid installmentId,
        Guid mediaWorkId,
        CancellationToken cancellationToken = default)
    {
        var installment = await _dbContext.SeriesInstallments
            .FirstOrDefaultAsync(i => i.Id == installmentId, cancellationToken);
        if (installment == null)
        {
            throw new InvalidOperationException($"Installment '{installmentId}' not found.");
        }

        var work = await _dbContext.MediaWorks
            .FirstOrDefaultAsync(w => w.Id == mediaWorkId, cancellationToken);
        if (work == null)
        {
            throw new InvalidOperationException($"Library work '{mediaWorkId}' not found.");
        }

        // If installment already had another work, clear old work's series reference
        if (installment.MediaWorkId.HasValue && installment.MediaWorkId.Value != mediaWorkId)
        {
            var oldWork = await _dbContext.MediaWorks
                .FirstOrDefaultAsync(w => w.Id == installment.MediaWorkId.Value, cancellationToken);
            if (oldWork != null && oldWork.MediaSeriesId == installment.MediaSeriesId)
            {
                oldWork.MediaSeriesId = null;
            }
        }

        installment.MediaWorkId = work.Id;
        installment.MediaWork = work;
        work.MediaSeriesId = installment.MediaSeriesId;

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task UnlinkWorkFromInstallmentAsync(
        Guid installmentId,
        CancellationToken cancellationToken = default)
    {
        var installment = await _dbContext.SeriesInstallments
            .Include(i => i.MediaWork)
            .FirstOrDefaultAsync(i => i.Id == installmentId, cancellationToken);
        if (installment == null)
        {
            throw new InvalidOperationException($"Installment '{installmentId}' not found.");
        }

        if (installment.MediaWork != null)
        {
            installment.MediaWork.MediaSeriesId = null;
        }
        installment.MediaWorkId = null;
        installment.MediaWork = null;

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<SeriesInstallment> AddInstallmentAsync(
        Guid seriesId,
        string title,
        int characterCount = 0,
        int? jitenSubdeckId = null,
        string? coverUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Title cannot be empty.", nameof(title));
        }

        var series = await _dbContext.MediaSeries
            .Include(s => s.Installments)
            .FirstOrDefaultAsync(s => s.Id == seriesId, cancellationToken);
        if (series == null)
        {
            throw new InvalidOperationException($"Series '{seriesId}' not found.");
        }

        var nextSeq = (series.Installments.MaxBy(i => i.SequenceNumber)?.SequenceNumber ?? 0) + 1;
        var normalizedCover = NormalizeCoverUrl(coverUrl) ?? series.CoverUrl;

        var installment = new SeriesInstallment(
            seriesId,
            sequenceNumber: nextSeq,
            title: title.Trim(),
            jitenSubdeckId: jitenSubdeckId,
            jitenCharacterCount: characterCount,
            coverUrl: normalizedCover);

        // Check if an unassigned library work matches the Jiten ID
        if (jitenSubdeckId.HasValue)
        {
            var subId = jitenSubdeckId.Value;
            var match = await _dbContext.MediaWorks
                .FirstOrDefaultAsync(w => w.MediaSeriesId == null &&
                    ((w.JitenSubdeckId.HasValue && w.JitenSubdeckId.Value == subId) ||
                     (!w.JitenSubdeckId.HasValue && w.JitenDeckId == subId)), cancellationToken);

            if (match != null)
            {
                installment.MediaWorkId = match.Id;
                installment.MediaWork = match;
                match.MediaSeriesId = series.Id;
            }
        }

        _dbContext.SeriesInstallments.Add(installment);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return installment;
    }

    public async Task RemoveInstallmentAsync(
        Guid installmentId,
        CancellationToken cancellationToken = default)
    {
        var installment = await _dbContext.SeriesInstallments
            .Include(i => i.MediaWork)
            .FirstOrDefaultAsync(i => i.Id == installmentId, cancellationToken);
        if (installment == null)
        {
            throw new InvalidOperationException($"Installment '{installmentId}' not found.");
        }

        if (installment.MediaWork != null)
        {
            installment.MediaWork.MediaSeriesId = null;
        }

        _dbContext.SeriesInstallments.Remove(installment);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteSeriesAsync(
        Guid seriesId,
        CancellationToken cancellationToken = default)
    {
        var series = await _dbContext.MediaSeries
            .Include(s => s.Installments)
            .Include(s => s.Works)
            .FirstOrDefaultAsync(s => s.Id == seriesId, cancellationToken);

        if (series == null)
        {
            throw new InvalidOperationException($"Series '{seriesId}' not found.");
        }

        // 1. Unlink any works pointing directly to this series so books and reading history are preserved
        foreach (var work in series.Works)
        {
            work.MediaSeriesId = null;
        }

        var otherLinkedWorks = await _dbContext.MediaWorks
            .Where(w => w.MediaSeriesId == seriesId)
            .ToListAsync(cancellationToken);

        foreach (var work in otherLinkedWorks)
        {
            work.MediaSeriesId = null;
        }

        // 2. Unlink installments from media works
        foreach (var installment in series.Installments)
        {
            installment.MediaWorkId = null;
            installment.MediaWork = null;
        }

        // 3. Remove all installments
        _dbContext.SeriesInstallments.RemoveRange(series.Installments);

        // 4. Remove series
        _dbContext.MediaSeries.Remove(series);

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MediaSeries>> GetAllSeriesAsync(
        MediaType? mediaType = null,
        string? search = null,
        CancellationToken cancellationToken = default)
    {
        var query = _dbContext.MediaSeries
            .Include(s => s.Installments)
                .ThenInclude(i => i.MediaWork)
                    .ThenInclude(w => w!.Logs)
            .AsNoTracking()
            .AsQueryable();

        if (mediaType.HasValue)
        {
            query = query.Where(s => s.MediaType == mediaType.Value);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var trimmedLower = search.Trim().ToLower();
            query = query.Where(s => s.Title.ToLower().Contains(trimmedLower));
        }

        var seriesList = await query
            .OrderBy(s => s.Title)
            .ToListAsync(cancellationToken);

        foreach (var s in seriesList)
        {
            s.Installments = s.Installments.OrderBy(i => i.SequenceNumber).ToList();
        }

        return seriesList;
    }

    private static string? NormalizeCoverUrl(string? coverUrl)
    {
        if (string.IsNullOrWhiteSpace(coverUrl) ||
            coverUrl.Trim().Equals("nocover.jpg", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var normalized = coverUrl.Trim();
        if (normalized.Length <= MaxCoverUrlLength && normalized.StartsWith('/'))
        {
            return normalized;
        }

        return normalized.Length <= MaxCoverUrlLength &&
               Uri.TryCreate(normalized, UriKind.Absolute, out var uri) &&
               uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? uri.AbsoluteUri
            : null;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
    }
}

