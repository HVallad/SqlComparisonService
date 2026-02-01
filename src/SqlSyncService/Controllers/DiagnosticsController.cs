using Microsoft.AspNetCore.Mvc;

namespace SqlSyncService.Controllers;

[ApiController]
[Route("api/diagnostics")]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class DiagnosticsController : ControllerBase
{
    [HttpGet("throw")]
    public IActionResult Throw()
    {
        throw new InvalidOperationException("Test exception from diagnostics endpoint.");
    }
}
