using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace SqlSyncService.Realtime;

public sealed class RealtimeEventPublisher : IRealtimeEventPublisher
{
    private readonly IHubContext<SyncHub> _hubContext;
    private readonly ILogger<RealtimeEventPublisher> _logger;

    public RealtimeEventPublisher(
        IHubContext<SyncHub> hubContext,
        ILogger<RealtimeEventPublisher> logger)
    {
        _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task PublishToSubscriptionAsync<TEvent>(
        Guid subscriptionId,
        string eventName,
        TEvent payload,
        CancellationToken cancellationToken = default)
    {
        if (subscriptionId == Guid.Empty)
        {
            throw new ArgumentException("SubscriptionId must not be empty.", nameof(subscriptionId));
        }

        if (string.IsNullOrWhiteSpace(eventName))
        {
            throw new ArgumentException("Event name must be provided.", nameof(eventName));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var groupName = SyncHubGroups.ForSubscription(subscriptionId);

        try
        {
            await _hubContext
                .Clients
                .Group(groupName)
                .SendAsync(eventName, payload!, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogDebug(
                "Published realtime event {EventName} to group {GroupName} for subscription {SubscriptionId}",
                eventName,
                groupName,
                subscriptionId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error publishing realtime event {EventName} to subscription {SubscriptionId}",
                eventName,
                subscriptionId);
        }
    }

    public async Task PublishToAllSubscriptionsAsync<TEvent>(
        string eventName,
        TEvent payload,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(eventName))
        {
            throw new ArgumentException("Event name must be provided.", nameof(eventName));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        const string groupName = SyncHubGroups.AllSubscriptions;

        try
        {
            await _hubContext
                .Clients
                .Group(groupName)
                .SendAsync(eventName, payload!, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogDebug(
                "Published realtime event {EventName} to group {GroupName}",
                eventName,
                groupName);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error publishing realtime event {EventName} to all subscriptions",
                eventName);
        }
    }
}

