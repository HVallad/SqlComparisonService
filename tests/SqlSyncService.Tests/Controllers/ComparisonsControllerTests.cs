using System.Net;
using System.Net.Http.Json;
using System.Linq;
using LiteDB;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SqlSyncService.Contracts;
using SqlSyncService.Contracts.Comparisons;
using SqlSyncService.Domain.Comparisons;
using SqlSyncService.Persistence;

namespace SqlSyncService.Tests.Controllers;

public class ComparisonsControllerTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ComparisonsControllerTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    private WebApplicationFactory<Program> CreateFactoryWithInMemoryLiteDb()
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<ILiteDatabase>(_ => new LiteDatabase(new MemoryStream()));
            });
        });
    }

    [Fact]
    public async Task GetById_Returns_Comparison_Detail_With_Summary()
    {
        // Arrange
        var factory = CreateFactoryWithInMemoryLiteDb();
        using var client = factory.CreateClient();

        var comparisonId = Guid.NewGuid();
        var subscriptionId = Guid.NewGuid();
        var comparedAt = new DateTime(2024, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var duration = TimeSpan.FromSeconds(42);

        using (var scope = factory.Services.CreateScope())
        {
            var history = scope.ServiceProvider.GetRequiredService<IComparisonHistoryRepository>();

            var comparison = new ComparisonResult
            {
                Id = comparisonId,
                SubscriptionId = subscriptionId,
                ComparedAt = comparedAt,
                Duration = duration,
                Status = ComparisonStatus.HasDifferences,
                Summary = new ComparisonSummary
                {
                    TotalDifferences = 3,
                    Additions = 1,
                    Modifications = 1,
                    Deletions = 1,
                    UnsupportedDatabaseObjectCount = 2,
                    UnsupportedFileObjectCount = 3,
                    ByObjectType = new Dictionary<string, int>
                    {
                        ["Table"] = 1,
                        ["ScalarFunction"] = 1,
                        ["Trigger"] = 1
                    }
                },
                Differences = new List<SchemaDifference>
                {
                    new()
                    {
                        Id = Guid.NewGuid(),
                        ObjectName = "dbo.OldTable",
                        SchemaName = "dbo",
                        ObjectType = SqlObjectType.Table,
                        DifferenceType = DifferenceType.Delete,
                        Source = DifferenceSource.Database
                    },
                    new()
                    {
                        Id = Guid.NewGuid(),
                        ObjectName = "dbo.NewFunc",
                        SchemaName = "dbo",
                        ObjectType = SqlObjectType.ScalarFunction,
                        DifferenceType = DifferenceType.Add,
                        Source = DifferenceSource.FileSystem
                    },
                    new()
                    {
                        Id = Guid.NewGuid(),
                        ObjectName = "dbo.Trg",
                        SchemaName = "dbo",
                        ObjectType = SqlObjectType.Trigger,
                        DifferenceType = DifferenceType.Modify,
                        Source = DifferenceSource.FileSystem
                    }
                }
            };

            await history.AddAsync(comparison);
        }

        // Act
        var response = await client.GetAsync($"/api/comparisons/{comparisonId}");

        // Assert
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<ComparisonDetailResponse>();
        Assert.NotNull(body);
        Assert.Equal(comparisonId, body!.Id);
        Assert.Equal(subscriptionId, body.SubscriptionId);
        Assert.Equal("has-differences", body.Status);
        Assert.Equal(comparedAt, body.ComparedAt.ToUniversalTime());
        Assert.Equal(System.Xml.XmlConvert.ToString(duration), body.Duration);
        Assert.Equal(3, body.DifferenceCount);

        var summary = body.Summary;
        Assert.NotNull(summary);
        Assert.Equal(3, summary.TotalDifferences);
        Assert.Equal(1, summary.ByType["table"]);
        Assert.Equal(1, summary.ByType["function"]);
        Assert.Equal(1, summary.ByType["trigger"]);
        Assert.Equal(1, summary.ByAction["add"]);
        Assert.Equal(1, summary.ByAction["modify"]);
        Assert.Equal(1, summary.ByAction["delete"]);
        Assert.Equal(1, summary.ByDirection["database-only"]);
        Assert.Equal(1, summary.ByDirection["file-only"]);
        Assert.Equal(1, summary.ByDirection["different"]);
        Assert.Equal(2, summary.UnsupportedDatabaseObjectCount);
        Assert.Equal(3, summary.UnsupportedFileObjectCount);
    }

    [Fact]
    public async Task GetById_Returns_NotFound_When_Comparison_Does_Not_Exist()
    {
        // Arrange
        var factory = CreateFactoryWithInMemoryLiteDb();
        using var client = factory.CreateClient();

        var missingId = Guid.NewGuid();

        // Act
        var response = await client.GetAsync($"/api/comparisons/{missingId}");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.NotFound, error!.Error.Code);
    }

    [Fact]
    public async Task GetUnsupportedObjects_Returns_List_And_Counts()
    {
        // Arrange
        var factory = CreateFactoryWithInMemoryLiteDb();
        using var client = factory.CreateClient();
        var comparisonId = Guid.NewGuid();
        var subscriptionId = Guid.NewGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var history = scope.ServiceProvider.GetRequiredService<IComparisonHistoryRepository>();

            var comparison = new ComparisonResult
            {
                Id = comparisonId,
                SubscriptionId = subscriptionId,
                ComparedAt = DateTime.UtcNow,
                Duration = TimeSpan.FromSeconds(5),
                Status = ComparisonStatus.HasDifferences,
                Summary = new ComparisonSummary(),
                UnsupportedObjects = new List<UnsupportedObject>
                    {
                        new()
                        {
                            Source = DifferenceSource.Database,
                            ObjectType = SqlObjectType.Login,
                            SchemaName = "master",
                            ObjectName = "AppLogin"
                        },
                        new()
                        {
                            Source = DifferenceSource.FileSystem,
                            ObjectType = SqlObjectType.Unknown,
                            SchemaName = string.Empty,
                            ObjectName = "SomeArtifact",
                            FilePath = "Misc/SomeArtifact.sql"
                        }
                    }
            };

            await history.AddAsync(comparison);
        }

        // Act
        var response = await client.GetAsync($"/api/comparisons/{comparisonId}/unsupported-objects");

        // Assert
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<GetUnsupportedObjectsResponse>();
        Assert.NotNull(body);
        Assert.Equal(comparisonId, body!.ComparisonId);
        Assert.Equal(2, body.TotalCount);
        Assert.Equal(1, body.DatabaseCount);
        Assert.Equal(1, body.FileCount);

        var dbObj = Assert.Single(body.Objects.Where(o => o.Source == "database"));
        Assert.Equal("login", dbObj.ObjectType);
        Assert.Equal("master", dbObj.SchemaName);
        Assert.Equal("AppLogin", dbObj.ObjectName);
        Assert.Null(dbObj.FilePath);

        var fileObj = Assert.Single(body.Objects.Where(o => o.Source == "file"));
        Assert.Equal("unknown", fileObj.ObjectType);
        Assert.Null(fileObj.SchemaName);
        Assert.Equal("SomeArtifact", fileObj.ObjectName);
        Assert.Equal("Misc/SomeArtifact.sql", fileObj.FilePath);
    }

    [Fact]
    public async Task GetDifferences_Returns_All_Differences_When_No_Filters()
    {
        // Arrange
        var factory = CreateFactoryWithInMemoryLiteDb();
        using var client = factory.CreateClient();

        var comparisonId = Guid.NewGuid();
        var subscriptionId = Guid.NewGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var history = scope.ServiceProvider.GetRequiredService<IComparisonHistoryRepository>();

            var differences = new List<SchemaDifference>
                {
                    new()
                    {
                        Id = Guid.NewGuid(),
                        SchemaName = "dbo",
                        ObjectName = "OldTable",
                        ObjectType = SqlObjectType.Table,
                        DifferenceType = DifferenceType.Delete,
                        Source = DifferenceSource.Database
                    },
                    new()
                    {
                        Id = Guid.NewGuid(),
                        SchemaName = "dbo",
                        ObjectName = "NewFunc",
                        ObjectType = SqlObjectType.ScalarFunction,
                        DifferenceType = DifferenceType.Add,
                        Source = DifferenceSource.FileSystem,
                        FilePath = "Functions/dbo.NewFunc.sql"
                    },
                    new()
                    {
                        Id = Guid.NewGuid(),
                        SchemaName = "dbo",
                        ObjectName = "Trg",
                        ObjectType = SqlObjectType.Trigger,
                        DifferenceType = DifferenceType.Modify,
                        Source = DifferenceSource.FileSystem,
                        FilePath = "Triggers/dbo.Trg.sql"
                    }
                };

            var comparison = new ComparisonResult
            {
                Id = comparisonId,
                SubscriptionId = subscriptionId,
                ComparedAt = DateTime.UtcNow,
                Duration = TimeSpan.FromSeconds(5),
                Status = ComparisonStatus.HasDifferences,
                Summary = new ComparisonSummary
                {
                    TotalDifferences = differences.Count,
                    Additions = 1,
                    Modifications = 1,
                    Deletions = 1,
                    ByObjectType = new Dictionary<string, int>
                    {
                        ["Table"] = 1,
                        ["ScalarFunction"] = 1,
                        ["Trigger"] = 1
                    }
                },
                Differences = differences
            };

            await history.AddAsync(comparison);
        }

        // Act
        var response = await client.GetAsync($"/api/comparisons/{comparisonId}/differences");

        // Assert
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<GetComparisonDifferencesResponse>();
        Assert.NotNull(body);
        Assert.Equal(comparisonId, body!.ComparisonId);
        Assert.Equal(3, body.TotalCount);
        Assert.Equal(3, body.Differences.Count);

        var tableDiff = body.Differences.Single(d => d.ObjectType == "table");
        Assert.Equal("dbo.OldTable", tableDiff.ObjectName);
        Assert.Equal("delete", tableDiff.Action);
        Assert.Equal("database-only", tableDiff.Direction);
        Assert.Equal("Object exists in database but not in project files.", tableDiff.Description);
        Assert.Equal("info", tableDiff.Severity);
        Assert.Null(tableDiff.FilePath);
        Assert.Equal("dbo/Tables/OldTable.sql", tableDiff.SuggestedFilePath);

        var functionDiff = body.Differences.Single(d => d.ObjectType == "function");
        Assert.Equal("dbo.NewFunc", functionDiff.ObjectName);
        Assert.Equal("add", functionDiff.Action);
        Assert.Equal("file-only", functionDiff.Direction);
        Assert.Equal("Object exists in project files but not in database.", functionDiff.Description);
        Assert.Equal("info", functionDiff.Severity);
        Assert.Equal("Functions/dbo.NewFunc.sql", functionDiff.FilePath);
        Assert.Null(functionDiff.SuggestedFilePath);

        var triggerDiff = body.Differences.Single(d => d.ObjectType == "trigger");
        Assert.Equal("dbo.Trg", triggerDiff.ObjectName);
        Assert.Equal("change", triggerDiff.Action);
        Assert.Equal("different", triggerDiff.Direction);
        Assert.Equal("warning", triggerDiff.Severity);
        Assert.Equal("Object definition differs between database and project files.", triggerDiff.Description);
    }

    [Fact]
    public async Task GetDifferences_Applies_Filters_For_Type_Action_And_Direction()
    {
        // Arrange
        var factory = CreateFactoryWithInMemoryLiteDb();
        using var client = factory.CreateClient();

        var comparisonId = Guid.NewGuid();
        var subscriptionId = Guid.NewGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var history = scope.ServiceProvider.GetRequiredService<IComparisonHistoryRepository>();

            var differences = new List<SchemaDifference>
                {
                    new()
                    {
                        Id = Guid.NewGuid(),
                        SchemaName = "dbo",
                        ObjectName = "OldTable",
                        ObjectType = SqlObjectType.Table,
                        DifferenceType = DifferenceType.Delete,
                        Source = DifferenceSource.Database
                    },
                    new()
                    {
                        Id = Guid.NewGuid(),
                        SchemaName = "dbo",
                        ObjectName = "NewFunc",
                        ObjectType = SqlObjectType.ScalarFunction,
                        DifferenceType = DifferenceType.Add,
                        Source = DifferenceSource.FileSystem
                    },
                    new()
                    {
                        Id = Guid.NewGuid(),
                        SchemaName = "dbo",
                        ObjectName = "Trg",
                        ObjectType = SqlObjectType.Trigger,
                        DifferenceType = DifferenceType.Modify,
                        Source = DifferenceSource.FileSystem
                    }
                };

            var comparison = new ComparisonResult
            {
                Id = comparisonId,
                SubscriptionId = subscriptionId,
                ComparedAt = DateTime.UtcNow,
                Duration = TimeSpan.FromSeconds(5),
                Status = ComparisonStatus.HasDifferences,
                Summary = new ComparisonSummary
                {
                    TotalDifferences = differences.Count,
                    Additions = 1,
                    Modifications = 1,
                    Deletions = 1,
                    ByObjectType = new Dictionary<string, int>
                    {
                        ["Table"] = 1,
                        ["ScalarFunction"] = 1,
                        ["Trigger"] = 1
                    }
                },
                Differences = differences
            };

            await history.AddAsync(comparison);
        }

        // Act & Assert - filter by type
        var byType = await client.GetFromJsonAsync<GetComparisonDifferencesResponse>($"/api/comparisons/{comparisonId}/differences?type=table");
        Assert.NotNull(byType);
        Assert.Single(byType!.Differences);
        Assert.Equal("table", byType.Differences[0].ObjectType);

        // Act & Assert - filter by action
        var byActionAdd = await client.GetFromJsonAsync<GetComparisonDifferencesResponse>($"/api/comparisons/{comparisonId}/differences?action=add");
        Assert.NotNull(byActionAdd);
        Assert.Single(byActionAdd!.Differences);
        Assert.Equal("add", byActionAdd.Differences[0].Action);

        // "modify" should be treated as alias for "change" (DifferenceType.Modify)
        var byActionModify = await client.GetFromJsonAsync<GetComparisonDifferencesResponse>($"/api/comparisons/{comparisonId}/differences?action=modify");
        Assert.NotNull(byActionModify);
        Assert.Single(byActionModify!.Differences);
        Assert.Equal("change", byActionModify.Differences[0].Action);

        // Act & Assert - filter by direction (hyphenated)
        var byDirectionDatabaseOnly = await client.GetFromJsonAsync<GetComparisonDifferencesResponse>($"/api/comparisons/{comparisonId}/differences?direction=database-only");
        Assert.NotNull(byDirectionDatabaseOnly);
        Assert.Single(byDirectionDatabaseOnly!.Differences);
        Assert.Equal("database-only", byDirectionDatabaseOnly.Differences[0].Direction);

        // Act & Assert - filter by direction (camelCase accepted)
        var byDirectionCamel = await client.GetFromJsonAsync<GetComparisonDifferencesResponse>($"/api/comparisons/{comparisonId}/differences?direction=databaseOnly");
        Assert.NotNull(byDirectionCamel);
        Assert.Single(byDirectionCamel!.Differences);
        Assert.Equal("database-only", byDirectionCamel.Differences[0].Direction);
    }

    [Fact]
    public async Task GetDifferences_Returns_NotFound_When_Comparison_Does_Not_Exist()
    {
        // Arrange
        var factory = CreateFactoryWithInMemoryLiteDb();
        using var client = factory.CreateClient();

        var missingId = Guid.NewGuid();

        // Act
        var response = await client.GetAsync($"/api/comparisons/{missingId}/differences");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.NotFound, error!.Error.Code);
    }

    [Fact]
    public async Task GetDifferenceById_Returns_Detailed_Difference()
    {
        // Arrange
        var factory = CreateFactoryWithInMemoryLiteDb();
        using var client = factory.CreateClient();

        var comparisonId = Guid.NewGuid();
        var subscriptionId = Guid.NewGuid();
        var differenceId = Guid.NewGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var history = scope.ServiceProvider.GetRequiredService<IComparisonHistoryRepository>();

            var comparison = new ComparisonResult
            {
                Id = comparisonId,
                SubscriptionId = subscriptionId,
                ComparedAt = DateTime.UtcNow,
                Duration = TimeSpan.FromSeconds(5),
                Status = ComparisonStatus.HasDifferences,
                Summary = new ComparisonSummary
                {
                    TotalDifferences = 1,
                    Additions = 0,
                    Modifications = 1,
                    Deletions = 0,
                    ByObjectType = new Dictionary<string, int>
                    {
                        ["StoredProcedure"] = 1
                    }
                },
                Differences = new List<SchemaDifference>
                    {
                        new()
                        {
                            Id = differenceId,
                            SchemaName = "dbo",
                            ObjectName = "GetUsers",
                            ObjectType = SqlObjectType.StoredProcedure,
                            DifferenceType = DifferenceType.Modify,
                            Source = DifferenceSource.FileSystem,
                            FilePath = "StoredProcedures/dbo.GetUsers.sql",
                            DatabaseDefinition = "CREATE PROCEDURE [dbo].[GetUsers] AS SELECT 1;",
                            FileDefinition = "CREATE PROCEDURE [dbo].[GetUsers] AS SELECT 2;",
                            PropertyChanges = new List<PropertyDifference>
                            {
                                new()
                                {
                                    PropertyName = "DefinitionHash",
                                    DatabaseValue = "hash-db",
                                    FileValue = "hash-file"
                                }
                            }
                        }
                    }
            };

            await history.AddAsync(comparison);
        }

        // Act
        var response = await client.GetAsync($"/api/comparisons/{comparisonId}/differences/{differenceId}");

        // Assert
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<ComparisonDifferenceDetailResponse>();
        Assert.NotNull(body);
        Assert.Equal(differenceId, body!.Id);
        Assert.Equal(comparisonId, body.ComparisonId);
        Assert.Equal(subscriptionId, body.SubscriptionId);
        Assert.Equal("stored-procedure", body.ObjectType);
        Assert.Equal("dbo.GetUsers", body.ObjectName);
        Assert.Equal("change", body.Action);
        Assert.Equal("different", body.Direction);
        Assert.Equal("StoredProcedures/dbo.GetUsers.sql", body.FilePath);
        Assert.Equal("CREATE PROCEDURE [dbo].[GetUsers] AS SELECT 1;", body.DatabaseScript);
        Assert.Equal("CREATE PROCEDURE [dbo].[GetUsers] AS SELECT 2;", body.FileScript);
        Assert.Null(body.UnifiedDiff);
        Assert.Null(body.SideBySideDiff);
        var propertyChange = Assert.Single(body.PropertyChanges);
        Assert.Equal("DefinitionHash", propertyChange.PropertyName);
        Assert.Equal("hash-db", propertyChange.DatabaseValue);
        Assert.Equal("hash-file", propertyChange.FileValue);
    }

    [Fact]
    public async Task GetDifferenceById_Returns_NotFound_When_Comparison_Does_Not_Exist()
    {
        // Arrange
        var factory = CreateFactoryWithInMemoryLiteDb();
        using var client = factory.CreateClient();

        var comparisonId = Guid.NewGuid();
        var diffId = Guid.NewGuid();

        // Act
        var response = await client.GetAsync($"/api/comparisons/{comparisonId}/differences/{diffId}");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.NotFound, error!.Error.Code);
    }

    [Fact]
    public async Task GetDifferenceById_Returns_NotFound_When_Difference_Does_Not_Exist()
    {
        // Arrange
        var factory = CreateFactoryWithInMemoryLiteDb();
        using var client = factory.CreateClient();

        var comparisonId = Guid.NewGuid();
        var subscriptionId = Guid.NewGuid();
        var existingDifferenceId = Guid.NewGuid();
        var missingDifferenceId = Guid.NewGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var history = scope.ServiceProvider.GetRequiredService<IComparisonHistoryRepository>();

            var comparison = new ComparisonResult
            {
                Id = comparisonId,
                SubscriptionId = subscriptionId,
                ComparedAt = DateTime.UtcNow,
                Duration = TimeSpan.FromSeconds(5),
                Status = ComparisonStatus.HasDifferences,
                Summary = new ComparisonSummary
                {
                    TotalDifferences = 1,
                    Additions = 1,
                    Modifications = 0,
                    Deletions = 0,
                    ByObjectType = new Dictionary<string, int>
                    {
                        ["Table"] = 1
                    }
                },
                Differences = new List<SchemaDifference>
                    {
                        new()
                        {
                            Id = existingDifferenceId,
                            SchemaName = "dbo",
                            ObjectName = "ExistingTable",
                            ObjectType = SqlObjectType.Table,
                            DifferenceType = DifferenceType.Add,
                            Source = DifferenceSource.FileSystem
                        }
                    }
            };

            await history.AddAsync(comparison);
        }

        // Act
        var response = await client.GetAsync($"/api/comparisons/{comparisonId}/differences/{missingDifferenceId}");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.NotFound, error!.Error.Code);
    }
}
