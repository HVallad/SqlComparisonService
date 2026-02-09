using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace SqlSyncService.Realtime;

/// <summary>
/// Thread-safe in-memory implementation of <see cref="IConnectionTracker"/>.
/// Suitable for single-instance deployments. For multi-node scenarios this can
/// be replaced with a distributed implementation (e.g. Redis-backed).
/// </summary>
public sealed class InMemoryConnectionTracker : IConnectionTracker
{
    private sealed class ConnectionState
    {
        public bool JoinedAll;
        public HashSet<Guid> Subscriptions { get; } = new();
    }

    private readonly ConcurrentDictionary<string, ConnectionState> _connections = new();

    public void RegisterConnection(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            return;
        }

        _connections.TryAdd(connectionId, new ConnectionState());
    }

    public void UnregisterConnection(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            return;
        }

        _connections.TryRemove(connectionId, out _);
    }

    public void JoinSubscription(string connectionId, Guid subscriptionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId) || subscriptionId == Guid.Empty)
        {
            return;
        }

        var state = _connections.GetOrAdd(connectionId, _ => new ConnectionState());

        lock (state)
        {
            state.Subscriptions.Add(subscriptionId);
        }
    }

    public void LeaveSubscription(string connectionId, Guid subscriptionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId) || subscriptionId == Guid.Empty)
        {
            return;
        }

        if (_connections.TryGetValue(connectionId, out var state))
        {
            lock (state)
            {
                state.Subscriptions.Remove(subscriptionId);
            }
        }
    }

    public void JoinAll(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            return;
        }

        var state = _connections.GetOrAdd(connectionId, _ => new ConnectionState());

        lock (state)
        {
            state.JoinedAll = true;
        }
    }

    public void LeaveAll(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            return;
        }

        if (_connections.TryGetValue(connectionId, out var state))
        {
            lock (state)
            {
                state.JoinedAll = false;
            }
        }
    }

    public IReadOnlyCollection<Guid> GetSubscriptions(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            return Array.Empty<Guid>();
        }

        if (_connections.TryGetValue(connectionId, out var state))
        {
            lock (state)
            {
                return state.Subscriptions.Count == 0
                    ? Array.Empty<Guid>()
                    : state.Subscriptions.ToArray();
            }
        }

        return Array.Empty<Guid>();
    }

    public bool IsInAllGroup(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            return false;
        }

        if (_connections.TryGetValue(connectionId, out var state))
        {
            lock (state)
            {
                return state.JoinedAll;
            }
        }

        return false;
    }

    public int GetConnectionCount() => _connections.Count;

    public int GetConnectionCountForSubscription(Guid subscriptionId)
    {
        if (subscriptionId == Guid.Empty)
        {
            return 0;
        }

        var count = 0;

        foreach (var state in _connections.Values)
        {
            lock (state)
            {
                if (state.Subscriptions.Contains(subscriptionId))
                {
                    count++;
                }
            }
        }

        return count;
    }
}
