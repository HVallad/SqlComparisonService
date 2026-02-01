using Microsoft.AspNetCore.Mvc;

namespace SqlSyncService.Controllers;

[ApiController]
[Route("api/[controller]")]
public class HealthController : ControllerBase
{
	[HttpGet]
	public IActionResult Get()
	{
		// Milestone 1: hard-coded healthy response
		return Ok(new { status = "healthy" });
	}
}
