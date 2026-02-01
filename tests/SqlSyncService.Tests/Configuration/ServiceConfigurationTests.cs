using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SqlSyncService.Configuration;
using Xunit;

namespace SqlSyncService.Tests.Configuration;

public class ServiceConfigurationTests
{
    [Fact]
    public void AppSettings_Configuration_Binds_And_Validates_Successfully()
    {
        // Arrange
        var solutionRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var configPath = Path.Combine(solutionRoot, "src", "SqlSyncService", "appsettings.json");

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(configPath, optional: false, reloadOnChange: false)
            .Build();

        using var provider = BuildServiceProvider(configuration);

        // Act
        var options = provider.GetRequiredService<IOptions<ServiceConfiguration>>();
        var value = options.Value;

        // Assert
        Assert.Equal(5050, value.Server.HttpPort);
        Assert.Equal(5051, value.Server.WebSocketPort);
        Assert.False(value.Server.EnableHttps);
        Assert.Equal("./cache", value.Cache.CacheDirectory);
        Assert.Equal("Information", value.Logging.MinimumLevel);
    }

    [Fact]
    public void Invalid_Port_Fails_DataAnnotations_Validation()
    {
        // Arrange
        var inMemorySettings = new Dictionary<string, string?>
        {
            ["Service:Server:HttpPort"] = "80", // below allowed range 1024-65535
            ["Service:Server:WebSocketPort"] = "5051",
            ["Service:Cache:CacheDirectory"] = "./cache"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings!)
            .Build();

        using var provider = BuildServiceProvider(configuration);

        var options = provider.GetRequiredService<IOptions<ServiceConfiguration>>();

        // Act & Assert
        Assert.Throws<OptionsValidationException>(() =>
        {
            var _ = options.Value;
        });
    }

    private static ServiceProvider BuildServiceProvider(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services
            .AddOptions<ServiceConfiguration>()
            .Bind(configuration.GetSection("Service"))
            .ValidateDataAnnotations()
            .Validate(
                config =>
                    config.Server.HttpPort is >= 1024 and <= 65535 &&
                    config.Server.WebSocketPort is >= 1024 and <= 65535 &&
                    config.Monitoring.MaxConcurrentComparisons is >= 1 and <= 32 &&
                    config.Cache.MaxCachedSnapshots is >= 1 and <= 100,
                "Service configuration contains out-of-range values.");

        return services.BuildServiceProvider(validateScopes: true);
    }
}

