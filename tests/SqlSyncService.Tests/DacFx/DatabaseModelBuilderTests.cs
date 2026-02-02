using SqlSyncService.DacFx;
using SqlSyncService.Domain.Caching;
using SqlSyncService.Domain.Comparisons;
using SqlSyncService.Domain.Subscriptions;

namespace SqlSyncService.Tests.DacFx;

public class DatabaseModelBuilderTests
{
	    [Fact]
	    public async Task BuildSnapshotAsync_Uses_Overrides_For_Extraction_And_ObjectPopulation()
	    {
	        // Arrange
	        var builder = new DatabaseModelBuilder();
	        var subscriptionId = Guid.NewGuid();
	        var connection = new DatabaseConnection
	        {
	            Server = "localhost",
	            Database = "TestDb"
	        };

	        var extractCalls = 0;
	        var populateCalls = 0;
	        var dacpacBytes = new byte[] { 1, 2, 3 };

	        try
	        {
	            DatabaseModelBuilder.ExtractDacpacOverride = (conn, ct) =>
	            {
	                extractCalls++;
	                Assert.Same(connection, conn);
	                return Task.FromResult(dacpacBytes);
	            };

	            DatabaseModelBuilder.PopulateObjectsOverride = snapshot =>
	            {
	                populateCalls++;
	                snapshot.Objects.Add(new SchemaObjectSummary
	                {
	                    SchemaName = "dbo",
	                    ObjectName = "Users"
	                });
	            };

	            // Act
	            var snapshot = await builder.BuildSnapshotAsync(subscriptionId, connection);

	            // Assert
	            Assert.Equal(subscriptionId, snapshot.SubscriptionId);
	            Assert.Equal("TestDb", snapshot.DatabaseVersion);
	            Assert.Equal(dacpacBytes, snapshot.DacpacBytes);
	            Assert.False(string.IsNullOrWhiteSpace(snapshot.Hash));
	            Assert.Equal(1, extractCalls);
	            Assert.Equal(1, populateCalls);
	            Assert.Single(snapshot.Objects);
	            Assert.Equal("dbo", snapshot.Objects[0].SchemaName);
	            Assert.Equal("Users", snapshot.Objects[0].ObjectName);
	        }
		        finally
		        {
		            DatabaseModelBuilder.ExtractDacpacOverride = null;
		            DatabaseModelBuilder.PopulateObjectsOverride = null;
		            DatabaseModelBuilder.LoadLoginsOverride = null;
		        }
		    }

	    [Fact]
	    public async Task BuildSnapshotAsync_Includes_Logins_When_Override_Provided()
	    {
	        // Arrange
	        var builder = new DatabaseModelBuilder();
	        var subscriptionId = Guid.NewGuid();
	        var connection = new DatabaseConnection
	        {
	            Server = "localhost",
	            Database = "TestDb"
	        };

	        var dacpacBytes = new byte[] { 9, 9, 9 };

	        try
	        {
	            DatabaseModelBuilder.ExtractDacpacOverride = (conn, ct) => Task.FromResult(dacpacBytes);
	            DatabaseModelBuilder.PopulateObjectsOverride = snapshot =>
	            {
	                snapshot.Objects.Add(new SchemaObjectSummary
	                {
	                    SchemaName = "dbo",
	                    ObjectName = "Users",
	                    ObjectType = SqlObjectType.Table
	                });
	            };

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
	            Assert.Equal(dacpacBytes, snapshot.DacpacBytes);
	            Assert.False(string.IsNullOrWhiteSpace(snapshot.Hash));
	            Assert.Equal(3, snapshot.Objects.Count);
	            Assert.Contains(snapshot.Objects, o => o.ObjectType == SqlObjectType.Table && o.ObjectName == "Users");
	            Assert.Contains(snapshot.Objects, o => o.ObjectType == SqlObjectType.Login && o.ObjectName == "LoginA");
	            Assert.Contains(snapshot.Objects, o => o.ObjectType == SqlObjectType.Login && o.ObjectName == "LoginB");
	        }
	        finally
	        {
	            DatabaseModelBuilder.ExtractDacpacOverride = null;
	            DatabaseModelBuilder.PopulateObjectsOverride = null;
	            DatabaseModelBuilder.LoadLoginsOverride = null;
	        }
	    }

    [Fact]
    public void SplitNameParts_Uses_Last_Two_Parts_For_Schema_And_Name()
    {
        // Arrange
        var parts = new[] { "MyDatabase", "dbo", "Users" };

        // Act
        var (schema, name) = DatabaseModelBuilder.SplitNameParts(parts);

        // Assert
        Assert.Equal("dbo", schema);
        Assert.Equal("Users", name);
    }

    [Fact]
    public void SplitNameParts_Assumes_Dbo_For_Single_Part_Name()
    {
        // Arrange
        var parts = new[] { "Users" };

        // Act
        var (schema, name) = DatabaseModelBuilder.SplitNameParts(parts);

        // Assert
        Assert.Equal("dbo", schema);
        Assert.Equal("Users", name);
    }

    [Fact]
    public void SplitNameParts_Returns_Empty_For_No_Parts()
    {
        // Arrange
        var parts = Array.Empty<string>();

        // Act
        var (schema, name) = DatabaseModelBuilder.SplitNameParts(parts);

        // Assert
        Assert.Equal(string.Empty, schema);
        Assert.Equal(string.Empty, name);
    }
}

