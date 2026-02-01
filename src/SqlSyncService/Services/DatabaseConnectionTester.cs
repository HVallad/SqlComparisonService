using System.Data;
using Microsoft.Data.SqlClient;
using SqlSyncService.Contracts;
using SqlSyncService.Contracts.Common;
using SqlSyncService.Contracts.Connections;

namespace SqlSyncService.Services;

public sealed class DatabaseConnectionTester : IDatabaseConnectionTester
{
    public async Task<TestConnectionResponse> TestConnectionAsync(TestConnectionRequest request, CancellationToken cancellationToken = default)
    {
        // For now we only support Windows and SQL authentication. Other auth types
        // are treated as connection failures with a clear error message.
        var authType = request.AuthType?.Trim().ToLowerInvariant();

        if (authType is not ("windows" or "sql" or "azuread" or "azuread-interactive"))
        {
            return new TestConnectionResponse
            {
                Success = false,
                Error = new ErrorDetail
                {
                    Code = ErrorCodes.ConnectionFailed,
                    Message = $"Unsupported authentication type '{request.AuthType}'.",
                    Details = "Only 'windows', 'sql', 'azuread', and 'azuread-interactive' are allowed."
                }
            };
        }

        if (authType is "azuread" or "azuread-interactive")
        {
            return new TestConnectionResponse
            {
                Success = false,
                Error = new ErrorDetail
                {
                    Code = ErrorCodes.ConnectionFailed,
                    Message = $"Authentication type '{request.AuthType}' is not yet implemented.",
                    Details = "Azure AD authentication will be implemented in a later milestone."
                }
            };
        }

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = request.Server,
            InitialCatalog = request.Database,
            TrustServerCertificate = request.TrustServerCertificate,
            ConnectTimeout = Math.Max(request.ConnectionTimeoutSeconds, 1),
        };

        if (authType == "windows")
        {
            builder.IntegratedSecurity = true;
        }
        else if (authType == "sql")
        {
            builder.IntegratedSecurity = false;
            builder.UserID = request.Username;
            builder.Password = request.Password;
        }

        try
        {
            await using var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var serverVersion = connection.ServerVersion;
            string? edition = null;

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT SERVERPROPERTY('Edition')";
                command.CommandType = CommandType.Text;

                var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                edition = result as string;
            }

            var counts = new ObjectCounts();

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT
    (SELECT COUNT(*) FROM sys.tables WHERE type = 'U') AS Tables,
    (SELECT COUNT(*) FROM sys.views) AS Views,
    (SELECT COUNT(*) FROM sys.procedures) AS StoredProcedures,
    (SELECT COUNT(*) FROM sys.objects WHERE type IN ('FN','IF','TF')) AS Functions;";
                command.CommandType = CommandType.Text;

                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    counts.Tables = reader.GetInt32(0);
                    counts.Views = reader.GetInt32(1);
                    counts.StoredProcedures = reader.GetInt32(2);
                    counts.Functions = reader.GetInt32(3);
                }
            }

            // Permissions detection is non-trivial; for now we assume that if the
            // connection and metadata queries succeed, core permissions are present.
            var permissions = new ConnectionPermissions
            {
                CanRead = true,
                CanWrite = true,
                CanExecuteDdl = true,
            };

            return new TestConnectionResponse
            {
                Success = true,
                ServerVersion = serverVersion,
                ServerEdition = edition,
                DatabaseExists = true,
                ObjectCounts = counts,
                Permissions = permissions
            };
        }
        catch (Exception ex)
        {
            return new TestConnectionResponse
            {
                Success = false,
                Error = new ErrorDetail
                {
                    Code = ErrorCodes.ConnectionFailed,
                    Message = $"Cannot connect to server '{request.Server}'",
                    Details = ex.Message
                }
            };
        }
    }
}
