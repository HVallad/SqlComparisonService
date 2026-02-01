using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using SqlSyncService.Contracts;

namespace SqlSyncService.Tests.Middleware;

public class GlobalExceptionHandlingTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public GlobalExceptionHandlingTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Unhandled_Exception_Is_Translated_To_Internal_Error_Response()
    {
        // Arrange
        using var client = _factory.CreateClient();

        // Act
        var response = await client.GetAsync("/api/diagnostics/throw");

        // Assert
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var payload = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.NotNull(payload);

        var error = payload!.Error;
        Assert.Equal(ErrorCodes.InternalError, error.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.TraceId));
    }
}
