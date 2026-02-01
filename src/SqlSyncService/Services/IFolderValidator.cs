using SqlSyncService.Contracts.Folders;

namespace SqlSyncService.Services;

public interface IFolderValidator
{
    Task<ValidateFolderResponse> ValidateFolderAsync(ValidateFolderRequest request, CancellationToken cancellationToken = default);
}
