using SqlSyncService.Contracts.Subscriptions;
using SqlSyncService.Domain.Subscriptions;

namespace SqlSyncService.Services;

public interface ISubscriptionService
{
    Task<IReadOnlyList<Subscription>> GetAllAsync(
        SubscriptionState? stateFilter = null,
        CancellationToken cancellationToken = default);

    Task<Subscription?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<Subscription> CreateAsync(
        CreateSubscriptionRequest request,
        CancellationToken cancellationToken = default);

    Task<Subscription> UpdateAsync(
        Guid id,
        UpdateSubscriptionRequest request,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(
        Guid id,
        bool deleteHistory,
        CancellationToken cancellationToken = default);

    Task<Subscription> PauseAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<Subscription> ResumeAsync(
        Guid id,
        CancellationToken cancellationToken = default);
}

