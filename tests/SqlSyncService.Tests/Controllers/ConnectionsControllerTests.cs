using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SqlSyncService.Contracts;
using SqlSyncService.Contracts.Common;
using SqlSyncService.Contracts.Connections;
using SqlSyncService.Services;

namespace SqlSyncService.Tests.Controllers;

public class ConnectionsControllerTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ConnectionsControllerTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task TestConnection_Returns_200_On_Success()
    {
        // Arrange - override IDatabaseConnectionTester with a successful stub
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IDatabaseConnectionTester, SuccessfulTester>();
            });
        });

        using var client = factory.CreateClient();

        var request = new TestConnectionRequest
        {
            Server = "localhost",
            Database = "TestDb",
            AuthType = "windows"
        };

        // Act
        var response = await client.PostAsJsonAsync("/api/connections/test", request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<TestConnectionResponse>();
        Assert.NotNull(body);
        Assert.True(body!.Success);
        Assert.NotNull(body.ObjectCounts);
    }

    [Fact]
    public async Task TestConnection_Returns_422_On_Connection_Failure()
    {
        // Arrange - override IDatabaseConnectionTester with a failing stub
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IDatabaseConnectionTester, FailingTester>();
            });
        });

        using var client = factory.CreateClient();

        var request = new TestConnectionRequest
        {
            Server = "localhost",
            Database = "TestDb",
            AuthType = "windows"
        };

        // Act
        var response = await client.PostAsJsonAsync("/api/connections/test", request);

        // Assert
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<TestConnectionResponse>();
        Assert.NotNull(body);
        Assert.False(body!.Success);
        Assert.NotNull(body.Error);
        Assert.Equal(ErrorCodes.ConnectionFailed, body.Error!.Code);
    }

    [Fact]
    public async Task TestConnection_Returns_Validation_Error_For_Invalid_Model()
    {
        // Arrange
        using var client = _factory.CreateClient();

        // Act - send an empty object so required properties are missing
        var response = await client.PostAsJsonAsync("/api/connections/test", new { });

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var errorResponse = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.NotNull(errorResponse);
        Assert.Equal(ErrorCodes.ValidationError, errorResponse!.Error.Code);
    }

    [Fact]
    public async Task TestConnection_Returns_Validation_Error_When_Sql_Auth_Without_Credentials()
    {
        // Arrange
        using var client = _factory.CreateClient();

        var request = new TestConnectionRequest
        {
            Server = "localhost",
            Database = "TestDb",
            AuthType = "sql",
            Username = string.Empty,
            Password = string.Empty
        };

        // Act
        var response = await client.PostAsJsonAsync("/api/connections/test", request);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var errorResponse = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.NotNull(errorResponse);
        Assert.Equal(ErrorCodes.ValidationError, errorResponse!.Error.Code);
        Assert.Equal("username", errorResponse.Error.Field);
    }

    private sealed class SuccessfulTester : IDatabaseConnectionTester
    {
        public Task<TestConnectionResponse> TestConnectionAsync(TestConnectionRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new TestConnectionResponse
            {
                Success = true,
                ServerVersion = "15.0.2000.5",
                ServerEdition = "Developer Edition",
                DatabaseExists = true,
                ObjectCounts = new ObjectCounts
                {
                    Tables = 1,
                    Views = 0,
                    StoredProcedures = 0,
                    Functions = 0
                }
            });
        }
    }

    private sealed class FailingTester : IDatabaseConnectionTester
    {
        public Task<TestConnectionResponse> TestConnectionAsync(TestConnectionRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new TestConnectionResponse
            {
                Success = false,
                Error = new ErrorDetail
                {
                    Code = ErrorCodes.ConnectionFailed,
                    Message = "Connection failed."
                }
            });
        }
    }
}
