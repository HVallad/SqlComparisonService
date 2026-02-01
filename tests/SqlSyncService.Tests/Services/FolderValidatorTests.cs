using SqlSyncService.Contracts.Folders;
using SqlSyncService.Services;

namespace SqlSyncService.Tests.Services;

public class FolderValidatorTests
{
    [Fact]
    public async Task ValidateFolder_Returns_Invalid_When_Folder_Does_Not_Exist()
    {
        // Arrange
        var validator = new FolderValidator();
        var path = Path.Combine(Path.GetTempPath(), "missing_" + Guid.NewGuid().ToString("N"));

        var request = new ValidateFolderRequest
        {
            Path = path
        };

        // Act
        var result = await validator.ValidateFolderAsync(request);

        // Assert
        Assert.False(result.Exists);
        Assert.False(result.Valid);
        Assert.Equal(0, result.SqlFileCount);
    }

    [Fact]
    public async Task ValidateFolder_Detects_Flat_Structure()
    {
        // Arrange
        var root = Path.Combine(Path.GetTempPath(), "flat_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var sqlPath = Path.Combine(root, "script.sql");
        await File.WriteAllTextAsync(sqlPath, "CREATE TABLE dbo.Test(Id int);");

        try
        {
            var validator = new FolderValidator();
            var request = new ValidateFolderRequest { Path = root };

            // Act
            var result = await validator.ValidateFolderAsync(request);

            // Assert
            Assert.True(result.Valid);
            Assert.Equal("flat", result.DetectedStructure);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ValidateFolder_Detects_ByType_Structure()
    {
        // Arrange
        var root = Path.Combine(Path.GetTempPath(), "bytype_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var tablesDir = Path.Combine(root, "Tables");
        Directory.CreateDirectory(tablesDir);

        var sqlPath = Path.Combine(tablesDir, "Users.sql");
        await File.WriteAllTextAsync(sqlPath, "CREATE TABLE dbo.Users(Id int);");

        try
        {
            var validator = new FolderValidator();
            var request = new ValidateFolderRequest { Path = root };

            // Act
            var result = await validator.ValidateFolderAsync(request);

            // Assert
            Assert.True(result.Valid);
            Assert.Equal("by-type", result.DetectedStructure);
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
