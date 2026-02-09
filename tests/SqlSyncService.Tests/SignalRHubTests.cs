using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using SqlSyncService.Realtime;

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

    [Fact]
    public async Task SyncHub_JoinSubscription_AddsMemberToGroup()
    {
        // Arrange
        var server = _factory.Server;
        var hubUrl = new Uri(server.BaseAddress, "/hubs/sync");
        var subscriptionId = Guid.NewGuid();

        await using var connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
            })
            .Build();

        await connection.StartAsync();

        // Act - should not throw
        await connection.InvokeAsync("JoinSubscription", subscriptionId);

        // Assert - connection is still active
        Assert.Equal(HubConnectionState.Connected, connection.State);

        await connection.StopAsync();
    }

    [Fact]
    public async Task SyncHub_LeaveSubscription_RemovesMemberFromGroup()
    {
        // Arrange
        var server = _factory.Server;
        var hubUrl = new Uri(server.BaseAddress, "/hubs/sync");
        var subscriptionId = Guid.NewGuid();

        await using var connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
            })
            .Build();

        await connection.StartAsync();
        await connection.InvokeAsync("JoinSubscription", subscriptionId);

        // Act - should not throw
        await connection.InvokeAsync("LeaveSubscription", subscriptionId);

        // Assert - connection is still active
        Assert.Equal(HubConnectionState.Connected, connection.State);

        await connection.StopAsync();
    }

    [Fact]
    public async Task SyncHub_JoinAll_AddsMemberToAllGroup()
    {
        // Arrange
        var server = _factory.Server;
        var hubUrl = new Uri(server.BaseAddress, "/hubs/sync");

        await using var connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
            })
            .Build();

        await connection.StartAsync();

        // Act - should not throw
        await connection.InvokeAsync("JoinAll");

        // Assert - connection is still active
        Assert.Equal(HubConnectionState.Connected, connection.State);

        await connection.StopAsync();
    }

    [Fact]
    public async Task SyncHub_LeaveAll_RemovesMemberFromAllGroup()
    {
        // Arrange
        var server = _factory.Server;
        var hubUrl = new Uri(server.BaseAddress, "/hubs/sync");

        await using var connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
            })
            .Build();

        await connection.StartAsync();
        await connection.InvokeAsync("JoinAll");

        // Act - should not throw
        await connection.InvokeAsync("LeaveAll");

        // Assert - connection is still active
        Assert.Equal(HubConnectionState.Connected, connection.State);

        await connection.StopAsync();
    }

    [Fact]
    public async Task SyncHub_MultipleClients_CanJoinSameSubscription()
    {
        // Arrange
        var server = _factory.Server;
        var hubUrl = new Uri(server.BaseAddress, "/hubs/sync");
        var subscriptionId = Guid.NewGuid();

        await using var connection1 = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
            })
            .Build();

        await using var connection2 = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
            })
            .Build();

        await connection1.StartAsync();
        await connection2.StartAsync();

        // Act - both clients join the same subscription
        await connection1.InvokeAsync("JoinSubscription", subscriptionId);
        await connection2.InvokeAsync("JoinSubscription", subscriptionId);

        // Assert - both connections are still active
        Assert.Equal(HubConnectionState.Connected, connection1.State);
        Assert.Equal(HubConnectionState.Connected, connection2.State);

        await connection1.StopAsync();
        await connection2.StopAsync();
    }

    [Fact]
    public async Task SyncHub_Client_CanJoinMultipleSubscriptions()
    {
        // Arrange
        var server = _factory.Server;
        var hubUrl = new Uri(server.BaseAddress, "/hubs/sync");
        var subscriptionId1 = Guid.NewGuid();
        var subscriptionId2 = Guid.NewGuid();

        await using var connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
            })
            .Build();

        await connection.StartAsync();

        // Act - join multiple subscriptions
        await connection.InvokeAsync("JoinSubscription", subscriptionId1);
        await connection.InvokeAsync("JoinSubscription", subscriptionId2);

        // Assert - connection is still active
        Assert.Equal(HubConnectionState.Connected, connection.State);

        await connection.StopAsync();
    }
}

/// <summary>
/// Unit tests for InMemoryConnectionTracker
/// </summary>
public class InMemoryConnectionTrackerTests
{
    [Fact]
    public void RegisterConnection_AddsNewConnection()
    {
        // Arrange
        var tracker = new InMemoryConnectionTracker();
        var connectionId = "connection-1";

        // Act
        tracker.RegisterConnection(connectionId);

        // Assert
        Assert.Equal(1, tracker.GetConnectionCount());
    }

    [Fact]
    public void UnregisterConnection_RemovesConnection()
    {
        // Arrange
        var tracker = new InMemoryConnectionTracker();
        var connectionId = "connection-1";
        tracker.RegisterConnection(connectionId);

        // Act
        tracker.UnregisterConnection(connectionId);

        // Assert
        Assert.Equal(0, tracker.GetConnectionCount());
    }

    [Fact]
    public void JoinSubscription_TracksSubscription()
    {
        // Arrange
        var tracker = new InMemoryConnectionTracker();
        var connectionId = "connection-1";
        var subscriptionId = Guid.NewGuid();
        tracker.RegisterConnection(connectionId);

        // Act
        tracker.JoinSubscription(connectionId, subscriptionId);

        // Assert
        var subscriptions = tracker.GetSubscriptions(connectionId);
        Assert.Contains(subscriptionId, subscriptions);
        Assert.Equal(1, tracker.GetConnectionCountForSubscription(subscriptionId));
    }

    [Fact]
    public void LeaveSubscription_RemovesSubscription()
    {
        // Arrange
        var tracker = new InMemoryConnectionTracker();
        var connectionId = "connection-1";
        var subscriptionId = Guid.NewGuid();
        tracker.RegisterConnection(connectionId);
        tracker.JoinSubscription(connectionId, subscriptionId);

        // Act
        tracker.LeaveSubscription(connectionId, subscriptionId);

        // Assert
        var subscriptions = tracker.GetSubscriptions(connectionId);
        Assert.DoesNotContain(subscriptionId, subscriptions);
        Assert.Equal(0, tracker.GetConnectionCountForSubscription(subscriptionId));
    }

    [Fact]
    public void JoinAll_SetsAllGroupMembership()
    {
        // Arrange
        var tracker = new InMemoryConnectionTracker();
        var connectionId = "connection-1";
        tracker.RegisterConnection(connectionId);

        // Act
        tracker.JoinAll(connectionId);

        // Assert
        Assert.True(tracker.IsInAllGroup(connectionId));
    }

    [Fact]
    public void LeaveAll_ClearsAllGroupMembership()
    {
        // Arrange
        var tracker = new InMemoryConnectionTracker();
        var connectionId = "connection-1";
        tracker.RegisterConnection(connectionId);
        tracker.JoinAll(connectionId);

        // Act
        tracker.LeaveAll(connectionId);

        // Assert
        Assert.False(tracker.IsInAllGroup(connectionId));
    }

    [Fact]
    public void GetSubscriptions_ReturnsEmpty_ForUnknownConnection()
    {
        // Arrange
        var tracker = new InMemoryConnectionTracker();

        // Act
        var subscriptions = tracker.GetSubscriptions("unknown");

        // Assert
        Assert.Empty(subscriptions);
    }

    [Fact]
    public void IsInAllGroup_ReturnsFalse_ForUnknownConnection()
    {
        // Arrange
        var tracker = new InMemoryConnectionTracker();

        // Act & Assert
        Assert.False(tracker.IsInAllGroup("unknown"));
    }

    [Fact]
    public void JoinSubscription_WithNullConnectionId_DoesNotThrow()
    {
        // Arrange
        var tracker = new InMemoryConnectionTracker();
        var subscriptionId = Guid.NewGuid();

        // Act & Assert - should not throw
        tracker.JoinSubscription(null!, subscriptionId);
        tracker.JoinSubscription("", subscriptionId);
        tracker.JoinSubscription("   ", subscriptionId);
    }

    [Fact]
    public void JoinSubscription_WithEmptyGuid_DoesNotThrow()
    {
        // Arrange
        var tracker = new InMemoryConnectionTracker();
        var connectionId = "connection-1";
        tracker.RegisterConnection(connectionId);

        // Act
        tracker.JoinSubscription(connectionId, Guid.Empty);

        // Assert - should not add empty guid
        var subscriptions = tracker.GetSubscriptions(connectionId);
        Assert.Empty(subscriptions);
    }

    [Fact]
    public void MultipleConnections_CanJoinSameSubscription()
    {
        // Arrange
        var tracker = new InMemoryConnectionTracker();
        var subscriptionId = Guid.NewGuid();

        // Act
        tracker.RegisterConnection("conn-1");
        tracker.RegisterConnection("conn-2");
        tracker.RegisterConnection("conn-3");
        tracker.JoinSubscription("conn-1", subscriptionId);
        tracker.JoinSubscription("conn-2", subscriptionId);
        tracker.JoinSubscription("conn-3", subscriptionId);

        // Assert
        Assert.Equal(3, tracker.GetConnectionCountForSubscription(subscriptionId));
    }

    [Fact]
    public void SingleConnection_CanJoinMultipleSubscriptions()
    {
        // Arrange
        var tracker = new InMemoryConnectionTracker();
        var connectionId = "connection-1";
        var sub1 = Guid.NewGuid();
        var sub2 = Guid.NewGuid();
        var sub3 = Guid.NewGuid();

        tracker.RegisterConnection(connectionId);

        // Act
        tracker.JoinSubscription(connectionId, sub1);
        tracker.JoinSubscription(connectionId, sub2);
        tracker.JoinSubscription(connectionId, sub3);

        // Assert
        var subscriptions = tracker.GetSubscriptions(connectionId);
        Assert.Equal(3, subscriptions.Count);
        Assert.Contains(sub1, subscriptions);
        Assert.Contains(sub2, subscriptions);
        Assert.Contains(sub3, subscriptions);
    }

    [Fact]
    public void UnregisterConnection_CleansUpAllMemberships()
    {
        // Arrange
        var tracker = new InMemoryConnectionTracker();
        var connectionId = "connection-1";
        var subscriptionId = Guid.NewGuid();

        tracker.RegisterConnection(connectionId);
        tracker.JoinSubscription(connectionId, subscriptionId);
        tracker.JoinAll(connectionId);

        // Act
        tracker.UnregisterConnection(connectionId);

        // Assert
        Assert.Equal(0, tracker.GetConnectionCount());
        Assert.Equal(0, tracker.GetConnectionCountForSubscription(subscriptionId));
        Assert.Empty(tracker.GetSubscriptions(connectionId));
        Assert.False(tracker.IsInAllGroup(connectionId));
    }
}

