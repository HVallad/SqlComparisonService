using SqlSyncService.DacFx;
using SqlSyncService.Domain.Caching;
using SqlSyncService.Domain.Comparisons;
using SqlSyncService.Domain.Subscriptions;

namespace SqlSyncService.Tests.DacFx;

public class DatabaseModelBuilderTests
{
    [Fact]
    public async Task BuildSnapshotAsync_Uses_SchemaReader_For_AllObjects()
    {
        // Arrange
        var mockReader = new MockDatabaseSchemaReader();
        mockReader.ObjectsToReturn = new List<SchemaObjectSummary>
        {
            new SchemaObjectSummary
            {
                SchemaName = "dbo",
                ObjectName = "Users",
                ObjectType = SqlObjectType.Table,
                DefinitionHash = "ABC123"
            }
        };

        var builder = new DatabaseModelBuilder(mockReader);
        var subscriptionId = Guid.NewGuid();
        var connection = new DatabaseConnection
        {
            Server = "localhost",
            Database = "TestDb"
        };

        try
        {
            // Disable login loading for this test
            DatabaseModelBuilder.LoadLoginsOverride = (conn, ct) =>
                Task.FromResult<IReadOnlyCollection<SchemaObjectSummary>>(Array.Empty<SchemaObjectSummary>());

            // Act
            var snapshot = await builder.BuildSnapshotAsync(subscriptionId, connection);

            // Assert
            Assert.Equal(subscriptionId, snapshot.SubscriptionId);
            Assert.Equal("TestDb", snapshot.DatabaseVersion);
            Assert.False(string.IsNullOrWhiteSpace(snapshot.Hash));
            Assert.True(mockReader.GetAllObjectsCalled);
            Assert.Single(snapshot.Objects);
            Assert.Equal("dbo", snapshot.Objects[0].SchemaName);
            Assert.Equal("Users", snapshot.Objects[0].ObjectName);
        }
        finally
        {
            DatabaseModelBuilder.LoadLoginsOverride = null;
        }
    }

    [Fact]
    public async Task BuildSnapshotAsync_Uses_SchemaReader_For_FilteredObjects()
    {
        // Arrange
        var mockReader = new MockDatabaseSchemaReader();
        mockReader.ObjectsToReturn = new List<SchemaObjectSummary>
        {
            new SchemaObjectSummary
            {
                SchemaName = "dbo",
                ObjectName = "GetUsers",
                ObjectType = SqlObjectType.StoredProcedure,
                DefinitionHash = "DEF456"
            }
        };

        var builder = new DatabaseModelBuilder(mockReader);
        var subscriptionId = Guid.NewGuid();
        var connection = new DatabaseConnection
        {
            Server = "localhost",
            Database = "TestDb"
        };

        try
        {
            // Disable login loading for this test
            DatabaseModelBuilder.LoadLoginsOverride = (conn, ct) =>
                Task.FromResult<IReadOnlyCollection<SchemaObjectSummary>>(Array.Empty<SchemaObjectSummary>());

            // Act
            var snapshot = await builder.BuildSnapshotAsync(subscriptionId, connection, SqlObjectType.StoredProcedure);

            // Assert
            Assert.True(mockReader.GetObjectsByTypeCalled);
            Assert.Equal(SqlObjectType.StoredProcedure, mockReader.LastFilterType);
            Assert.Single(snapshot.Objects);
            Assert.Equal("GetUsers", snapshot.Objects[0].ObjectName);
        }
        finally
        {
            DatabaseModelBuilder.LoadLoginsOverride = null;
        }
    }

    [Fact]
    public async Task BuildSnapshotAsync_Includes_Logins_When_Override_Provided()
    {
        // Arrange
        var mockReader = new MockDatabaseSchemaReader();
        mockReader.ObjectsToReturn = new List<SchemaObjectSummary>
        {
            new SchemaObjectSummary
            {
                SchemaName = "dbo",
                ObjectName = "Users",
                ObjectType = SqlObjectType.Table,
                DefinitionHash = "TABLE123"
            }
        };

        var builder = new DatabaseModelBuilder(mockReader);
        var subscriptionId = Guid.NewGuid();
        var connection = new DatabaseConnection
        {
            Server = "localhost",
            Database = "TestDb"
        };

        try
        {
            DatabaseModelBuilder.LoadLoginsOverride = (conn, ct) =>
            {
                IReadOnlyCollection<SchemaObjectSummary> logins = new[]
                {
                    new SchemaObjectSummary
                    {
                        SchemaName = string.Empty,
                        ObjectName = "LoginA",
                        ObjectType = SqlObjectType.Login
                    },
                    new SchemaObjectSummary
                    {
                        SchemaName = string.Empty,
                        ObjectName = "LoginB",
                        ObjectType = SqlObjectType.Login
                    }
                };

                return Task.FromResult(logins);
            };

            // Act
            var snapshot = await builder.BuildSnapshotAsync(subscriptionId, connection);

            // Assert
            Assert.Equal(subscriptionId, snapshot.SubscriptionId);
            Assert.Equal("TestDb", snapshot.DatabaseVersion);
            Assert.False(string.IsNullOrWhiteSpace(snapshot.Hash));
            Assert.Equal(3, snapshot.Objects.Count);
            Assert.Contains(snapshot.Objects, o => o.ObjectType == SqlObjectType.Table && o.ObjectName == "Users");
            Assert.Contains(snapshot.Objects, o => o.ObjectType == SqlObjectType.Login && o.ObjectName == "LoginA");
            Assert.Contains(snapshot.Objects, o => o.ObjectType == SqlObjectType.Login && o.ObjectName == "LoginB");
        }
        finally
        {
            DatabaseModelBuilder.LoadLoginsOverride = null;
        }
    }

    [Fact]
    public async Task BuildSnapshotAsync_Skips_Logins_When_Filtering_For_Other_Types()
    {
        // Arrange
        var mockReader = new MockDatabaseSchemaReader();
        mockReader.ObjectsToReturn = new List<SchemaObjectSummary>
        {
            new SchemaObjectSummary
            {
                SchemaName = "dbo",
                ObjectName = "Users",
                ObjectType = SqlObjectType.Table,
                DefinitionHash = "TABLE123"
            }
        };

        var builder = new DatabaseModelBuilder(mockReader);
        var subscriptionId = Guid.NewGuid();
        var connection = new DatabaseConnection
        {
            Server = "localhost",
            Database = "TestDb"
        };

        var loginOverrideCalled = false;

        try
        {
            DatabaseModelBuilder.LoadLoginsOverride = (conn, ct) =>
            {
                loginOverrideCalled = true;
                return Task.FromResult<IReadOnlyCollection<SchemaObjectSummary>>(Array.Empty<SchemaObjectSummary>());
            };

            // Act - filter by Table, should skip login loading
            var snapshot = await builder.BuildSnapshotAsync(subscriptionId, connection, SqlObjectType.Table);

            // Assert
            Assert.False(loginOverrideCalled, "Login loading should be skipped when filtering for non-Login types");
            Assert.Single(snapshot.Objects);
        }
        finally
        {
            DatabaseModelBuilder.LoadLoginsOverride = null;
        }
    }

    /// <summary>
    /// Mock implementation of IDatabaseSchemaReader for testing.
    /// </summary>
    private class MockDatabaseSchemaReader : IDatabaseSchemaReader
    {
        public List<SchemaObjectSummary> ObjectsToReturn { get; set; } = new();
        public bool GetAllObjectsCalled { get; private set; }
        public bool GetObjectsByTypeCalled { get; private set; }
        public bool GetObjectCalled { get; private set; }
        public SqlObjectType? LastFilterType { get; private set; }

        public Task<IReadOnlyList<SchemaObjectSummary>> GetAllObjectsAsync(
            DatabaseConnection connection,
            CancellationToken cancellationToken = default)
        {
            GetAllObjectsCalled = true;
            return Task.FromResult<IReadOnlyList<SchemaObjectSummary>>(ObjectsToReturn);
        }

        public Task<SchemaObjectSummary?> GetObjectAsync(
            DatabaseConnection connection,
            string schemaName,
            string objectName,
            SqlObjectType objectType,
            CancellationToken cancellationToken = default)
        {
            GetObjectCalled = true;
            LastFilterType = objectType;
            var obj = ObjectsToReturn.FirstOrDefault(o =>
                o.SchemaName == schemaName &&
                o.ObjectName == objectName &&
                o.ObjectType == objectType);
            return Task.FromResult(obj);
        }

        public Task<IReadOnlyList<SchemaObjectSummary>> GetObjectsByTypeAsync(
            DatabaseConnection connection,
            SqlObjectType objectType,
            CancellationToken cancellationToken = default)
        {
            GetObjectsByTypeCalled = true;
            LastFilterType = objectType;
            var filtered = ObjectsToReturn.Where(o => o.ObjectType == objectType).ToList();
            return Task.FromResult<IReadOnlyList<SchemaObjectSummary>>(filtered);
        }

        public Task<IReadOnlyList<SchemaObjectSummary>> GetObjectsAsync(
            DatabaseConnection connection,
            IEnumerable<SqlSyncService.Domain.Changes.ObjectIdentifier> objectsToQuery,
            CancellationToken cancellationToken = default)
        {
            // Return matching objects from ObjectsToReturn based on identifiers
            var identifierList = objectsToQuery.ToList();
            var results = ObjectsToReturn.Where(o =>
                identifierList.Any(id =>
                    string.Equals(id.SchemaName, o.SchemaName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(id.ObjectName, o.ObjectName, StringComparison.OrdinalIgnoreCase) &&
                    id.ObjectType == o.ObjectType)).ToList();
            return Task.FromResult<IReadOnlyList<SchemaObjectSummary>>(results);
        }
    }
}
