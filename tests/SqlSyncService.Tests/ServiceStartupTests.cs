using System.IO;
using System.Net.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace SqlSyncService.Tests;

public class ServiceStartupTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ServiceStartupTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Service_Starts_And_Responds_To_Health_Request()
    {
        // Arrange
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            // Use a clear base address for readability; TestServer hosts in-memory
            BaseAddress = new Uri("http://localhost:5050")
        });

        // Act
        var response = await client.GetAsync("/api/health");

        // Assert
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public void Configuration_Uses_Expected_Http_Port_5050()
    {
        // Arrange
        var solutionRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var configPath = Path.Combine(solutionRoot, "src", "SqlSyncService", "appsettings.json");

        var config = new ConfigurationBuilder()
            .AddJsonFile(configPath, optional: false, reloadOnChange: false)
            .Build();

        // Act
        var httpPort = config.GetSection("Service:Server:HttpPort").Get<int>();

        // Assert
        Assert.Equal(5050, httpPort);
    }
}

