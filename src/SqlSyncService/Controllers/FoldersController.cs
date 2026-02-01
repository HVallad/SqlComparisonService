using Microsoft.AspNetCore.Mvc;
using SqlSyncService.Contracts;
using SqlSyncService.Contracts.Folders;
using SqlSyncService.Services;

namespace SqlSyncService.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class FoldersController : ControllerBase
{
    private readonly IFolderValidator _folderValidator;

    public FoldersController(IFolderValidator folderValidator)
    {
        _folderValidator = folderValidator;
    }

    [HttpPost("validate")]
    public async Task<IActionResult> ValidateFolder([FromBody] ValidateFolderRequest request, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(request.Path))
        {
            var error = new ErrorDetail
            {
                Code = ErrorCodes.NotFound,
                Message = $"Folder '{request.Path}' was not found.",
                Field = "path",
                TraceId = HttpContext.TraceIdentifier,
                Timestamp = DateTime.UtcNow
            };

            return NotFound(new ErrorResponse { Error = error });
        }

        var result = await _folderValidator.ValidateFolderAsync(request, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }
}
