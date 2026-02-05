using SqlSyncService.Domain.Changes;
using SqlSyncService.Domain.Comparisons;

namespace SqlSyncService.ChangeDetection;

/// <summary>
/// Aggregates rapid changes into batches within a configurable debounce window.
/// Multiple changes to the same object within the window are deduplicated.
/// </summary>
public interface IChangeDebouncer
{
    /// <summary>
    /// Records a change event. Changes are aggregated and deduplicated within the debounce window.
    /// </summary>
    /// <param name="subscriptionId">The subscription the change belongs to.</param>
    /// <param name="objectIdentifier">The identifier of the changed object (e.g., file path or object name).</param>
    /// <param name="source">The source of the change (Database or FileSystem).</param>
    /// <param name="type">The type of change (Created, Modified, Deleted, Renamed).</param>
    void RecordChange(Guid subscriptionId, string objectIdentifier, ChangeSource source, ChangeType type);

    /// <summary>
    /// Records a change event with a known SQL object type. Changes are aggregated and deduplicated within the debounce window.
    /// </summary>
    /// <param name="subscriptionId">The subscription the change belongs to.</param>
    /// <param name="objectIdentifier">The identifier of the changed object (e.g., schema.objectName).</param>
    /// <param name="source">The source of the change (Database or FileSystem).</param>
    /// <param name="type">The type of change (Created, Modified, Deleted, Renamed).</param>
    /// <param name="objectType">The type of SQL object that changed.</param>
    void RecordChange(Guid subscriptionId, string objectIdentifier, ChangeSource source, ChangeType type, SqlObjectType objectType);

    /// <summary>
    /// Raised when a batch of changes is ready for processing after the debounce window expires.
    /// </summary>
    event EventHandler<PendingChangeBatch>? BatchReady;
}

