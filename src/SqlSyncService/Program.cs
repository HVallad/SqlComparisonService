using SqlSyncService.Realtime;

var builder = WebApplication.CreateBuilder(args);

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
