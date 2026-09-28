using Kiseki.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Kiseki.Core.Services;

public class LibraryManagementService : ILibraryManagementService
{
    private readonly ImmersionDbContext _dbContext;

    public LibraryManagementService(ImmersionDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task DeleteMediaWorkAsync(
        Guid mediaWorkId,
        CancellationToken cancellationToken = default)
    {
        var work = await _dbContext.MediaWorks
            .Include(w => w.Logs)
            .FirstOrDefaultAsync(w => w.Id == mediaWorkId, cancellationToken);

        if (work == null)
        {
            throw new InvalidOperationException($"Media work '{mediaWorkId}' not found.");
        }

        // 1. Unlink any series installments pointing to this book so the series structure is preserved
        var linkedInstallments = await _dbContext.SeriesInstallments
            .Where(i => i.MediaWorkId == mediaWorkId)
            .ToListAsync(cancellationToken);

        foreach (var installment in linkedInstallments)
        {
            installment.MediaWorkId = null;
            installment.MediaWork = null;
        }

        // 2. Remove all immersion logs for this work.
        // Explicit removal ensures foreign key restriction on TtsuBinding (log.TtsuBindingId) is not violated.
        var logs = await _dbContext.ImmersionLogs
            .Where(l => l.MediaWorkId == mediaWorkId)
            .ToListAsync(cancellationToken);

        _dbContext.ImmersionLogs.RemoveRange(logs);

        // 3. Remove TTSU binding if present
        var binding = await _dbContext.TtsuBindings
            .FirstOrDefaultAsync(b => b.MediaWorkId == mediaWorkId, cancellationToken);

        if (binding != null)
        {
            _dbContext.TtsuBindings.Remove(binding);
        }

        // 4. Remove the media work itself
        _dbContext.MediaWorks.Remove(work);

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteImmersionLogAsync(
        Guid immersionLogId,
        Guid? mediaWorkId = null,
        CancellationToken cancellationToken = default)
    {
        var log = await _dbContext.ImmersionLogs
            .FirstOrDefaultAsync(l => l.Id == immersionLogId, cancellationToken);

        if (log == null)
        {
            throw new InvalidOperationException($"Immersion log '{immersionLogId}' not found.");
        }

        if (mediaWorkId.HasValue && log.MediaWorkId != mediaWorkId.Value)
        {
            throw new InvalidOperationException(
                $"Immersion log '{immersionLogId}' does not belong to work '{mediaWorkId.Value}'.");
        }

        _dbContext.ImmersionLogs.Remove(log);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
