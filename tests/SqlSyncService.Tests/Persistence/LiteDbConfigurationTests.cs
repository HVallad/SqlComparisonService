using LiteDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SqlSyncService.Tests.Persistence;

public class LiteDbConfigurationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public LiteDbConfigurationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public void LiteDb_File_Is_Created_At_Configured_Path_On_Service_Start()
    {
        // Arrange
        var tempFilePath = Path.Combine(Path.GetTempPath(), $"sqlsync_{Guid.NewGuid():N}.db");
        if (File.Exists(tempFilePath))
        {
            File.Delete(tempFilePath);
        }

        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((context, config) =>
            {
                var overrides = new Dictionary<string, string?>
                {
                    ["LiteDb:DatabasePath"] = tempFilePath
                };
                config.AddInMemoryCollection(overrides!);
            });
        });

        // Act - resolving ILiteDatabase forces the LiteDB singleton to be created
        using (var scope = factory.Services.CreateScope())
        {
            _ = scope.ServiceProvider.GetRequiredService<ILiteDatabase>();
        }

        // Assert
        Assert.True(File.Exists(tempFilePath));
    }
}

