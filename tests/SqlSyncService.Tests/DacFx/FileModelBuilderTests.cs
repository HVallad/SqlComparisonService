using SqlSyncService.DacFx;
using SqlSyncService.Domain.Subscriptions;
using SqlSyncService.Domain.Comparisons;

namespace SqlSyncService.Tests.DacFx;

public class FileModelBuilderTests
{
    [Fact]
    public async Task BuildCacheAsync_Classifies_Function_With_CreateTable_In_Body_As_Function()
    {
        // Arrange
        var root = Path.Combine(Path.GetTempPath(), "func_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var sqlPath = Path.Combine(root, "dbo.fn_GenerateCreateTableScript.sql");
            var script = @"CREATE FUNCTION [dbo].[fn_GenerateCreateTableScript]()
RETURNS NVARCHAR(MAX)
AS
BEGIN
    DECLARE @sql NVARCHAR(MAX) = 'CREATE TABLE dbo.Test(Id int);';
    RETURN @sql;
END";

            await File.WriteAllTextAsync(sqlPath, script);

            var builder = new FileModelBuilder();
            var folder = new ProjectFolder { RootPath = root };

            // Act
            var cache = await builder.BuildCacheAsync(Guid.NewGuid(), folder);

            // Assert
            var entry = Assert.Single(cache.FileEntries).Value;
            Assert.Equal("fn_GenerateCreateTableScript", entry.ObjectName);
            Assert.Equal(SqlObjectType.ScalarFunction, entry.ObjectType);
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
    public async Task BuildCacheAsync_Ignores_Comments_When_Classifying_ObjectType()
    {
        // Arrange
        var root = Path.Combine(Path.GetTempPath(), "comments_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var sqlPath = Path.Combine(root, "CommentTest.sql");
            var script = @"-- CREATE TABLE dbo.ShouldBeIgnored(Id int);
/* CREATE PROCEDURE dbo.ShouldAlsoBeIgnored AS SELECT 1; */
CREATE VIEW dbo.RealView AS SELECT 42 AS Value;";

            await File.WriteAllTextAsync(sqlPath, script);

            var builder = new FileModelBuilder();
            var folder = new ProjectFolder { RootPath = root };

            // Act
            var cache = await builder.BuildCacheAsync(Guid.NewGuid(), folder);

            // Assert
            var entry = Assert.Single(cache.FileEntries).Value;
            Assert.Equal(SqlObjectType.View, entry.ObjectType);
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
    public async Task BuildCacheAsync_Classifies_Login_Script_As_Login()
    {
        // Arrange
        var root = Path.Combine(Path.GetTempPath(), "login_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var sqlPath = Path.Combine(root, "CreateLogin.sql");
            var script = @"CREATE LOGIN [TestLogin] WITH PASSWORD = 'P@ssw0rd';";

            await File.WriteAllTextAsync(sqlPath, script);

            var builder = new FileModelBuilder();
            var folder = new ProjectFolder { RootPath = root };

            // Act
            var cache = await builder.BuildCacheAsync(Guid.NewGuid(), folder);

            // Assert
            var entry = Assert.Single(cache.FileEntries).Value;
            Assert.Equal(SqlObjectType.Login, entry.ObjectType);
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
	    public async Task BuildCacheAsync_Classifies_Role_Script_As_Role()
	    {
	        // Arrange
	        var root = Path.Combine(Path.GetTempPath(), "role_" + Guid.NewGuid().ToString("N"));
	        Directory.CreateDirectory(root);

	        try
	        {
	            var sqlPath = Path.Combine(root, "CreateRole.sql");
	            var script = @"CREATE ROLE [ReportingRole];";

	            await File.WriteAllTextAsync(sqlPath, script);

	            var builder = new FileModelBuilder();
	            var folder = new ProjectFolder { RootPath = root };

	            // Act
	            var cache = await builder.BuildCacheAsync(Guid.NewGuid(), folder);

	            // Assert
	            var entry = Assert.Single(cache.FileEntries).Value;
	            Assert.Equal(SqlObjectType.Role, entry.ObjectType);
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
	    public async Task BuildCacheAsync_Classifies_User_Script_As_User()
	    {
	        // Arrange
	        var root = Path.Combine(Path.GetTempPath(), "user_" + Guid.NewGuid().ToString("N"));
	        Directory.CreateDirectory(root);

	        try
	        {
	            var sqlPath = Path.Combine(root, "CreateUser.sql");
	            var script = @"CREATE USER [TestUser] FOR LOGIN [TestLogin];";

	            await File.WriteAllTextAsync(sqlPath, script);

	            var builder = new FileModelBuilder();
	            var folder = new ProjectFolder { RootPath = root };

	            // Act
	            var cache = await builder.BuildCacheAsync(Guid.NewGuid(), folder);

	            // Assert
	            var entry = Assert.Single(cache.FileEntries).Value;
	            Assert.Equal(SqlObjectType.User, entry.ObjectType);
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
	    public async Task BuildCacheAsync_Classifies_Unknown_When_No_Recognized_Ddl()
	    {
	        // Arrange
	        var root = Path.Combine(Path.GetTempPath(), "unknown_" + Guid.NewGuid().ToString("N"));
	        Directory.CreateDirectory(root);

	        try
	        {
	            var sqlPath = Path.Combine(root, "MiscScript.sql");
	            var script = @"PRINT 'Hello world';";

	            await File.WriteAllTextAsync(sqlPath, script);

	            var builder = new FileModelBuilder();
	            var folder = new ProjectFolder { RootPath = root };

	            // Act
	            var cache = await builder.BuildCacheAsync(Guid.NewGuid(), folder);
	            var entry = Assert.Single(cache.FileEntries).Value;

	            // Assert
	            Assert.Equal(SqlObjectType.Unknown, entry.ObjectType);
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