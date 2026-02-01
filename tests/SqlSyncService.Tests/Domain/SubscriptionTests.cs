using SqlSyncService.Domain.Subscriptions;
using Xunit;

namespace SqlSyncService.Tests.Domain;

public class SubscriptionTests
{
    [Fact]
    public void New_Subscription_Is_Active_By_Default()
    {
        // Arrange & Act
        var subscription = new Subscription();

        // Assert
        Assert.Equal(SubscriptionState.Active, subscription.State);
        Assert.True(subscription.IsActive());
        Assert.False(subscription.IsPaused());
    }

    [Fact]
    public void IsActive_And_IsPaused_Reflect_State()
    {
        // Arrange
        var subscription = new Subscription
        {
            State = SubscriptionState.Paused
        };

        // Act & Assert
        Assert.False(subscription.IsActive());
        Assert.True(subscription.IsPaused());
    }
}

