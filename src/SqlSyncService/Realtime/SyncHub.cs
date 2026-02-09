using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace SqlSyncService.Realtime;

/// <summary>
/// Helper methods for building consistent SignalR group names used by <see cref="SyncHub"/>.
/// </summary>
public static class SyncHubGroups
{
    /// <summary>
    /// Group name for clients that want to receive events for all subscriptions.
    /// </summary>
    public const string AllSubscriptions = "subscriptions:all";

    /// <summary>
    /// Builds the group name for a specific subscription.
    /// </summary>
    public static string ForSubscription(Guid subscriptionId) => $"subscription:{subscriptionId:D}";
}

/// <summary>
/// SignalR hub used for real-time notifications about subscription activity and comparisons.
/// Clients can join per-subscription groups or a global "all subscriptions" group.
/// </summary>
public sealed class SyncHub : Hub
{
    private readonly IConnectionTracker _connectionTracker;
    private readonly ILogger<SyncHub> _logger;

    public SyncHub(IConnectionTracker connectionTracker, ILogger<SyncHub> logger)
    {
        _connectionTracker = connectionTracker ?? throw new ArgumentNullException(nameof(connectionTracker));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public override Task OnConnectedAsync()
    {
        _connectionTracker.RegisterConnection(Context.ConnectionId);
        _logger.LogInformation("Client connected: {ConnectionId}", Context.ConnectionId);

        return base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var connectionId = Context.ConnectionId;

        var subscriptions = _connectionTracker.GetSubscriptions(connectionId);
        foreach (var subscriptionId in subscriptions)
        {
            _logger.LogDebug(
                "Connection {ConnectionId} leaving subscription {SubscriptionId} due to disconnect",
                connectionId,
                subscriptionId);
        }

        if (_connectionTracker.IsInAllGroup(connectionId))
        {
            _logger.LogDebug(
                "Connection {ConnectionId} leaving all-subscriptions group due to disconnect",
                connectionId);
        }

        _connectionTracker.UnregisterConnection(connectionId);

        _logger.LogInformation(
            "Client disconnected: {ConnectionId}. Exception: {Message}",
            connectionId,
            exception?.Message);

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Adds the current connection to the SignalR group for the specified subscription.
    /// </summary>
    public async Task JoinSubscription(Guid subscriptionId)
    {
        var connectionId = Context.ConnectionId;
        var groupName = SyncHubGroups.ForSubscription(subscriptionId);

        await Groups.AddToGroupAsync(connectionId, groupName);
        _connectionTracker.JoinSubscription(connectionId, subscriptionId);

        _logger.LogInformation(
            "Connection {ConnectionId} joined subscription {SubscriptionId}",
            connectionId,
            subscriptionId);
    }

    /// <summary>
    /// Removes the current connection from the SignalR group for the specified subscription.
    /// </summary>
    public async Task LeaveSubscription(Guid subscriptionId)
    {
        var connectionId = Context.ConnectionId;
        var groupName = SyncHubGroups.ForSubscription(subscriptionId);

        await Groups.RemoveFromGroupAsync(connectionId, groupName);
        _connectionTracker.LeaveSubscription(connectionId, subscriptionId);

        _logger.LogInformation(
            "Connection {ConnectionId} left subscription {SubscriptionId}",
            connectionId,
            subscriptionId);
    }

    /// <summary>
    /// Adds the current connection to the global group that receives events for all subscriptions.
    /// </summary>
    public async Task JoinAll()
    {
        var connectionId = Context.ConnectionId;

        await Groups.AddToGroupAsync(connectionId, SyncHubGroups.AllSubscriptions);
        _connectionTracker.JoinAll(connectionId);

        _logger.LogInformation(
            "Connection {ConnectionId} joined all-subscriptions group",
            connectionId);
    }

    /// <summary>
    /// Removes the current connection from the global "all subscriptions" group.
    /// </summary>
    public async Task LeaveAll()
    {
        var connectionId = Context.ConnectionId;

        await Groups.RemoveFromGroupAsync(connectionId, SyncHubGroups.AllSubscriptions);
        _connectionTracker.LeaveAll(connectionId);

        _logger.LogInformation(
            "Connection {ConnectionId} left all-subscriptions group",
            connectionId);
    }
}

