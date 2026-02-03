using SqlSyncService.Domain.Changes;

namespace SqlSyncService.ChangeDetection;

/// <summary>
/// Background service that coordinates the change detection pipeline by wiring
/// the ChangeDebouncer's BatchReady event to the ChangeProcessor.
/// </summary>
public sealed class ChangeDetectionCoordinator : BackgroundService
{
    private readonly IChangeDebouncer _debouncer;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ChangeDetectionCoordinator> _logger;

    public ChangeDetectionCoordinator(
        IChangeDebouncer debouncer,
        IServiceProvider serviceProvider,
        ILogger<ChangeDetectionCoordinator> logger)
    {
        _debouncer = debouncer ?? throw new ArgumentNullException(nameof(debouncer));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _debouncer.BatchReady += OnBatchReady;
        _logger.LogInformation("ChangeDetectionCoordinator started - listening for batches");

        // Keep running until cancelled
        return Task.CompletedTask;
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _debouncer.BatchReady -= OnBatchReady;
        _logger.LogInformation("ChangeDetectionCoordinator stopped");
        return base.StopAsync(cancellationToken);
    }

    private async void OnBatchReady(object? sender, PendingChangeBatch batch)
    {
        try
        {
            _logger.LogDebug(
                "Received batch with {ChangeCount} changes for subscription {SubscriptionId}",
                batch.Changes.Count, batch.SubscriptionId);

            // Create a scope to resolve scoped services
            using var scope = _serviceProvider.CreateScope();
            var processor = scope.ServiceProvider.GetRequiredService<IChangeProcessor>();

            await processor.ProcessBatchAsync(batch, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error processing batch for subscription {SubscriptionId}",
                batch.SubscriptionId);
        }
    }
}

