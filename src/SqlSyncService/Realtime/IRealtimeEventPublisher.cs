using System;
using System.Threading;
using System.Threading.Tasks;

namespace SqlSyncService.Realtime;

public interface IRealtimeEventPublisher
{
    Task PublishToSubscriptionAsync<TEvent>(
        Guid subscriptionId,
        string eventName,
        TEvent payload,
        CancellationToken cancellationToken = default);

    Task PublishToAllSubscriptionsAsync<TEvent>(
        string eventName,
        TEvent payload,
        CancellationToken cancellationToken = default);
}

