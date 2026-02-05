using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using SqlSyncService.Configuration;
using SqlSyncService.Domain.Changes;
using SqlSyncService.Domain.Comparisons;

namespace SqlSyncService.ChangeDetection;

/// <summary>
/// Aggregates rapid changes into batches within a configurable debounce window.
/// Thread-safe implementation using ConcurrentDictionary for per-subscription state.
/// </summary>
public sealed class ChangeDebouncer : IChangeDebouncer, IDisposable
{
    private readonly TimeSpan _debounceWindow;
    private readonly ConcurrentDictionary<Guid, DebounceState> _subscriptionStates = new();
    private readonly ILogger<ChangeDebouncer> _logger;
    private bool _disposed;

    public event EventHandler<PendingChangeBatch>? BatchReady;

    public ChangeDebouncer(IOptions<ServiceConfiguration> config, ILogger<ChangeDebouncer> logger)
    {
        _debounceWindow = config.Value.Monitoring.FileSystemDebounce;
        _logger = logger;
    }

    /// <summary>
    /// Internal constructor for testing with explicit debounce window.
    /// </summary>
    internal ChangeDebouncer(TimeSpan debounceWindow, ILogger<ChangeDebouncer> logger)
    {
        _debounceWindow = debounceWindow;
        _logger = logger;
    }

    public void RecordChange(Guid subscriptionId, string objectIdentifier, ChangeSource source, ChangeType type)
    {
        RecordChangeInternal(subscriptionId, objectIdentifier, source, type, objectType: null);
    }

    public void RecordChange(Guid subscriptionId, string objectIdentifier, ChangeSource source, ChangeType type, SqlObjectType objectType)
    {
        RecordChangeInternal(subscriptionId, objectIdentifier, source, type, objectType);
    }

    private void RecordChangeInternal(Guid subscriptionId, string objectIdentifier, ChangeSource source, ChangeType type, SqlObjectType? objectType)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ChangeDebouncer));
        }

        var state = _subscriptionStates.GetOrAdd(subscriptionId, _ => new DebounceState());

        state.AddChange(subscriptionId, objectIdentifier, source, type, objectType);
        state.ResetTimer(_debounceWindow, () => FlushBatch(subscriptionId));

        _logger.LogDebug(
            "Recorded change for subscription {SubscriptionId}: {ObjectIdentifier} ({Source}/{Type}/{ObjectType})",
            subscriptionId, objectIdentifier, source, type, objectType?.ToString() ?? "Unknown");
    }

    private void FlushBatch(Guid subscriptionId)
    {
        if (!_subscriptionStates.TryGetValue(subscriptionId, out var state))
        {
            return;
        }

        var batch = state.FlushChanges(subscriptionId);
        if (batch.Changes.Count == 0)
        {
            return;
        }

        _logger.LogInformation(
            "Flushing batch for subscription {SubscriptionId}: {ChangeCount} changes",
            subscriptionId, batch.Changes.Count);

        BatchReady?.Invoke(this, batch);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var state in _subscriptionStates.Values)
        {
            state.Dispose();
        }

        _subscriptionStates.Clear();
    }

    /// <summary>
    /// Internal state for tracking pending changes per subscription.
    /// </summary>
    private sealed class DebounceState : IDisposable
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, DetectedChange> _pendingChanges = new();
        private Timer? _timer;
        private DateTime _batchStartedAt;
        private bool _disposed;

        public void AddChange(Guid subscriptionId, string objectIdentifier, ChangeSource source, ChangeType type, SqlObjectType? objectType)
        {
            lock (_lock)
            {
                if (_pendingChanges.Count == 0)
                {
                    _batchStartedAt = DateTime.UtcNow;
                }

                // Deduplication: last change type wins
                _pendingChanges[objectIdentifier] = new DetectedChange
                {
                    Id = Guid.NewGuid(),
                    SubscriptionId = subscriptionId,
                    ObjectIdentifier = objectIdentifier,
                    Source = source,
                    Type = type,
                    DetectedAt = DateTime.UtcNow,
                    IsProcessed = false,
                    ObjectType = objectType
                };
            }
        }

        public void ResetTimer(TimeSpan debounceWindow, Action onElapsed)
        {
            lock (_lock)
            {
                _timer?.Dispose();
                _timer = new Timer(_ => onElapsed(), null, debounceWindow, Timeout.InfiniteTimeSpan);
            }
        }

        public PendingChangeBatch FlushChanges(Guid subscriptionId)
        {
            lock (_lock)
            {
                var batch = new PendingChangeBatch
                {
                    SubscriptionId = subscriptionId,
                    Changes = _pendingChanges.Values.ToList(),
                    BatchStartedAt = _batchStartedAt,
                    BatchCompletedAt = DateTime.UtcNow
                };

                _pendingChanges.Clear();
                _timer?.Dispose();
                _timer = null;

                return batch;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
        }
    }
}

