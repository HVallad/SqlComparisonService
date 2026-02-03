using SqlSyncService.Contracts;
using SqlSyncService.Contracts.Common;
using SqlSyncService.Contracts.Connections;
using SqlSyncService.Services;

namespace SqlSyncService.Tests.Services;

public class DatabaseConnectionTesterTests
{
    private readonly DatabaseConnectionTester _tester = new();

    [Theory]
    [InlineData("invalid")]
    [InlineData("kerberos")]
    [InlineData("certificate")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task TestConnectionAsync_Returns_Error_For_Unsupported_Auth_Type(string authType)
    {
        // Arrange
        var request = new TestConnectionRequest
        {
            Server = "localhost",
            Database = "TestDb",
            AuthType = authType
        };

        // Act
        var result = await _tester.TestConnectionAsync(request);

        // Assert
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal(ErrorCodes.ConnectionFailed, result.Error!.Code);
        Assert.Contains("Unsupported authentication type", result.Error.Message);
        Assert.Contains("windows", result.Error.Details);
        Assert.Contains("sql", result.Error.Details);
        Assert.Contains("azuread", result.Error.Details);
    }

    [Theory]
    [InlineData("azuread")]
    [InlineData("AzureAD")]
    [InlineData("AZUREAD")]
    [InlineData("azuread-interactive")]
    [InlineData("AzureAD-Interactive")]
    public async Task TestConnectionAsync_Returns_Not_Implemented_For_AzureAD_Auth_Types(string authType)
    {
        // Arrange
        var request = new TestConnectionRequest
        {
            Server = "localhost",
            Database = "TestDb",
            AuthType = authType
        };

        // Act
        var result = await _tester.TestConnectionAsync(request);

        // Assert
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal(ErrorCodes.ConnectionFailed, result.Error!.Code);
        Assert.Contains("not yet implemented", result.Error.Message);
        Assert.Contains("Azure AD", result.Error.Details);
    }

    [Theory]
    [InlineData("windows")]
    [InlineData("Windows")]
    [InlineData("WINDOWS")]
    [InlineData("sql")]
    [InlineData("SQL")]
    public async Task TestConnectionAsync_Returns_Connection_Error_For_Invalid_Server(string authType)
    {
        // Arrange - use an invalid server name that will fail connection
        var request = new TestConnectionRequest
        {
            Server = "nonexistent-server-that-does-not-exist.local",
            Database = "TestDb",
            AuthType = authType,
            ConnectionTimeoutSeconds = 1 // Short timeout to fail fast
        };

        if (authType.Equals("sql", StringComparison.OrdinalIgnoreCase))
        {
            request.Username = "testuser";
            request.Password = "testpass";
        }

        // Act
        var result = await _tester.TestConnectionAsync(request);

        // Assert
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal(ErrorCodes.ConnectionFailed, result.Error!.Code);
        Assert.Contains("Cannot connect to server", result.Error.Message);
    }

    [Fact]
    public async Task TestConnectionAsync_Uses_Minimum_Timeout_Of_1_Second()
    {
        // Arrange - use 0 timeout, which should be adjusted to 1
        var request = new TestConnectionRequest
        {
            Server = "nonexistent-server.local",
            Database = "TestDb",
            AuthType = "windows",
            ConnectionTimeoutSeconds = 0
        };

        // Act - this should not hang indefinitely because timeout is set to at least 1 second
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await _tester.TestConnectionAsync(request, cts.Token);

        // Assert
        Assert.False(result.Success);
        // If we got here before CancellationToken fired, the minimum timeout was applied
    }
}

