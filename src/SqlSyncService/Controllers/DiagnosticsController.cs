using Microsoft.AspNetCore.Mvc;
using SqlSyncService.Services;

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

	[HttpGet("throw-comparison-in-progress")]
	public IActionResult ThrowComparisonInProgress()
	{
		throw new ComparisonInProgressException();
	}
}
