namespace Kiseki.Core.Services;

public interface ILibraryManagementService
{
    Task DeleteMediaWorkAsync(
        Guid mediaWorkId,
        CancellationToken cancellationToken = default);

    Task DeleteImmersionLogAsync(
        Guid immersionLogId,
        Guid? mediaWorkId = null,
        CancellationToken cancellationToken = default);
}
