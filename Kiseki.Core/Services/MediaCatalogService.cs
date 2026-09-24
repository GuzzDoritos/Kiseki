using System.Security.Cryptography;
using System.Text;
using Kiseki.Core.Entities;
using Kiseki.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Kiseki.Core.Services;

/// <summary>
/// Compatibility boundary for changes which affect both a tracked copy and its
/// canonical installment.  Callers deliberately do not set either relationship
/// directly while the legacy series columns still exist.
/// </summary>
public sealed class MediaCatalogService(ImmersionDbContext context)
{
    /// <summary>
    /// Creates the same complete copy/installment graph for convenience paths
    /// which intentionally remain in-memory and have no DbContext yet.
    /// </summary>
    public static MediaWork CreateDetachedTrackedCopy(string title, MediaType mediaType)
    {
        var installment = new MediaInstallment(title, mediaType);
        var work = new MediaWork(title, mediaType: mediaType)
        {
            MediaInstallment = installment,
            MediaInstallmentId = installment.Id
        };
        return work;
    }

    /// <summary>Applies provider evidence to an unpersisted copy/installment graph.</summary>
    public static void LinkDetachedCopyToJiten(
        MediaWork work,
        JitenMediaSelection selection,
        JitenTitleChoice titleChoice = JitenTitleChoice.KeepCurrent)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(selection);
        if (work.MediaType != MediaType.Book || work.MediaInstallment is null)
        {
            throw new MediaCatalogConflictException("A detached Jiten link requires a book copy with an installment.");
        }

        var key = JitenKey(selection);
        var identity = work.MediaInstallment.ProviderIdentities.SingleOrDefault(item =>
            item.Provider == "jiten" && item.NormalizedKey == key)
            ?? new InstallmentProviderIdentity
            {
                Provider = "jiten",
                NormalizedKey = key,
                MediaInstallment = work.MediaInstallment,
                ProviderItemId = selection.SubdeckId ?? selection.DeckId,
                ParentProviderItemId = selection.SubdeckId is null ? null : selection.DeckId
            };
        if (!work.MediaInstallment.ProviderIdentities.Contains(identity))
        {
            work.MediaInstallment.ProviderIdentities.Add(identity);
        }

        selection.ApplyTo(work, titleChoice);
        ApplyCanonicalJitenMetadata(work.MediaInstallment, selection);
        AddJitenSnapshot(identity, selection);
    }

    public async Task<MediaWork> CreateTrackedCopyAsync(
        string title,
        MediaType mediaType,
        Guid? installmentId = null,
        Guid? seriesId = null,
        CancellationToken cancellationToken = default)
    {
        var installment = installmentId is null
            ? null
            : await LoadInstallmentAsync(installmentId.Value, cancellationToken);
        var series = seriesId is null
            ? null
            : await LoadSeriesAsync(seriesId.Value, cancellationToken);

        if (installment is not null && installment.MediaType != mediaType)
        {
            throw new MediaCatalogConflictException("A tracked copy and its installment must have the same media type.");
        }

        if (series is not null && series.MediaType != mediaType)
        {
            throw new MediaCatalogConflictException("A tracked copy and its series must have the same media type.");
        }

        if (installment is null)
        {
            installment = new MediaInstallment(title, mediaType)
            {
                MediaSeries = series
            };
            context.MediaInstallments.Add(installment);
        }
        else if (series is not null && installment.MediaSeriesId != series.Id)
        {
            if (installment.MediaSeriesId is not null)
            {
                throw new MediaCatalogConflictException("Choose an installment already assigned to the selected series, or move the installment explicitly.");
            }

            await SetInstallmentSeriesAsync(installment, series, cancellationToken);
        }

        var work = new MediaWork(title, mediaType: mediaType);
        SetCopyInstallment(work, installment);
        context.MediaWorks.Add(work);
        return work;
    }

    /// <summary>Attaches a compatibility installment to an existing tracked copy if it has none.</summary>
    public async Task<MediaInstallment> EnsureInstallmentAsync(
        MediaWork work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (work.MediaInstallmentId is Guid installmentId)
        {
            var installment = work.MediaInstallment ?? await LoadInstallmentAsync(installmentId, cancellationToken);
            if (installment.MediaType != work.MediaType)
            {
                throw new MediaCatalogConflictException("The stored copy and installment media types no longer agree.");
            }

            SetCopyInstallment(work, installment);
            return installment;
        }

        var newInstallment = new MediaInstallment(work.Title, work.MediaType)
        {
            MediaSeriesId = work.MediaSeriesId
        };
        context.MediaInstallments.Add(newInstallment);
        SetCopyInstallment(work, newInstallment);
        return newInstallment;
    }

    public async Task AssignCopyToInstallmentAsync(
        MediaWork work,
        Guid installmentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        var installment = await LoadInstallmentAsync(installmentId, cancellationToken);
        if (installment.MediaType != work.MediaType)
        {
            throw new MediaCatalogConflictException("A tracked copy and its installment must have the same media type.");
        }

        SetCopyInstallment(work, installment);
    }

    /// <summary>
    /// Assigns the copy's canonical installment to a series and synchronizes
    /// every linked copy's legacy series FK.  This is intentionally an
    /// installment move, not a per-copy legacy relationship edit.
    /// </summary>
    public async Task AssignCopyToSeriesAsync(
        MediaWork work,
        Guid? seriesId,
        CancellationToken cancellationToken = default)
    {
        var installment = await EnsureInstallmentAsync(work, cancellationToken);
        MediaSeries? series = null;
        if (seriesId is Guid id)
        {
            series = await LoadSeriesAsync(id, cancellationToken);
            if (series.MediaType != installment.MediaType)
            {
                throw new MediaCatalogConflictException("An installment and its series must have the same media type.");
            }
        }

        await SetInstallmentSeriesAsync(installment, series, cancellationToken);
    }

    /// <summary>
    /// Type changes never leave a linked copy pointing at an incompatible
    /// installment.  A changed copy becomes a standalone installment, keeping
    /// the prior installment and any other tracked copies intact.
    /// </summary>
    public async Task ChangeCopyMediaTypeAsync(
        MediaWork work,
        MediaType mediaType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        var installment = await EnsureInstallmentAsync(work, cancellationToken);
        if (mediaType == work.MediaType)
        {
            SetCopyInstallment(work, installment);
            return;
        }

        // A Jiten identity is a book-catalogue identity. Remove the legacy
        // link and its orphaned canonical claim before detaching to another
        // media type, so the old installment cannot retain a stale claim.
        if (work.HasJitenLink)
        {
            await UnlinkFromJitenAsync(work, cancellationToken);
        }

        var detached = new MediaInstallment(work.Title, mediaType);
        context.MediaInstallments.Add(detached);
        work.MediaType = mediaType;
        SetCopyInstallment(work, detached);
    }

    public async Task LinkToJitenAsync(
        MediaWork work,
        JitenMediaSelection selection,
        JitenTitleChoice titleChoice = JitenTitleChoice.KeepCurrent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(selection);
        if (work.MediaType != MediaType.Book)
        {
            throw new MediaCatalogConflictException("Jiten linking currently supports books only.");
        }

        var installment = await EnsureInstallmentAsync(work, cancellationToken);
        var key = JitenKey(selection);
        var existing = await context.InstallmentProviderIdentities
            .Include(identity => identity.Snapshots)
            .SingleOrDefaultAsync(identity => identity.Provider == "jiten" && identity.NormalizedKey == key, cancellationToken);
        if (existing is not null && existing.MediaInstallmentId != installment.Id)
        {
            throw new MediaCatalogConflictException("That Jiten item is already linked to a different installment. Review or consolidate the copies before relinking it.");
        }

        await RemoveObsoleteJitenIdentityAsync(work, installment, key, cancellationToken);
        existing ??= new InstallmentProviderIdentity
        {
            Provider = "jiten",
            NormalizedKey = key,
            MediaInstallment = installment,
            ProviderItemId = selection.SubdeckId ?? selection.DeckId,
            ParentProviderItemId = selection.SubdeckId is null ? null : selection.DeckId
        };
        if (context.Entry(existing).State == EntityState.Detached)
        {
            context.InstallmentProviderIdentities.Add(existing);
        }

        selection.ApplyTo(work, titleChoice);
        ApplyCanonicalJitenMetadata(installment, selection);
        AddJitenSnapshot(existing, selection);
    }

    public async Task UnlinkFromJitenAsync(MediaWork work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (!work.HasJitenLink)
        {
            return;
        }

        var installment = await EnsureInstallmentAsync(work, cancellationToken);
        await RemoveObsoleteJitenIdentityAsync(work, installment, replacementKey: null, cancellationToken);
        work.RemoveJitenLink();
    }

    private async Task<MediaInstallment> LoadInstallmentAsync(Guid id, CancellationToken cancellationToken)
    {
        var local = context.MediaInstallments.Local.FirstOrDefault(item => item.Id == id);
        if (local is not null && (local.MediaSeriesId is null || local.MediaSeries is not null))
        {
            return local;
        }

        return await context.MediaInstallments.Include(item => item.MediaSeries)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new MediaCatalogConflictException("The selected installment no longer exists.");
    }

    private async Task<MediaSeries> LoadSeriesAsync(Guid id, CancellationToken cancellationToken) =>
        context.MediaSeries.Local.FirstOrDefault(item => item.Id == id)
        ?? await context.MediaSeries.SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
        ?? throw new MediaCatalogConflictException("The selected series no longer exists.");

    private async Task SetInstallmentSeriesAsync(MediaInstallment installment, MediaSeries? series, CancellationToken cancellationToken)
    {
        installment.MediaSeries = series;
        installment.MediaSeriesId = series?.Id;
        installment.Version = Guid.NewGuid();

        var persistedCopies = await context.MediaWorks
            .Where(copy => copy.MediaInstallmentId == installment.Id)
            .ToListAsync(cancellationToken);
        var copies = persistedCopies
            .Concat(context.MediaWorks.Local.Where(copy => copy.MediaInstallmentId == installment.Id))
            .DistinctBy(copy => copy.Id);
        foreach (var copy in copies)
        {
            copy.MediaSeries = series;
            copy.MediaSeriesId = series?.Id;
            copy.Version = Guid.NewGuid();
        }
    }

    private static void SetCopyInstallment(MediaWork work, MediaInstallment installment)
    {
        if (installment.MediaType != work.MediaType)
        {
            throw new MediaCatalogConflictException("A tracked copy and its installment must have the same media type.");
        }

        work.MediaInstallment = installment;
        work.MediaInstallmentId = installment.Id;
        work.MediaSeriesId = installment.MediaSeriesId;
        work.MediaSeries = installment.MediaSeries;
        work.Version = Guid.NewGuid();
    }

    private async Task RemoveObsoleteJitenIdentityAsync(
        MediaWork work,
        MediaInstallment installment,
        string? replacementKey,
        CancellationToken cancellationToken)
    {
        if (!work.HasJitenLink)
        {
            return;
        }

        var oldKey = work.JitenSubdeckId is int child
            ? $"subdeck:{work.JitenDeckId}:{child}"
            : $"deck:{work.JitenDeckId}";
        if (oldKey == replacementKey)
        {
            return;
        }

        var identity = await context.InstallmentProviderIdentities
            .SingleOrDefaultAsync(item => item.Provider == "jiten" && item.NormalizedKey == oldKey && item.MediaInstallmentId == installment.Id, cancellationToken);
        if (identity is null)
        {
            return;
        }

        var retainedByAnotherCopy = await context.MediaWorks.AnyAsync(copy =>
            copy.Id != work.Id && copy.MediaInstallmentId == installment.Id &&
            copy.JitenDeckId == work.JitenDeckId && copy.JitenSubdeckId == work.JitenSubdeckId,
            cancellationToken);
        if (!retainedByAnotherCopy)
        {
            context.InstallmentProviderIdentities.Remove(identity);
        }
    }

    private static string JitenKey(JitenMediaSelection selection) => selection.SubdeckId is int child
        ? $"subdeck:{selection.DeckId}:{child}"
        : $"deck:{selection.DeckId}";

    private static void ApplyCanonicalJitenMetadata(MediaInstallment installment, JitenMediaSelection selection)
    {
        installment.CanonicalTitle = selection.DisplayTitle;
        installment.CanonicalCharacterCount = selection.CharacterCount > 0 ? selection.CharacterCount : null;
        installment.CanonicalCoverUrl = selection.CoverUrl;
        installment.CanonicalCoverSource = selection.CoverEvidence switch
        {
            JitenCoverEvidence.Specific => CanonicalCoverSource.ProviderExact,
            JitenCoverEvidence.ParentFallback => CanonicalCoverSource.ProviderParentFallback,
            _ => CanonicalCoverSource.None
        };
        installment.Version = Guid.NewGuid();
    }

    private static void AddJitenSnapshot(InstallmentProviderIdentity identity, JitenMediaSelection selection)
    {
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{identity.NormalizedKey}|{selection.DisplayTitle}|{selection.CharacterCount}|{selection.CoverUrl}")));
        if (identity.Snapshots.Any(snapshot => snapshot.Fingerprint == fingerprint))
        {
            return;
        }

        identity.Snapshots.Add(new InstallmentProviderSnapshot
        {
            Provider = identity.Provider,
            NormalizedKey = identity.NormalizedKey,
            Fingerprint = fingerprint,
            Title = selection.DisplayTitle,
            CharacterCount = selection.CharacterCount,
            CoverUrl = selection.CoverUrl,
            CoverSource = selection.CoverEvidence switch
            {
                JitenCoverEvidence.Specific => CanonicalCoverSource.ProviderExact,
                JitenCoverEvidence.ParentFallback => CanonicalCoverSource.ProviderParentFallback,
                _ => CanonicalCoverSource.None
            },
            ReleaseState = ReleaseState.Unknown,
            ObservedAtUtc = DateTimeOffset.UtcNow,
            IsComplete = true
        });
    }
}

public sealed class MediaCatalogConflictException(string message) : InvalidOperationException(message);
