using System.Diagnostics;
using System.Linq;
using Microsoft.Extensions.Options;
using SqlSyncService.Configuration;
using SqlSyncService.DacFx;
using SqlSyncService.Domain.Caching;
using SqlSyncService.Domain.Comparisons;
using SqlSyncService.Domain.Subscriptions;
using SqlSyncService.Persistence;

namespace SqlSyncService.Services;

public interface IComparisonOrchestrator
{
    Task<ComparisonResult> RunComparisonAsync(Guid subscriptionId, bool fullComparison, CancellationToken cancellationToken = default);
}

public sealed class ComparisonOrchestrator : IComparisonOrchestrator
{
    private static SemaphoreSlim? _semaphore;
    private static readonly object _lock = new();

    private readonly ISubscriptionRepository _subscriptionRepository;
    private readonly ISchemaSnapshotRepository _schemaSnapshotRepository;
    private readonly IComparisonHistoryRepository _comparisonHistoryRepository;
    private readonly IDatabaseModelBuilder _databaseModelBuilder;
    private readonly IFileModelBuilder _fileModelBuilder;
    private readonly ISchemaComparer _schemaComparer;

    public ComparisonOrchestrator(
        ISubscriptionRepository subscriptionRepository,
        ISchemaSnapshotRepository schemaSnapshotRepository,
        IComparisonHistoryRepository comparisonHistoryRepository,
        IDatabaseModelBuilder databaseModelBuilder,
        IFileModelBuilder fileModelBuilder,
        ISchemaComparer schemaComparer,
        IOptions<ServiceConfiguration> options)
    {
        _subscriptionRepository = subscriptionRepository ?? throw new ArgumentNullException(nameof(subscriptionRepository));
        _schemaSnapshotRepository = schemaSnapshotRepository ?? throw new ArgumentNullException(nameof(schemaSnapshotRepository));
        _comparisonHistoryRepository = comparisonHistoryRepository ?? throw new ArgumentNullException(nameof(comparisonHistoryRepository));
        _databaseModelBuilder = databaseModelBuilder ?? throw new ArgumentNullException(nameof(databaseModelBuilder));
        _fileModelBuilder = fileModelBuilder ?? throw new ArgumentNullException(nameof(fileModelBuilder));
        _schemaComparer = schemaComparer ?? throw new ArgumentNullException(nameof(schemaComparer));

        if (_semaphore == null)
        {
            lock (_lock)
            {
                if (_semaphore == null)
                {
                    var max = options?.Value.Monitoring.MaxConcurrentComparisons ?? 1;
                    _semaphore = new SemaphoreSlim(max, max);
                }
            }
        }
    }

    public async Task<ComparisonResult> RunComparisonAsync(Guid subscriptionId, bool fullComparison, CancellationToken cancellationToken = default)
    {
        if (subscriptionId == Guid.Empty) throw new ArgumentException("SubscriptionId must not be empty.", nameof(subscriptionId));

        var semaphore = _semaphore ?? throw new InvalidOperationException("Comparison semaphore is not initialized.");
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var subscription = await _subscriptionRepository.GetByIdAsync(subscriptionId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Subscription '{subscriptionId}' was not found.");

            var stopwatch = Stopwatch.StartNew();

            SchemaSnapshot snapshot;
            var builtNewSnapshot = false;

            if (fullComparison)
            {
                snapshot = await _databaseModelBuilder.BuildSnapshotAsync(subscriptionId, subscription.Database, cancellationToken).ConfigureAwait(false);
                builtNewSnapshot = true;
            }
            else
            {
                snapshot = await _schemaSnapshotRepository.GetLatestForSubscriptionAsync(subscriptionId, cancellationToken).ConfigureAwait(false)
                           ?? await _databaseModelBuilder.BuildSnapshotAsync(subscriptionId, subscription.Database, cancellationToken).ConfigureAwait(false);
                builtNewSnapshot = snapshot.Id == Guid.Empty; // Defensive; current model initializes Id.
            }

            var fileCache = await _fileModelBuilder.BuildCacheAsync(subscriptionId, subscription.Project, cancellationToken).ConfigureAwait(false);

            var differences = await _schemaComparer.CompareAsync(snapshot, fileCache, subscription.Options, cancellationToken).ConfigureAwait(false);

            stopwatch.Stop();

            var status = differences.Count == 0 ? ComparisonStatus.Synchronized : ComparisonStatus.HasDifferences;

            var result = new ComparisonResult
            {
                Id = Guid.NewGuid(),
                SubscriptionId = subscriptionId,
                ComparedAt = DateTime.UtcNow,
                Duration = stopwatch.Elapsed,
                Status = status,
                Differences = differences.ToList(),
                Summary = BuildSummary(differences)
            };

            await _comparisonHistoryRepository.AddAsync(result, cancellationToken).ConfigureAwait(false);

            if (builtNewSnapshot)
            {
                await _schemaSnapshotRepository.AddAsync(snapshot, cancellationToken).ConfigureAwait(false);
            }

            subscription.LastComparedAt = result.ComparedAt;
            await _subscriptionRepository.UpdateAsync(subscription, cancellationToken).ConfigureAwait(false);

            return result;
        }
        finally
        {
            semaphore.Release();
        }
    }

    private static ComparisonSummary BuildSummary(IReadOnlyList<SchemaDifference> differences)
    {
        var summary = new ComparisonSummary
        {
            TotalDifferences = differences.Count,
            Additions = differences.Count(d => d.DifferenceType == DifferenceType.Add),
            Modifications = differences.Count(d => d.DifferenceType == DifferenceType.Modify),
            Deletions = differences.Count(d => d.DifferenceType == DifferenceType.Delete)
        };

        foreach (var group in differences.GroupBy(d => d.ObjectType.ToString()))
        {
            summary.ByObjectType[group.Key] = group.Count();
        }

        return summary;
    }
}

