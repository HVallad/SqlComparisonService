using Microsoft.Extensions.Logging;
using SqlSyncService.ChangeDetection;
using SqlSyncService.Domain.Changes;

namespace SqlSyncService.Tests.ChangeDetection;

public class ChangeDebouncerTests : IDisposable
{
    private readonly ChangeDebouncer _debouncer;
    private readonly TimeSpan _debounceWindow = TimeSpan.FromMilliseconds(100);

    public ChangeDebouncerTests()
    {
        var logger = new LoggerFactory().CreateLogger<ChangeDebouncer>();
        _debouncer = new ChangeDebouncer(_debounceWindow, logger);
    }

    public void Dispose()
    {
        _debouncer.Dispose();
    }

    [Fact]
    public async Task RecordChange_EmitsBatchAfterDebounceWindow()
    {
        // Arrange
        var subscriptionId = Guid.NewGuid();
        PendingChangeBatch? receivedBatch = null;
        _debouncer.BatchReady += (_, batch) => receivedBatch = batch;

        // Act
        _debouncer.RecordChange(subscriptionId, "dbo.Table1", ChangeSource.FileSystem, ChangeType.Modified);
        await Task.Delay(_debounceWindow + TimeSpan.FromMilliseconds(50));

        // Assert
        Assert.NotNull(receivedBatch);
        Assert.Equal(subscriptionId, receivedBatch.SubscriptionId);
        Assert.Single(receivedBatch.Changes);
        Assert.Equal("dbo.Table1", receivedBatch.Changes[0].ObjectIdentifier);
        Assert.Equal(ChangeSource.FileSystem, receivedBatch.Changes[0].Source);
        Assert.Equal(ChangeType.Modified, receivedBatch.Changes[0].Type);
    }

    [Fact]
    public async Task RecordChange_AggregatesMultipleChangesWithinWindow()
    {
        // Arrange
        var subscriptionId = Guid.NewGuid();
        PendingChangeBatch? receivedBatch = null;
        _debouncer.BatchReady += (_, batch) => receivedBatch = batch;

        // Act - Record multiple changes within debounce window
        _debouncer.RecordChange(subscriptionId, "dbo.Table1", ChangeSource.FileSystem, ChangeType.Modified);
        await Task.Delay(20);
        _debouncer.RecordChange(subscriptionId, "dbo.Table2", ChangeSource.FileSystem, ChangeType.Created);
        await Task.Delay(20);
        _debouncer.RecordChange(subscriptionId, "dbo.View1", ChangeSource.Database, ChangeType.Modified);

        await Task.Delay(_debounceWindow + TimeSpan.FromMilliseconds(50));

        // Assert - All changes should be in single batch
        Assert.NotNull(receivedBatch);
        Assert.Equal(3, receivedBatch.Changes.Count);
    }

    [Fact]
    public async Task RecordChange_DeduplicatesSameObjectChanges_LastChangeWins()
    {
        // Arrange
        var subscriptionId = Guid.NewGuid();
        PendingChangeBatch? receivedBatch = null;
        _debouncer.BatchReady += (_, batch) => receivedBatch = batch;

        // Act - Same file changed multiple times: Create -> Modify -> Delete
        _debouncer.RecordChange(subscriptionId, "dbo.Table1", ChangeSource.FileSystem, ChangeType.Created);
        await Task.Delay(10);
        _debouncer.RecordChange(subscriptionId, "dbo.Table1", ChangeSource.FileSystem, ChangeType.Modified);
        await Task.Delay(10);
        _debouncer.RecordChange(subscriptionId, "dbo.Table1", ChangeSource.FileSystem, ChangeType.Deleted);

        await Task.Delay(_debounceWindow + TimeSpan.FromMilliseconds(50));

        // Assert - Only the last change should be present (Delete)
        Assert.NotNull(receivedBatch);
        Assert.Single(receivedBatch.Changes);
        Assert.Equal(ChangeType.Deleted, receivedBatch.Changes[0].Type);
    }

    [Fact]
    public async Task RecordChange_SeparateSubscriptionsGetSeparateBatches()
    {
        // Arrange
        var subscriptionId1 = Guid.NewGuid();
        var subscriptionId2 = Guid.NewGuid();
        var receivedBatches = new List<PendingChangeBatch>();
        _debouncer.BatchReady += (_, batch) => receivedBatches.Add(batch);

        // Act
        _debouncer.RecordChange(subscriptionId1, "dbo.Table1", ChangeSource.FileSystem, ChangeType.Modified);
        _debouncer.RecordChange(subscriptionId2, "dbo.Table2", ChangeSource.FileSystem, ChangeType.Modified);

        await Task.Delay(_debounceWindow + TimeSpan.FromMilliseconds(100));

        // Assert - Each subscription should get its own batch
        Assert.Equal(2, receivedBatches.Count);
        Assert.Contains(receivedBatches, b => b.SubscriptionId == subscriptionId1);
        Assert.Contains(receivedBatches, b => b.SubscriptionId == subscriptionId2);
    }

    [Fact]
    public async Task RecordChange_DoesNotEmitEmptyBatch()
    {
        // Arrange
        var subscriptionId = Guid.NewGuid();
        PendingChangeBatch? receivedBatch = null;
        _debouncer.BatchReady += (_, batch) => receivedBatch = batch;

        // Act - Record change and wait for batch to be emitted
        _debouncer.RecordChange(subscriptionId, "dbo.Table1", ChangeSource.FileSystem, ChangeType.Modified);
        await Task.Delay(_debounceWindow + TimeSpan.FromMilliseconds(50));

        var firstBatch = receivedBatch;
        receivedBatch = null;

        // Wait again - should not receive another batch
        await Task.Delay(_debounceWindow + TimeSpan.FromMilliseconds(50));

        // Assert
        Assert.NotNull(firstBatch);
        Assert.Single(firstBatch.Changes);
        Assert.Null(receivedBatch);
    }

    [Fact]
    public void RecordChange_ThrowsObjectDisposedException_WhenDisposed()
    {
        // Arrange
        var subscriptionId = Guid.NewGuid();
        _debouncer.Dispose();

        // Act & Assert
        Assert.Throws<ObjectDisposedException>(() =>
            _debouncer.RecordChange(subscriptionId, "dbo.Table1", ChangeSource.FileSystem, ChangeType.Modified));
    }

    [Fact]
    public async Task BatchReady_SetsCorrectBatchTimestamps()
    {
        // Arrange
        var subscriptionId = Guid.NewGuid();
        PendingChangeBatch? receivedBatch = null;
        _debouncer.BatchReady += (_, batch) => receivedBatch = batch;
        var beforeRecord = DateTime.UtcNow;

        // Act
        _debouncer.RecordChange(subscriptionId, "dbo.Table1", ChangeSource.FileSystem, ChangeType.Modified);
        await Task.Delay(_debounceWindow + TimeSpan.FromMilliseconds(50));
        var afterBatch = DateTime.UtcNow;

        // Assert
        Assert.NotNull(receivedBatch);
        Assert.True(receivedBatch.BatchStartedAt >= beforeRecord);
        Assert.True(receivedBatch.BatchCompletedAt <= afterBatch);
        Assert.True(receivedBatch.BatchStartedAt <= receivedBatch.BatchCompletedAt);
    }
}

