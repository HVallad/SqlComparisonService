using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using SqlSyncService.Contracts;
using SqlSyncService.Contracts.Folders;

namespace SqlSyncService.Tests.Controllers;

public class FoldersControllerTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public FoldersControllerTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ValidateFolder_Returns_NotFound_For_Missing_Folder()
    {
        // Arrange
        using var client = _factory.CreateClient();

        var request = new ValidateFolderRequest
        {
            Path = Path.Combine(Path.GetTempPath(), "does_not_exist_" + Guid.NewGuid().ToString("N"))
        };

        // Act
        var response = await client.PostAsJsonAsync("/api/folders/validate", request);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.NotFound, error!.Error.Code);
    }

    [Fact]
    public async Task ValidateFolder_Returns_Validation_Error_When_Path_Missing()
    {
        // Arrange
        using var client = _factory.CreateClient();

        // Act - send an empty object so the Path field is missing
        var response = await client.PostAsJsonAsync("/api/folders/validate", new { });

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.ValidationError, error!.Error.Code);
    }

    [Fact]
    public async Task ValidateFolder_Returns_Valid_Response_For_Existing_Writable_Folder()
    {
        // Arrange - create a temporary folder with a simple by-type structure
        var root = Path.Combine(Path.GetTempPath(), "sqlsync_folder_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var tablesDir = Path.Combine(root, "Tables");
        Directory.CreateDirectory(tablesDir);

        var sqlPath = Path.Combine(tablesDir, "Users.sql");
        await File.WriteAllTextAsync(sqlPath, "CREATE TABLE dbo.Users(Id int);");

        try
        {
            using var client = _factory.CreateClient();

            var request = new ValidateFolderRequest
            {
                Path = root
            };

            // Act
            var response = await client.PostAsJsonAsync("/api/folders/validate", request);

            // Assert
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadFromJsonAsync<ValidateFolderResponse>();
            Assert.NotNull(body);

            Assert.True(body!.Valid);
            Assert.True(body.Exists);
            Assert.True(body.IsWritable);
            Assert.Equal(root, body.Path);
            Assert.Equal("by-type", body.DetectedStructure);
            Assert.True(body.SqlFileCount >= 1);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
