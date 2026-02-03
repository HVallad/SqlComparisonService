using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SqlSyncService.ChangeDetection;
using SqlSyncService.Configuration;
using SqlSyncService.Domain.Changes;
using SqlSyncService.Domain.Subscriptions;
using SqlSyncService.Persistence;
using SqlSyncService.Realtime;

namespace SqlSyncService.Workers;

/// <summary>
/// Background worker that polls database metadata to detect schema changes.
/// Queries sys.objects.modify_date to identify when objects have been modified.
/// </summary>
public sealed class DatabasePollingWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DatabasePollingWorker> _logger;
    private readonly IChangeDebouncer _debouncer;
    private readonly TimeSpan _pollInterval;
    private readonly bool _enabled;

    // Track last known modify_date per subscription
    private readonly ConcurrentDictionary<Guid, DateTime> _lastKnownModifyDates = new();

    // SQL query to get the latest modify date from database objects
    private const string PollQuery = @"
        SELECT MAX(modify_date) AS LatestModifyDate
        FROM sys.objects
        WHERE type IN ('U', 'V', 'P', 'FN', 'IF', 'TF', 'TR')";

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
        var hubContext = scope.ServiceProvider.GetRequiredService<IHubContext<SyncHub>>();

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
                await PollSubscriptionAsync(subscription, hubContext, cancellationToken);
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
        IHubContext<SyncHub> hubContext,
        CancellationToken cancellationToken)
    {
        var latestModifyDate = await GetLatestModifyDateAsync(
            subscription.Database.ConnectionString, cancellationToken);

        if (latestModifyDate == null)
        {
            _logger.LogDebug(
                "No objects found in database for subscription {SubscriptionId}",
                subscription.Id);
            return;
        }

        var lastKnown = _lastKnownModifyDates.GetOrAdd(subscription.Id, DateTime.MinValue);

        if (latestModifyDate > lastKnown)
        {
            // Database has changed since last poll
            _logger.LogInformation(
                "Database change detected for subscription {SubscriptionId}: {LastKnown} -> {Latest}",
                subscription.Id, lastKnown, latestModifyDate);

            // Update tracking
            _lastKnownModifyDates[subscription.Id] = latestModifyDate.Value;

            // Only record changes after initial seeding (not on first poll)
            if (lastKnown > DateTime.MinValue)
            {
                // Record as a generic "database" change - we don't know which specific object
                _debouncer.RecordChange(
                    subscription.Id,
                    "DATABASE_SCHEMA",
                    ChangeSource.Database,
                    ChangeType.Modified);

                // Emit SignalR event
                await hubContext.Clients.All.SendAsync(
                    "DatabaseChanged",
                    new { SubscriptionId = subscription.Id, LatestModifyDate = latestModifyDate },
                    cancellationToken);
            }
        }
    }

    private static async Task<DateTime?> GetLatestModifyDateAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(PollQuery, connection);
        command.CommandTimeout = 10; // 10-second timeout for polling query

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result == DBNull.Value ? null : (DateTime?)result;
    }

    /// <summary>
    /// Clears the tracking state for a subscription. Called when a subscription is deleted.
    /// </summary>
    public void ClearSubscriptionState(Guid subscriptionId)
    {
        _lastKnownModifyDates.TryRemove(subscriptionId, out _);
    }
}

