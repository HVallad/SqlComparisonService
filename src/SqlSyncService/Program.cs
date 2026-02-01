using SqlSyncService.Configuration;
using SqlSyncService.Realtime;

var builder = WebApplication.CreateBuilder(args);

// Bind and validate service configuration
builder.Services
    .AddOptions<ServiceConfiguration>()
    .Bind(builder.Configuration.GetSection("Service"))
    .ValidateDataAnnotations()
    .Validate(
        config =>
            config.Server.HttpPort is >= 1024 and <= 65535 &&
            config.Server.WebSocketPort is >= 1024 and <= 65535 &&
            config.Monitoring.MaxConcurrentComparisons is >= 1 and <= 32 &&
            config.Cache.MaxCachedSnapshots is >= 1 and <= 100,
        "Service configuration contains out-of-range values.")
    .ValidateOnStart();

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddHealthChecks();

// Swagger/OpenAPI
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
	app.UseSwagger();
	app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthorization();

// Map endpoints
app.MapControllers();
app.MapHealthChecks("/health");
app.MapHub<SyncHub>("/hubs/sync");

app.Run();

// Expose Program class for WebApplicationFactory<Program> in tests
public partial class Program
{
}
