using Microsoft.AspNetCore.Mvc;
using SqlSyncService.Contracts;
using SqlSyncService.Contracts.Connections;
using SqlSyncService.Services;

namespace SqlSyncService.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class ConnectionsController : ControllerBase
{
    private readonly IDatabaseConnectionTester _connectionTester;

    public ConnectionsController(IDatabaseConnectionTester connectionTester)
    {
        _connectionTester = connectionTester;
    }

    [HttpPost("test")]
    public async Task<IActionResult> TestConnection([FromBody] TestConnectionRequest request, CancellationToken cancellationToken)
    {
        // Additional validation: username/password required when authType is sql.
        if (request.AuthType.Equals("sql", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            {
                var error = new ErrorDetail
                {
                    Code = ErrorCodes.ValidationError,
                    Message = "Username and password are required when authType is 'sql'.",
                    Field = "username",
                    TraceId = HttpContext.TraceIdentifier,
                    Timestamp = DateTime.UtcNow
                };

                return BadRequest(new ErrorResponse { Error = error });
            }
        }

        var result = await _connectionTester.TestConnectionAsync(request, cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            return UnprocessableEntity(result);
        }

        return Ok(result);
    }
}
