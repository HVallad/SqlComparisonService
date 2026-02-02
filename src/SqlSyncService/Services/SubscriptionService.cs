using System;
using System.Linq;
using SqlSyncService.Contracts.Subscriptions;
using SqlSyncService.Domain.Comparisons;
using SqlSyncService.Domain.Subscriptions;
using SqlSyncService.Persistence;

namespace SqlSyncService.Services;

public sealed class SubscriptionService : ISubscriptionService
{
    private readonly ISubscriptionRepository _subscriptions;
    private readonly IComparisonHistoryRepository _history;

    public SubscriptionService(ISubscriptionRepository subscriptions, IComparisonHistoryRepository history)
    {
        _subscriptions = subscriptions;
        _history = history;
    }

    public async Task<IReadOnlyList<Subscription>> GetAllAsync(SubscriptionState? stateFilter = null, CancellationToken cancellationToken = default)
    {
        var all = await _subscriptions.GetAllAsync(cancellationToken).ConfigureAwait(false);
        if (!stateFilter.HasValue)
        {
            return all;
        }

        return all.Where(s => s.State == stateFilter.Value).ToList();
    }

    public Task<Subscription?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _subscriptions.GetByIdAsync(id, cancellationToken);

    public async Task<Subscription> CreateAsync(CreateSubscriptionRequest request, CancellationToken cancellationToken = default)
    {
        var existing = await _subscriptions.GetAllAsync(cancellationToken).ConfigureAwait(false);
        if (existing.Any(s => string.Equals(s.Name, request.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new SubscriptionConflictException($"A subscription named '{request.Name}' already exists.", "name");
        }

        var subscription = new Subscription
        {
            Name = request.Name,
            Database = new DatabaseConnection
            {
                Server = request.Database.Server,
                Database = request.Database.Database,
                AuthType = MapAuthType(request.Database.AuthType),
                Username = request.Database.Username,
                EncryptedPassword = request.Database.Password,
                TrustServerCertificate = request.Database.TrustServerCertificate,
                ConnectionTimeoutSeconds = request.Database.ConnectionTimeoutSeconds
            },
            Project = new ProjectFolder
            {
                RootPath = request.Project.Path,
                IncludePatterns = request.Project.IncludePatterns ?? new[] { "**/*.sql" },
                ExcludePatterns = request.Project.ExcludePatterns ?? new[] { "**/bin/**", "**/obj/**" },
                Structure = MapFolderStructure(request.Project.Structure)
            },
            Options = MapOptionsFromCreate(request.Options),
            State = SubscriptionState.Active,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await _subscriptions.AddAsync(subscription, cancellationToken).ConfigureAwait(false);
        return subscription;
    }

    public async Task<Subscription> UpdateAsync(Guid id, UpdateSubscriptionRequest request, CancellationToken cancellationToken = default)
    {
        var subscription = await _subscriptions.GetByIdAsync(id, cancellationToken).ConfigureAwait(false)
                          ?? throw new SubscriptionNotFoundException(id);

        var originalName = subscription.Name;

        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            subscription.Name = request.Name;
        }

        if (request.Database is not null)
        {
            if (!string.IsNullOrWhiteSpace(request.Database.Server))
            {
                subscription.Database.Server = request.Database.Server;
            }

            if (!string.IsNullOrWhiteSpace(request.Database.Database))
            {
                subscription.Database.Database = request.Database.Database;
            }

            if (!string.IsNullOrWhiteSpace(request.Database.AuthType))
            {
                subscription.Database.AuthType = MapAuthType(request.Database.AuthType);
            }

            if (request.Database.Username is not null)
            {
                subscription.Database.Username = request.Database.Username;
            }

            if (request.Database.Password is not null)
            {
                subscription.Database.EncryptedPassword = request.Database.Password;
            }

            if (request.Database.TrustServerCertificate.HasValue)
            {
                subscription.Database.TrustServerCertificate = request.Database.TrustServerCertificate.Value;
            }

            if (request.Database.ConnectionTimeoutSeconds.HasValue)
            {
                subscription.Database.ConnectionTimeoutSeconds = request.Database.ConnectionTimeoutSeconds.Value;
            }
        }

        if (request.Project is not null)
        {
            if (!string.IsNullOrWhiteSpace(request.Project.Path))
            {
                subscription.Project.RootPath = request.Project.Path;
            }

            if (request.Project.IncludePatterns is not null)
            {
                subscription.Project.IncludePatterns = request.Project.IncludePatterns;
            }

            if (request.Project.ExcludePatterns is not null)
            {
                subscription.Project.ExcludePatterns = request.Project.ExcludePatterns;
            }

            if (!string.IsNullOrWhiteSpace(request.Project.Structure))
            {
                subscription.Project.Structure = MapFolderStructure(request.Project.Structure);
            }
        }

        if (request.Options is not null)
        {
            if (request.Options.AutoCompare.HasValue)
            {
                subscription.Options.AutoCompare = request.Options.AutoCompare.Value;
            }

            if (request.Options.CompareOnFileChange.HasValue)
            {
                subscription.Options.CompareOnFileChange = request.Options.CompareOnFileChange.Value;
            }

            if (request.Options.CompareOnDatabaseChange.HasValue)
            {
                subscription.Options.CompareOnDatabaseChange = request.Options.CompareOnDatabaseChange.Value;
            }

            if (request.Options.IgnoreWhitespace.HasValue)
            {
                subscription.Options.IgnoreWhitespace = request.Options.IgnoreWhitespace.Value;
            }

            if (request.Options.IgnoreComments.HasValue)
            {
                subscription.Options.IgnoreComments = request.Options.IgnoreComments.Value;
            }

            if (request.Options.ObjectTypes is not null)
            {
                ApplyObjectTypes(subscription.Options, request.Options.ObjectTypes);
            }
        }

        if (!string.Equals(originalName, subscription.Name, StringComparison.OrdinalIgnoreCase))
        {
            var existing = await _subscriptions.GetAllAsync(cancellationToken).ConfigureAwait(false);
            if (existing.Any(s => s.Id != id && string.Equals(s.Name, subscription.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new SubscriptionConflictException($"A subscription named '{subscription.Name}' already exists.", "name");
            }
        }

        subscription.UpdatedAt = DateTime.UtcNow;
        await _subscriptions.UpdateAsync(subscription, cancellationToken).ConfigureAwait(false);
        return subscription;
    }

    public async Task<bool> DeleteAsync(Guid id, bool deleteHistory, CancellationToken cancellationToken = default)
    {
        var deleted = await _subscriptions.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        if (!deleted)
        {
            return false;
        }

        if (deleteHistory)
        {
            await _history.DeleteBySubscriptionAsync(id, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    public async Task<Subscription> PauseAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var subscription = await _subscriptions.GetByIdAsync(id, cancellationToken).ConfigureAwait(false)
                          ?? throw new SubscriptionNotFoundException(id);

        subscription.State = SubscriptionState.Paused;
        if (!subscription.PausedAt.HasValue)
        {
            subscription.PausedAt = DateTime.UtcNow;
        }

        subscription.UpdatedAt = DateTime.UtcNow;
        await _subscriptions.UpdateAsync(subscription, cancellationToken).ConfigureAwait(false);
        return subscription;
    }

    public async Task<Subscription> ResumeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var subscription = await _subscriptions.GetByIdAsync(id, cancellationToken).ConfigureAwait(false)
                          ?? throw new SubscriptionNotFoundException(id);

        if (subscription.State != SubscriptionState.Paused)
        {
            throw new SubscriptionConflictException($"Subscription '{id}' is not paused.", "state");
        }

        subscription.State = SubscriptionState.Active;
        subscription.ResumedAt = DateTime.UtcNow;
        subscription.UpdatedAt = DateTime.UtcNow;

        await _subscriptions.UpdateAsync(subscription, cancellationToken).ConfigureAwait(false);
        return subscription;
    }

    private static AuthenticationType MapAuthType(string authType)
    {
        if (string.IsNullOrWhiteSpace(authType))
        {
            return AuthenticationType.WindowsIntegrated;
        }

        return authType.Trim().ToLowerInvariant() switch
        {
            "sql" or "sqlserver" => AuthenticationType.SqlServer,
            "azuread" => AuthenticationType.AzureAD,
            "azuread-interactive" or "azureadinteractive" => AuthenticationType.AzureADInteractive,
            _ => AuthenticationType.WindowsIntegrated
        };
    }

    private static FolderStructure MapFolderStructure(string structure)
    {
        if (string.IsNullOrWhiteSpace(structure))
        {
            return FolderStructure.ByObjectType;
        }

        return structure.Trim().ToLowerInvariant() switch
        {
            "flat" => FolderStructure.Flat,
            "by-schema" => FolderStructure.BySchema,
            "by-schema-and-type" => FolderStructure.BySchemaAndType,
            _ => FolderStructure.ByObjectType
        };
    }

    private static ComparisonOptions MapOptionsFromCreate(CreateSubscriptionOptionsConfig options)
    {
        var result = new ComparisonOptions
        {
            AutoCompare = options.AutoCompare,
            CompareOnFileChange = options.CompareOnFileChange,
            CompareOnDatabaseChange = options.CompareOnDatabaseChange,
            IgnoreWhitespace = options.IgnoreWhitespace,
            IgnoreComments = options.IgnoreComments
        };

        ApplyObjectTypes(result, options.ObjectTypes);
        return result;
    }

    private static void ApplyObjectTypes(ComparisonOptions options, string[]? objectTypes)
    {
        if (objectTypes is null || objectTypes.Length == 0)
        {
            return;
        }

        options.IncludeTables = false;
        options.IncludeViews = false;
        options.IncludeStoredProcedures = false;
        options.IncludeFunctions = false;
        options.IncludeTriggers = false;

        foreach (var raw in objectTypes)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var type = raw.Trim().ToLowerInvariant();
            switch (type)
            {
                case "table":
                    options.IncludeTables = true;
                    break;
                case "view":
                    options.IncludeViews = true;
                    break;
                case "stored-procedure" or "storedprocedure":
                    options.IncludeStoredProcedures = true;
                    break;
                case "function":
                    options.IncludeFunctions = true;
                    break;
                case "trigger":
                    options.IncludeTriggers = true;
                    break;
            }
        }
    }
}

public sealed class SubscriptionNotFoundException : Exception
{
    public SubscriptionNotFoundException(Guid id)
        : base($"Subscription '{id}' was not found.")
    {
    }
}

public sealed class SubscriptionConflictException : Exception
{
    public SubscriptionConflictException(string message, string? field)
        : base(message)
    {
        Field = field;
    }

    public string? Field { get; }
}

