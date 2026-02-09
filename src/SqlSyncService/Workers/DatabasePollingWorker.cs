using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SqlSyncService.ChangeDetection;
using SqlSyncService.Configuration;
using SqlSyncService.Domain.Changes;
using SqlSyncService.Domain.Comparisons;
using SqlSyncService.Domain.Subscriptions;
using SqlSyncService.Persistence;
using SqlSyncService.Realtime;
using SqlSyncService.Realtime.Events;

namespace SqlSyncService.Workers;

/// <summary>
/// Background worker that polls database metadata to detect schema changes.
/// Queries sys.objects.modify_date to identify when specific objects have been modified.
/// </summary>
public sealed class DatabasePollingWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DatabasePollingWorker> _logger;
    private readonly IChangeDebouncer _debouncer;
    private readonly TimeSpan _pollInterval;
    private readonly bool _enabled;

    // Track last known modify_date per object: key = "subscriptionId:schema.objectName:objectType"
    private readonly ConcurrentDictionary<string, DateTime> _lastKnownObjectModifyDates = new();

    // SQL query to get individual object modify dates
    private const string PollQuery = @"
        SELECT
            SCHEMA_NAME(schema_id) AS SchemaName,
            name AS ObjectName,
            type AS ObjectType,
            modify_date AS ModifyDate
        FROM sys.objects
        WHERE type IN ('U', 'V', 'P', 'FN', 'IF', 'TF', 'TR')";

    // Map SQL Server type codes to SqlObjectType
    private static readonly Dictionary<string, SqlObjectType> SqlTypeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "U", SqlObjectType.Table },
        { "V", SqlObjectType.View },
        { "P", SqlObjectType.StoredProcedure },
        { "FN", SqlObjectType.ScalarFunction },
        { "IF", SqlObjectType.InlineTableValuedFunction },
        { "TF", SqlObjectType.TableValuedFunction },
        { "TR", SqlObjectType.Trigger }
    };

    public DatabasePollingWorker(
        IServiceProvider serviceProvider,
        IChangeDebouncer debouncer,
        IOptions<ServiceConfiguration> config,
        ILogger<DatabasePollingWorker> logger)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _debouncer = debouncer ?? throw new ArgumentNullException(nameof(debouncer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _pollInterval = config.Value.Monitoring.DatabasePollInterval;
        _enabled = config.Value.Workers.EnableDatabasePolling;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("DatabasePollingWorker is disabled via configuration");
            return;
        }

        _logger.LogInformation("DatabasePollingWorker started with interval {Interval}", _pollInterval);

        using var timer = new PeriodicTimer(_pollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await PollAllActiveSubscriptionsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during database polling cycle");
            }
        }

        _logger.LogInformation("DatabasePollingWorker stopped");
    }

    private async Task PollAllActiveSubscriptionsAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var subscriptionRepository = scope.ServiceProvider.GetRequiredService<ISubscriptionRepository>();
        var eventPublisher = scope.ServiceProvider.GetRequiredService<IRealtimeEventPublisher>();

        var subscriptions = await subscriptionRepository.GetAllAsync(cancellationToken);

        foreach (var subscription in subscriptions)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (!subscription.IsActive() || !subscription.Options.CompareOnDatabaseChange)
            {
                continue;
            }

            try
            {
                await PollSubscriptionAsync(subscription, eventPublisher, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to poll database for subscription {SubscriptionId}",
                    subscription.Id);
            }
        }
    }

    private async Task PollSubscriptionAsync(
        Subscription subscription,
        IRealtimeEventPublisher eventPublisher,
        CancellationToken cancellationToken)
    {
        var objects = await GetDatabaseObjectsAsync(
            subscription.Database.ConnectionString, cancellationToken);

        if (objects.Count == 0)
        {
            _logger.LogDebug(
                "No objects found in database for subscription {SubscriptionId}",
                subscription.Id);
            return;
        }

        var changedObjects = new List<DatabaseObjectInfo>();
        var isFirstPoll = !HasExistingState(subscription.Id);

        foreach (var obj in objects)
        {
            var key = BuildObjectKey(subscription.Id, obj);
            var lastKnown = _lastKnownObjectModifyDates.GetOrAdd(key, DateTime.MinValue);

            if (obj.ModifyDate > lastKnown)
            {
                // Update tracking
                _lastKnownObjectModifyDates[key] = obj.ModifyDate;

                // Only record changes after initial seeding (not on first poll)
                if (lastKnown > DateTime.MinValue)
                {
                    changedObjects.Add(obj);
                }
            }
        }

        if (changedObjects.Count > 0)
        {
            _logger.LogInformation(
                "Database changes detected for subscription {SubscriptionId}: {ObjectCount} object(s) modified",
                subscription.Id, changedObjects.Count);

            foreach (var obj in changedObjects)
            {
                var objectIdentifier = $"{obj.SchemaName}.{obj.ObjectName}";

                _logger.LogDebug(
                    "Object changed: {ObjectType} {ObjectIdentifier} at {ModifyDate}",
                    obj.ObjectType, objectIdentifier, obj.ModifyDate);

                _debouncer.RecordChange(
                    subscription.Id,
                    objectIdentifier,
                    ChangeSource.Database,
                    ChangeType.Modified,
                    obj.ObjectType);

                // Emit SignalR event for each changed object per the spec
                await eventPublisher.PublishToSubscriptionAsync(
                    subscription.Id,
                    RealtimeEventNames.DatabaseChanged,
                    new DatabaseChangedEvent
                    {
                        SubscriptionId = subscription.Id,
                        Timestamp = DateTimeOffset.UtcNow,
                        ChangeType = "modified",
                        ObjectName = objectIdentifier,
                        ObjectType = obj.ObjectType.ToString().ToLowerInvariant(),
                        ModifiedBy = null // Not available from sys.objects polling
                    },
                    cancellationToken);
            }
        }
        else if (isFirstPoll)
        {
            _logger.LogDebug(
                "Initial state seeded for subscription {SubscriptionId}: {ObjectCount} objects tracked",
                subscription.Id, objects.Count);
        }
    }

    private bool HasExistingState(Guid subscriptionId)
    {
        var prefix = $"{subscriptionId}:";
        return _lastKnownObjectModifyDates.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static string BuildObjectKey(Guid subscriptionId, DatabaseObjectInfo obj)
    {
        return $"{subscriptionId}:{obj.SchemaName}.{obj.ObjectName}:{obj.ObjectType}";
    }

    private static async Task<List<DatabaseObjectInfo>> GetDatabaseObjectsAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        var results = new List<DatabaseObjectInfo>();

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(PollQuery, connection);
        command.CommandTimeout = 10; // 10-second timeout for polling query

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var schemaName = reader.IsDBNull(0) ? "dbo" : reader.GetString(0);
            var objectName = reader.GetString(1);
            var sqlType = reader.GetString(2).Trim();
            var modifyDate = reader.GetDateTime(3);

            if (SqlTypeMap.TryGetValue(sqlType, out var objectType))
            {
                results.Add(new DatabaseObjectInfo(schemaName, objectName, objectType, modifyDate));
            }
        }

        return results;
    }

    /// <summary>
    /// Clears the tracking state for a subscription. Called when a subscription is deleted.
    /// </summary>
    public void ClearSubscriptionState(Guid subscriptionId)
    {
        var prefix = $"{subscriptionId}:";
        var keysToRemove = _lastKnownObjectModifyDates.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .ToList();

        foreach (var key in keysToRemove)
        {
            _lastKnownObjectModifyDates.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Gets the list of currently tracked objects for a subscription (for testing/diagnostics).
    /// </summary>
    public IReadOnlyDictionary<string, DateTime> GetTrackedObjects(Guid subscriptionId)
    {
        var prefix = $"{subscriptionId}:";
        return _lastKnownObjectModifyDates
            .Where(kvp => kvp.Key.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    /// <summary>
    /// Represents a database object with its modify date.
    /// </summary>
    private record DatabaseObjectInfo(
        string SchemaName,
        string ObjectName,
        SqlObjectType ObjectType,
        DateTime ModifyDate);
}

