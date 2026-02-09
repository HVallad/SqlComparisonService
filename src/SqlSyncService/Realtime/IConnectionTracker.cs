using System;
using System.Collections.Generic;

namespace SqlSyncService.Realtime;

/// <summary>
/// Tracks active SignalR connections and their subscription/group membership.
/// Used by the hub and background services for diagnostics and routing decisions.
/// </summary>
public interface IConnectionTracker
{
    /// <summary>
    /// Registers a new connection when it is established.
    /// </summary>
    void RegisterConnection(string connectionId);

    /// <summary>
    /// Removes all tracking state for a connection when it is closed.
    /// </summary>
    void UnregisterConnection(string connectionId);

    /// <summary>
    /// Records that a connection joined a specific subscription.
    /// </summary>
    void JoinSubscription(string connectionId, Guid subscriptionId);

    /// <summary>
    /// Records that a connection left a specific subscription.
    /// </summary>
    void LeaveSubscription(string connectionId, Guid subscriptionId);

    /// <summary>
    /// Records that a connection joined the global "all subscriptions" group.
    /// </summary>
    void JoinAll(string connectionId);

    /// <summary>
    /// Records that a connection left the global "all subscriptions" group.
    /// </summary>
    void LeaveAll(string connectionId);

    /// <summary>
    /// Gets the set of subscriptions a connection is currently subscribed to.
    /// Returns an empty collection if the connection is unknown or has no subscriptions.
    /// </summary>
    IReadOnlyCollection<Guid> GetSubscriptions(string connectionId);

    /// <summary>
    /// Returns true if the connection is currently part of the global "all subscriptions" group.
    /// </summary>
    bool IsInAllGroup(string connectionId);

    /// <summary>
    /// Gets the total number of active connections being tracked.
    /// </summary>
    int GetConnectionCount();

    /// <summary>
    /// Gets the number of active connections that are subscribed to a specific subscription.
    /// </summary>
    int GetConnectionCountForSubscription(Guid subscriptionId);
}
