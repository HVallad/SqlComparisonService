using System.Text;

namespace SqlSyncService.Domain.Subscriptions;

public class DatabaseConnection
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Server { get; set; } = string.Empty;
    public string Database { get; set; } = string.Empty;
    public AuthenticationType AuthType { get; set; } = AuthenticationType.WindowsIntegrated;
    public string? Username { get; set; }
    public string? EncryptedPassword { get; set; }
    public bool TrustServerCertificate { get; set; }
    public int ConnectionTimeoutSeconds { get; set; } = 30;

    public string ConnectionString => BuildConnectionString();

    private string BuildConnectionString()
    {
        var builder = new StringBuilder();

        builder.Append($"Server={Server};Database={Database};");
        builder.Append($"Connect Timeout={ConnectionTimeoutSeconds};");

        if (TrustServerCertificate)
        {
            builder.Append("TrustServerCertificate=True;");
        }

        if (AuthType == AuthenticationType.SqlServer)
        {
            builder.Append("Integrated Security=False;");
            if (!string.IsNullOrWhiteSpace(Username))
            {
                builder.Append($"User Id={Username};");
            }

            if (!string.IsNullOrEmpty(EncryptedPassword))
            {
                // For now, we assume the encrypted password is already usable.
                builder.Append($"Password={EncryptedPassword};");
            }
        }
        else
        {
            builder.Append("Integrated Security=True;");
        }

        return builder.ToString();
    }
}

public enum AuthenticationType
{
    WindowsIntegrated,
    SqlServer,
    AzureAD,
    AzureADInteractive
}

