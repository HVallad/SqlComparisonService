using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;

namespace SqlSyncService.Tests;

public class SignalRHubTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public SignalRHubTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task SyncHub_Allows_Client_Connection()
    {
        // Arrange
        var server = _factory.Server;
        var hubUrl = new Uri(server.BaseAddress, "/hubs/sync");

        await using var connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                // Use the in-memory TestServer HTTP handler
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
            })
            .WithAutomaticReconnect()
            .Build();

        // Act
        await connection.StartAsync();

        // Assert
        Assert.Equal(HubConnectionState.Connected, connection.State);

        await connection.StopAsync();
    }
}

