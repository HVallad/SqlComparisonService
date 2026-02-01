using LiteDB;
using SqlSyncService.Domain.Subscriptions;
using SqlSyncService.Persistence;

namespace SqlSyncService.Tests.Persistence;

public class SubscriptionRepositoryTests
{
    private static SubscriptionRepository CreateRepository()
    {
        var database = new LiteDatabase(new MemoryStream());
        var context = new LiteDbContext(database);
        return new SubscriptionRepository(context);
    }

    [Fact]
    public async Task Add_And_GetById_Roundtrips_Subscription()
    {
        // Arrange
        var repository = CreateRepository();
        var subscription = new Subscription
        {
            Name = "Test subscription"
        };

        // Act
        await repository.AddAsync(subscription);
        var loaded = await repository.GetByIdAsync(subscription.Id);

        // Assert
        Assert.NotNull(loaded);
        Assert.Equal(subscription.Id, loaded!.Id);
        Assert.Equal("Test subscription", loaded.Name);
    }

    [Fact]
    public async Task GetAll_Returns_All_Subscriptions()
    {
        // Arrange
        var repository = CreateRepository();
        var first = new Subscription { Name = "First" };
        var second = new Subscription { Name = "Second" };

        await repository.AddAsync(first);
        await repository.AddAsync(second);

        // Act
        var all = await repository.GetAllAsync();

        // Assert
        Assert.Equal(2, all.Count);
        Assert.Contains(all, s => s.Name == "First");
        Assert.Contains(all, s => s.Name == "Second");
    }

    [Fact]
    public async Task Update_Persists_Changes()
    {
        // Arrange
        var repository = CreateRepository();
        var subscription = new Subscription { Name = "Original" };
        await repository.AddAsync(subscription);

        // Act
        subscription.Name = "Updated";
        await repository.UpdateAsync(subscription);
        var loaded = await repository.GetByIdAsync(subscription.Id);

        // Assert
        Assert.NotNull(loaded);
        Assert.Equal("Updated", loaded!.Name);
    }

    [Fact]
    public async Task Delete_Removes_Subscription()
    {
        // Arrange
        var repository = CreateRepository();
        var subscription = new Subscription { Name = "ToDelete" };
        await repository.AddAsync(subscription);

        // Act
        var removed = await repository.DeleteAsync(subscription.Id);
        var loaded = await repository.GetByIdAsync(subscription.Id);

        // Assert
        Assert.True(removed);
        Assert.Null(loaded);
    }
}

