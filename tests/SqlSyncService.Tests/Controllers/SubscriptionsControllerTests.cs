using System.Net;
using System.Net.Http.Json;
using LiteDB;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SqlSyncService.Contracts;
using SqlSyncService.Contracts.Subscriptions;

namespace SqlSyncService.Tests.Controllers;

public class SubscriptionsControllerTests : IClassFixture<WebApplicationFactory<Program>>
{
	private readonly WebApplicationFactory<Program> _factory;

	public SubscriptionsControllerTests(WebApplicationFactory<Program> factory)
	{
		_factory = factory;
	}

	private WebApplicationFactory<Program> CreateFactoryWithInMemoryLiteDb()
	{
		return _factory.WithWebHostBuilder(builder =>
		{
			builder.ConfigureServices(services =>
			{
				services.AddSingleton<ILiteDatabase>(_ => new LiteDatabase(new MemoryStream()));
			});
		});
	}

	[Fact]
	public async Task GetSubscriptions_Returns_Empty_List_When_No_Subscriptions()
	{
		// Arrange
		var factory = CreateFactoryWithInMemoryLiteDb();
		using var client = factory.CreateClient();

		// Act
		var response = await client.GetAsync("/api/subscriptions");

		// Assert
		response.EnsureSuccessStatusCode();

		var body = await response.Content.ReadFromJsonAsync<GetSubscriptionsResponse>();
		Assert.NotNull(body);
		Assert.Equal(0, body!.TotalCount);
		Assert.Empty(body.Subscriptions);
	}

	[Fact]
	public async Task Create_Then_GetById_Roundtrips_Subscription()
	{
		// Arrange
		var factory = CreateFactoryWithInMemoryLiteDb();
		using var client = factory.CreateClient();

		var createRequest = new CreateSubscriptionRequest
		{
			Name = "Test subscription",
			Database = new CreateSubscriptionDatabaseConfig
			{
				Server = "localhost",
				Database = "TestDb",
				AuthType = "windows"
			},
			Project = new CreateSubscriptionProjectConfig
			{
				Path = "C:/projects/test",
				Structure = "by-type"
			},
			Options = new CreateSubscriptionOptionsConfig
			{
				AutoCompare = true,
				CompareOnFileChange = false,
				CompareOnDatabaseChange = false,
				IgnoreWhitespace = true,
				IgnoreComments = false,
				ObjectTypes = new[] { "table", "view" }
			}
		};

		// Act - create
		var createResponse = await client.PostAsJsonAsync("/api/subscriptions", createRequest);

		// Assert - creation
		Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
		Assert.NotNull(createResponse.Headers.Location);
		Assert.Contains("/api/subscriptions/", createResponse.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);

		var created = await createResponse.Content.ReadFromJsonAsync<SubscriptionDetailResponse>();
		Assert.NotNull(created);
		Assert.Equal(createRequest.Name, created!.Name);
		Assert.Equal("active", created.State);
		Assert.Equal(createRequest.Database.Server, created.Database.Server);
		Assert.Equal(createRequest.Project.Path, created.Project.Path);

		// Act - get by id
		var getResponse = await client.GetAsync($"/api/subscriptions/{created.Id}");
		getResponse.EnsureSuccessStatusCode();

		var loaded = await getResponse.Content.ReadFromJsonAsync<SubscriptionDetailResponse>();
		Assert.NotNull(loaded);
		Assert.Equal(created.Id, loaded!.Id);
		Assert.Equal(created.Name, loaded.Name);
	}

	[Fact]
	public async Task GetSubscriptions_Filters_By_State_Active()
	{
		// Arrange
		var factory = CreateFactoryWithInMemoryLiteDb();
		using var client = factory.CreateClient();

		var activeRequest = new CreateSubscriptionRequest
		{
			Name = "Active sub",
			Database = new CreateSubscriptionDatabaseConfig
			{
				Server = "localhost",
				Database = "Db1",
				AuthType = "windows"
			},
			Project = new CreateSubscriptionProjectConfig
			{
				Path = "C:/projects/active",
				Structure = "by-type"
			},
			Options = new CreateSubscriptionOptionsConfig()
		};

		var pausedRequest = new CreateSubscriptionRequest
		{
			Name = "Paused sub",
			Database = new CreateSubscriptionDatabaseConfig
			{
				Server = "localhost",
				Database = "Db2",
				AuthType = "windows"
			},
			Project = new CreateSubscriptionProjectConfig
			{
				Path = "C:/projects/paused",
				Structure = "by-type"
			},
			Options = new CreateSubscriptionOptionsConfig()
		};

		var activeResponse = await client.PostAsJsonAsync("/api/subscriptions", activeRequest);
		activeResponse.EnsureSuccessStatusCode();
		var active = await activeResponse.Content.ReadFromJsonAsync<SubscriptionDetailResponse>();
		Assert.NotNull(active);

		var pausedResponse = await client.PostAsJsonAsync("/api/subscriptions", pausedRequest);
		pausedResponse.EnsureSuccessStatusCode();
		var paused = await pausedResponse.Content.ReadFromJsonAsync<SubscriptionDetailResponse>();
		Assert.NotNull(paused);

		// Pause the second subscription
		var pauseResponse = await client.PostAsync($"/api/subscriptions/{paused!.Id}/pause", content: null);
		pauseResponse.EnsureSuccessStatusCode();

		// Act - filter by state=active
		var response = await client.GetAsync("/api/subscriptions?state=active");
		response.EnsureSuccessStatusCode();

		var body = await response.Content.ReadFromJsonAsync<GetSubscriptionsResponse>();
		Assert.NotNull(body);
		Assert.Equal(1, body!.TotalCount);
		Assert.Single(body.Subscriptions);
		Assert.Equal(active!.Id, body.Subscriptions[0].Id);
		Assert.Equal("active", body.Subscriptions[0].State);
	}

	[Fact]
	public async Task CreateSubscription_Returns_409_For_Duplicate_Name()
	{
		// Arrange
		var factory = CreateFactoryWithInMemoryLiteDb();
		using var client = factory.CreateClient();

		var request = new CreateSubscriptionRequest
		{
			Name = "Duplicate sub",
			Database = new CreateSubscriptionDatabaseConfig
			{
				Server = "localhost",
				Database = "Db1",
				AuthType = "windows"
			},
			Project = new CreateSubscriptionProjectConfig
			{
				Path = "C:/projects/dup",
				Structure = "by-type"
			},
			Options = new CreateSubscriptionOptionsConfig()
		};

		// First create succeeds
		var first = await client.PostAsJsonAsync("/api/subscriptions", request);
		first.EnsureSuccessStatusCode();

		// Act - second create with same name
		var second = await client.PostAsJsonAsync("/api/subscriptions", request);

		// Assert
		Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

		var error = await second.Content.ReadFromJsonAsync<ErrorResponse>();
		Assert.NotNull(error);
		Assert.Equal(ErrorCodes.Conflict, error!.Error.Code);
		Assert.Equal("name", error.Error.Field);
	}

	[Fact]
	public async Task DeleteSubscription_Removes_Subscription_And_Returns_204()
	{
		// Arrange
		var factory = CreateFactoryWithInMemoryLiteDb();
		using var client = factory.CreateClient();

		var createRequest = new CreateSubscriptionRequest
		{
			Name = "To delete",
			Database = new CreateSubscriptionDatabaseConfig
			{
				Server = "localhost",
				Database = "Db1",
				AuthType = "windows"
			},
			Project = new CreateSubscriptionProjectConfig
			{
				Path = "C:/projects/delete",
				Structure = "by-type"
			},
			Options = new CreateSubscriptionOptionsConfig()
		};

		var createResponse = await client.PostAsJsonAsync("/api/subscriptions", createRequest);
		createResponse.EnsureSuccessStatusCode();
		var created = await createResponse.Content.ReadFromJsonAsync<SubscriptionDetailResponse>();
		Assert.NotNull(created);

		// Act - delete
		var deleteResponse = await client.DeleteAsync($"/api/subscriptions/{created!.Id}?deleteHistory=true");

		// Assert
		Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

		// Subsequent GET should return 404
		var getResponse = await client.GetAsync($"/api/subscriptions/{created.Id}");
		Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
	}

	[Fact]
	public async Task Pause_And_Resume_Subscription_Succeeds()
	{
		// Arrange
		var factory = CreateFactoryWithInMemoryLiteDb();
		using var client = factory.CreateClient();

		var createRequest = new CreateSubscriptionRequest
		{
			Name = "PauseResume sub",
			Database = new CreateSubscriptionDatabaseConfig
			{
				Server = "localhost",
				Database = "Db1",
				AuthType = "windows"
			},
			Project = new CreateSubscriptionProjectConfig
			{
				Path = "C:/projects/pause-resume",
				Structure = "by-type"
			},
			Options = new CreateSubscriptionOptionsConfig()
		};

		var createResponse = await client.PostAsJsonAsync("/api/subscriptions", createRequest);
		createResponse.EnsureSuccessStatusCode();
		var created = await createResponse.Content.ReadFromJsonAsync<SubscriptionDetailResponse>();
		Assert.NotNull(created);

		// Act - pause
		var pauseResponse = await client.PostAsync($"/api/subscriptions/{created!.Id}/pause", content: null);
		pauseResponse.EnsureSuccessStatusCode();

		var paused = await pauseResponse.Content.ReadFromJsonAsync<SubscriptionPauseResponse>();
		Assert.NotNull(paused);
		Assert.Equal("paused", paused!.State);
		Assert.Equal(created.Id, paused.Id);

		// Act - resume
		var resumeResponse = await client.PostAsync($"/api/subscriptions/{created.Id}/resume", content: null);
		resumeResponse.EnsureSuccessStatusCode();

		var resumed = await resumeResponse.Content.ReadFromJsonAsync<SubscriptionResumeResponse>();
		Assert.NotNull(resumed);
		Assert.Equal("active", resumed!.State);
		Assert.Equal(created.Id, resumed.Id);
	}

	[Fact]
	public async Task Resume_Returns_Conflict_When_Subscription_Not_Paused()
	{
		// Arrange
		var factory = CreateFactoryWithInMemoryLiteDb();
		using var client = factory.CreateClient();

		var createRequest = new CreateSubscriptionRequest
		{
			Name = "Not paused",
			Database = new CreateSubscriptionDatabaseConfig
			{
				Server = "localhost",
				Database = "Db1",
				AuthType = "windows"
			},
			Project = new CreateSubscriptionProjectConfig
			{
				Path = "C:/projects/not-paused",
				Structure = "by-type"
			},
			Options = new CreateSubscriptionOptionsConfig()
		};

		var createResponse = await client.PostAsJsonAsync("/api/subscriptions", createRequest);
		createResponse.EnsureSuccessStatusCode();
		var created = await createResponse.Content.ReadFromJsonAsync<SubscriptionDetailResponse>();
		Assert.NotNull(created);

		// Act - attempt to resume without pausing first
		var resumeResponse = await client.PostAsync($"/api/subscriptions/{created!.Id}/resume", content: null);

		// Assert
		Assert.Equal(HttpStatusCode.Conflict, resumeResponse.StatusCode);

		var error = await resumeResponse.Content.ReadFromJsonAsync<ErrorResponse>();
		Assert.NotNull(error);
		Assert.Equal(ErrorCodes.Conflict, error!.Error.Code);
		Assert.Equal("state", error.Error.Field);
	}

	[Fact]
	public async Task GetSubscriptions_Returns_Validation_Error_For_Invalid_State_Filter()
	{
		// Arrange
		var factory = CreateFactoryWithInMemoryLiteDb();
		using var client = factory.CreateClient();

		// Act
		var response = await client.GetAsync("/api/subscriptions?state=not-a-valid-state");

		// Assert
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

		var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
		Assert.NotNull(error);
		Assert.Equal(ErrorCodes.ValidationError, error!.Error.Code);
		Assert.Equal("state", error.Error.Field);
	}
}
