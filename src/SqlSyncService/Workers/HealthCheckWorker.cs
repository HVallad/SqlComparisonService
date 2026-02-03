using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using SqlSyncService.Configuration;
using SqlSyncService.Contracts.Connections;
using SqlSyncService.Contracts.Folders;
using SqlSyncService.Domain.Subscriptions;
using SqlSyncService.Persistence;
using SqlSyncService.Realtime;
using SqlSyncService.Services;

namespace SqlSyncService.Workers;

/// <summary>
/// Background worker that periodically checks the health of all active subscriptions.
/// Verifies database connectivity, folder accessibility, and SQL file presence.
/// </summary>
public sealed class HealthCheckWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<HealthCheckWorker> _logger;
    private readonly TimeSpan _healthCheckInterval;
    private readonly bool _enabled;

    public HealthCheckWorker(
        IServiceProvider serviceProvider,
        IOptions<ServiceConfiguration> config,
        ILogger<HealthCheckWorker> logger)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _healthCheckInterval = config.Value.Monitoring.HealthCheckInterval;
        _enabled = config.Value.Workers.EnableHealthChecks;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("HealthCheckWorker is disabled via configuration");
            return;
        }

        _logger.LogInformation("HealthCheckWorker started with interval {Interval}", _healthCheckInterval);

        using var timer = new PeriodicTimer(_healthCheckInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await CheckAllSubscriptionsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during health check cycle");
            }
        }

        _logger.LogInformation("HealthCheckWorker stopped");
    }

    private async Task CheckAllSubscriptionsAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var subscriptionRepository = scope.ServiceProvider.GetRequiredService<ISubscriptionRepository>();
        var connectionTester = scope.ServiceProvider.GetRequiredService<IDatabaseConnectionTester>();
        var folderValidator = scope.ServiceProvider.GetRequiredService<IFolderValidator>();
        var hubContext = scope.ServiceProvider.GetRequiredService<IHubContext<SyncHub>>();

        var subscriptions = await subscriptionRepository.GetAllAsync(cancellationToken);

        foreach (var subscription in subscriptions)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var previousStatus = subscription.Health.OverallStatus;
            await CheckSubscriptionHealthAsync(subscription, connectionTester, folderValidator, cancellationToken);

            // Update subscription in repository
            await subscriptionRepository.UpdateAsync(subscription, cancellationToken);

            // Emit SignalR event if status changed
            if (subscription.Health.OverallStatus != previousStatus)
            {
                await hubContext.Clients.All.SendAsync(
                    "SubscriptionHealthChanged",
                    new
                    {
                        SubscriptionId = subscription.Id,
                        PreviousStatus = previousStatus.ToString(),
                        CurrentStatus = subscription.Health.OverallStatus.ToString(),
                        subscription.Health.DatabaseConnectable,
                        subscription.Health.FolderAccessible,
                        subscription.Health.SqlFilesPresent,
                        subscription.Health.LastError
                    },
                    cancellationToken);

                _logger.LogInformation(
                    "Subscription {SubscriptionId} health changed from {PreviousStatus} to {CurrentStatus}",
                    subscription.Id, previousStatus, subscription.Health.OverallStatus);
            }
        }
    }

    private async Task CheckSubscriptionHealthAsync(
        Subscription subscription,
        IDatabaseConnectionTester connectionTester,
        IFolderValidator folderValidator,
        CancellationToken cancellationToken)
    {
        subscription.Health.LastCheckedAt = DateTime.UtcNow;
        subscription.Health.LastError = null;

        try
        {
            // Check database connectivity with 5-second timeout
            var dbRequest = new TestConnectionRequest
            {
                Server = subscription.Database.Server,
                Database = subscription.Database.Database,
                AuthType = subscription.Database.AuthType.ToString(),
                Username = subscription.Database.Username,
                Password = subscription.Database.EncryptedPassword,
                TrustServerCertificate = subscription.Database.TrustServerCertificate,
                ConnectionTimeoutSeconds = Math.Min(subscription.Database.ConnectionTimeoutSeconds, 5)
            };
            var dbResult = await connectionTester.TestConnectionAsync(dbRequest, cancellationToken);
            subscription.Health.DatabaseConnectable = dbResult.Success;
            if (!dbResult.Success)
            {
                subscription.Health.LastError = dbResult.Error?.Message;
            }
        }
        catch (Exception ex)
        {
            subscription.Health.DatabaseConnectable = false;
            subscription.Health.LastError = $"Database check failed: {ex.Message}";
        }

        // Check folder accessibility
        try
        {
            var folderRequest = new ValidateFolderRequest
            {
                Path = subscription.Project.RootPath,
                IncludePatterns = subscription.Project.IncludePatterns,
                ExcludePatterns = subscription.Project.ExcludePatterns
            };
            var folderResult = await folderValidator.ValidateFolderAsync(folderRequest, cancellationToken);
            subscription.Health.FolderAccessible = folderResult.Exists;
            subscription.Health.SqlFilesPresent = folderResult.SqlFileCount > 0;

            if (!folderResult.Valid && subscription.Health.LastError is null)
            {
                subscription.Health.LastError = folderResult.ParseErrors.FirstOrDefault()?.Message;
            }
        }
        catch (Exception ex)
        {
            subscription.Health.FolderAccessible = false;
            subscription.Health.SqlFilesPresent = false;
            if (subscription.Health.LastError is null)
            {
                subscription.Health.LastError = $"Folder check failed: {ex.Message}";
            }
        }

        // Determine overall status
        subscription.Health.OverallStatus = DetermineOverallStatus(subscription.Health);
    }

    private static HealthStatus DetermineOverallStatus(SubscriptionHealth health)
    {
        if (health.DatabaseConnectable && health.FolderAccessible && health.SqlFilesPresent)
        {
            return HealthStatus.Healthy;
        }

        if (!health.DatabaseConnectable || !health.FolderAccessible)
        {
            return HealthStatus.Unhealthy;
        }

        // Folder accessible but no SQL files
        return HealthStatus.Degraded;
    }
}

