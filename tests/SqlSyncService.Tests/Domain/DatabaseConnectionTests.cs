using SqlSyncService.Domain.Subscriptions;
using Xunit;

namespace SqlSyncService.Tests.Domain;

public class DatabaseConnectionTests
{
    [Fact]
    public void WindowsIntegrated_Auth_Uses_Integrated_Security_And_Omits_Credentials()
    {
        // Arrange
        var connection = new DatabaseConnection
        {
            Server = "localhost",
            Database = "TestDb",
            AuthType = AuthenticationType.WindowsIntegrated,
            Username = "ignoredUser",
            EncryptedPassword = "ignoredPassword",
            TrustServerCertificate = true,
            ConnectionTimeoutSeconds = 15
        };

        // Act
        var connectionString = connection.ConnectionString;

        // Assert
        Assert.Contains("Server=localhost;", connectionString);
        Assert.Contains("Database=TestDb;", connectionString);
        Assert.Contains("Connect Timeout=15;", connectionString);
        Assert.Contains("Integrated Security=True;", connectionString);
        Assert.Contains("TrustServerCertificate=True;", connectionString);
        Assert.DoesNotContain("User Id=", connectionString);
        Assert.DoesNotContain("Password=", connectionString);
    }

    [Fact]
    public void SqlServer_Auth_Includes_Credentials_And_Disables_Integrated_Security()
    {
        // Arrange
        var connection = new DatabaseConnection
        {
            Server = "db.example",
            Database = "TestDb",
            AuthType = AuthenticationType.SqlServer,
            Username = "sa",
            EncryptedPassword = "secret",
            TrustServerCertificate = false,
            ConnectionTimeoutSeconds = 30
        };

        // Act
        var connectionString = connection.ConnectionString;

        // Assert
        Assert.Contains("Server=db.example;", connectionString);
        Assert.Contains("Database=TestDb;", connectionString);
        Assert.Contains("Connect Timeout=30;", connectionString);
        Assert.Contains("Integrated Security=False;", connectionString);
        Assert.Contains("User Id=sa;", connectionString);
        Assert.Contains("Password=secret;", connectionString);
        Assert.DoesNotContain("TrustServerCertificate=True;", connectionString);
    }
}

