using LiteDB;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SqlSyncService.ChangeDetection;
using SqlSyncService.Configuration;
using SqlSyncService.Contracts;
using SqlSyncService.Middleware;
using SqlSyncService.Persistence;
using SqlSyncService.Realtime;
using SqlSyncService.Services;
using SqlSyncService.DacFx;
using SqlSyncService.Workers;
using System.IO;
using System.Linq;

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

// Bind and validate LiteDB configuration
builder.Services
    .AddOptions<LiteDbOptions>()
    .Bind(builder.Configuration.GetSection("LiteDb"))
    .ValidateDataAnnotations()
    .Validate(
        options => !string.IsNullOrWhiteSpace(options.DatabasePath),
        "LiteDb.DatabasePath must be provided.")
    .ValidateOnStart();

// Register LiteDB and persistence layer
builder.Services.AddSingleton<ILiteDatabase>(sp =>
{
    var options = sp.GetRequiredService<IOptions<LiteDbOptions>>().Value;

    var databasePath = options.DatabasePath;
    if (string.IsNullOrWhiteSpace(databasePath))
    {
        throw new InvalidOperationException("LiteDb.DatabasePath configuration is required.");
    }

    var directory = Path.GetDirectoryName(databasePath);
    if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
    {
        Directory.CreateDirectory(directory);
    }

    // Use shared mode to allow multiple processes/threads to access the database
    var connectionString = $"Filename={databasePath};Connection=Shared";
    return new LiteDatabase(connectionString);
});

builder.Services.AddSingleton<LiteDbContext>();
builder.Services.AddScoped<ISubscriptionRepository, SubscriptionRepository>();
builder.Services.AddScoped<ISchemaSnapshotRepository, SchemaSnapshotRepository>();
builder.Services.AddScoped<IComparisonHistoryRepository, ComparisonHistoryRepository>();
builder.Services.AddScoped<IPendingChangeRepository, PendingChangeRepository>();

// Add services to the container.
builder.Services
    .AddControllers()
    .ConfigureApiBehaviorOptions(options =>
    {
        options.InvalidModelStateResponseFactory = context =>
        {
            var firstError = context.ModelState
                .Where(kvp => kvp.Value?.Errors.Count > 0)
                .Select(kvp => new
                {
                    Field = kvp.Key,
                    Error = kvp.Value!.Errors.First().ErrorMessage
                })
                .FirstOrDefault();

            var errorDetail = new ErrorDetail
            {
                Code = ErrorCodes.ValidationError,
                Message = "The request contains invalid data.",
                Details = firstError?.Error,
                Field = string.IsNullOrWhiteSpace(firstError?.Field) ? null : firstError!.Field,
                TraceId = context.HttpContext.TraceIdentifier,
                Timestamp = DateTime.UtcNow
            };

            var response = new ErrorResponse { Error = errorDetail };

            return new BadRequestObjectResult(response);
        };
    });

builder.Services.AddSignalR();
builder.Services.AddHealthChecks();

// Application services
builder.Services.AddScoped<IDatabaseConnectionTester, DatabaseConnectionTester>();
builder.Services.AddScoped<IFolderValidator, FolderValidator>();
builder.Services.AddScoped<ISubscriptionService, SubscriptionService>();
builder.Services.AddScoped<IDatabaseModelBuilder, DatabaseModelBuilder>();
builder.Services.AddScoped<IFileModelBuilder, FileModelBuilder>();
builder.Services.AddScoped<ISchemaComparer, SchemaComparer>();
builder.Services.AddScoped<IComparisonOrchestrator, ComparisonOrchestrator>();

// Change detection components
builder.Services.AddSingleton<IChangeDebouncer, ChangeDebouncer>();
builder.Services.AddScoped<IChangeProcessor, ChangeProcessor>();

// Background workers
builder.Services.AddHostedService<ChangeDetectionCoordinator>();
builder.Services.AddHostedService<HealthCheckWorker>();
builder.Services.AddHostedService<CacheCleanupWorker>();
builder.Services.AddHostedService<FileWatchingWorker>();
builder.Services.AddHostedService<DatabasePollingWorker>();
builder.Services.AddHostedService<ReconciliationWorker>();

// Swagger/OpenAPI
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Global exception handling
app.UseMiddleware<GlobalExceptionHandlingMiddleware>();

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
