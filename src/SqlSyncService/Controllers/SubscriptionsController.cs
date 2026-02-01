using Microsoft.AspNetCore.Mvc;
using SqlSyncService.Contracts;
using SqlSyncService.Contracts.Subscriptions;
using SqlSyncService.Domain.Comparisons;
using SqlSyncService.Domain.Subscriptions;
using SqlSyncService.Persistence;
using SqlSyncService.Services;
using System.Linq;

namespace SqlSyncService.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class SubscriptionsController : ControllerBase
{
	private readonly ISubscriptionService _subscriptionService;
	private readonly IComparisonHistoryRepository _historyRepository;

	public SubscriptionsController(
		ISubscriptionService subscriptionService,
		IComparisonHistoryRepository historyRepository)
	{
		_subscriptionService = subscriptionService;
		_historyRepository = historyRepository;
	}

	[HttpGet]
	public async Task<ActionResult<GetSubscriptionsResponse>> GetAll(
		[FromQuery] string? state,
		CancellationToken cancellationToken)
	{
		SubscriptionState? stateFilter = null;

		if (!string.IsNullOrWhiteSpace(state))
		{
			if (!Enum.TryParse<SubscriptionState>(state, ignoreCase: true, out var parsed))
			{
				var error = new ErrorDetail
				{
					Code = ErrorCodes.ValidationError,
					Message = $"Invalid subscription state '{state}'.",
					Field = "state",
					TraceId = HttpContext.TraceIdentifier,
					Timestamp = DateTime.UtcNow
				};

				return BadRequest(new ErrorResponse { Error = error });
			}

			stateFilter = parsed;
		}

		var subscriptions = await _subscriptionService
			.GetAllAsync(stateFilter, cancellationToken)
			.ConfigureAwait(false);

		var response = new GetSubscriptionsResponse
		{
			TotalCount = subscriptions.Count
		};

		foreach (var subscription in subscriptions)
		{
			var history = await _historyRepository
				.GetBySubscriptionAsync(subscription.Id, maxCount: 1, cancellationToken)
				.ConfigureAwait(false);

			var last = history.FirstOrDefault();

			var item = new SubscriptionListItemResponse
			{
				Id = subscription.Id,
				Name = subscription.Name,
				State = MapState(subscription.State),
				Database = new SubscriptionDatabaseSummaryResponse
				{
					Server = subscription.Database.Server,
					Database = subscription.Database.Database,
					DisplayName = BuildDatabaseDisplayName(subscription.Database.Server, subscription.Database.Database)
				},
				Project = new SubscriptionProjectSummaryResponse
				{
					Path = subscription.Project.RootPath,
					SqlFileCount = 0
				},
				LastComparedAt = last?.ComparedAt,
				DifferenceCount = last?.Summary.TotalDifferences ?? 0,
				Health = new SubscriptionHealthSummaryResponse
				{
					Database = "unknown",
					FileSystem = "unknown"
				}
			};

			response.Subscriptions.Add(item);
		}

		return Ok(response);
	}

	[HttpGet("{id:guid}")]
	public async Task<ActionResult<SubscriptionDetailResponse>> GetById(
		Guid id,
		CancellationToken cancellationToken)
	{
		var subscription = await _subscriptionService
			.GetByIdAsync(id, cancellationToken)
			.ConfigureAwait(false);

		if (subscription is null)
		{
			var error = new ErrorDetail
			{
				Code = ErrorCodes.NotFound,
				Message = $"Subscription '{id}' was not found.",
				TraceId = HttpContext.TraceIdentifier,
				Timestamp = DateTime.UtcNow
			};

			return NotFound(new ErrorResponse { Error = error });
		}

		var history = await _historyRepository
			.GetBySubscriptionAsync(id, maxCount: null, cancellationToken)
			.ConfigureAwait(false);

		var last = history.FirstOrDefault();
		var totalComparisons = history.Count;

		var response = MapToDetailResponse(subscription, last, totalComparisons);
		return Ok(response);
	}

	[HttpPost]
	public async Task<ActionResult<SubscriptionDetailResponse>> Create(
		[FromBody] CreateSubscriptionRequest request,
		CancellationToken cancellationToken)
	{
		var subscription = await _subscriptionService
			.CreateAsync(request, cancellationToken)
			.ConfigureAwait(false);

		var response = MapToDetailResponse(subscription, lastComparison: null, totalComparisons: 0);

		return CreatedAtAction(
			nameof(GetById),
			new { id = subscription.Id },
			response);
	}

	[HttpPut("{id:guid}")]
	public async Task<ActionResult<SubscriptionDetailResponse>> Update(
		Guid id,
		[FromBody] UpdateSubscriptionRequest request,
		CancellationToken cancellationToken)
	{
		var subscription = await _subscriptionService
			.UpdateAsync(id, request, cancellationToken)
			.ConfigureAwait(false);

		var history = await _historyRepository
			.GetBySubscriptionAsync(id, maxCount: null, cancellationToken)
			.ConfigureAwait(false);

		var last = history.FirstOrDefault();
		var totalComparisons = history.Count;

		var response = MapToDetailResponse(subscription, last, totalComparisons);
		return Ok(response);
	}

	[HttpDelete("{id:guid}")]
	public async Task<IActionResult> Delete(
		Guid id,
		[FromQuery] bool deleteHistory,
		CancellationToken cancellationToken)
	{
		var deleted = await _subscriptionService
			.DeleteAsync(id, deleteHistory, cancellationToken)
			.ConfigureAwait(false);

		if (!deleted)
		{
			var error = new ErrorDetail
			{
				Code = ErrorCodes.NotFound,
				Message = $"Subscription '{id}' was not found.",
				TraceId = HttpContext.TraceIdentifier,
				Timestamp = DateTime.UtcNow
			};

			return NotFound(new ErrorResponse { Error = error });
		}

		return NoContent();
	}

	[HttpPost("{id:guid}/pause")]
	public async Task<ActionResult<SubscriptionPauseResponse>> Pause(
		Guid id,
		CancellationToken cancellationToken)
	{
		var subscription = await _subscriptionService
			.PauseAsync(id, cancellationToken)
			.ConfigureAwait(false);

		var response = new SubscriptionPauseResponse
		{
			Id = subscription.Id,
			State = MapState(subscription.State),
			PausedAt = subscription.PausedAt ?? subscription.UpdatedAt
		};

		return Ok(response);
	}

	[HttpPost("{id:guid}/resume")]
	public async Task<ActionResult<SubscriptionResumeResponse>> Resume(
		Guid id,
		CancellationToken cancellationToken)
	{
		var subscription = await _subscriptionService
			.ResumeAsync(id, cancellationToken)
			.ConfigureAwait(false);

		var response = new SubscriptionResumeResponse
		{
			Id = subscription.Id,
			State = MapState(subscription.State),
			ResumedAt = subscription.ResumedAt ?? subscription.UpdatedAt
		};

		return Ok(response);
	}

	private static SubscriptionDetailResponse MapToDetailResponse(
		Subscription subscription,
		ComparisonResult? lastComparison,
		int totalComparisons)
	{
		var response = new SubscriptionDetailResponse
		{
			Id = subscription.Id,
			Name = subscription.Name,
			State = MapState(subscription.State),
			CreatedAt = subscription.CreatedAt,
			UpdatedAt = subscription.UpdatedAt,
			Database = new SubscriptionDatabaseDetailResponse
			{
				Server = subscription.Database.Server,
				Database = subscription.Database.Database,
				AuthType = MapAuthType(subscription.Database.AuthType),
				DisplayName = BuildDatabaseDisplayName(subscription.Database.Server, subscription.Database.Database)
			},
			Project = new SubscriptionProjectDetailResponse
			{
				Path = subscription.Project.RootPath,
				IncludePatterns = subscription.Project.IncludePatterns,
				ExcludePatterns = subscription.Project.ExcludePatterns,
				Structure = MapFolderStructure(subscription.Project.Structure),
				SqlFileCount = 0
			},
			Options = new SubscriptionOptionsResponse
			{
				AutoCompare = subscription.Options.AutoCompare,
				CompareOnFileChange = subscription.Options.CompareOnFileChange,
				CompareOnDatabaseChange = subscription.Options.CompareOnDatabaseChange,
				ObjectTypes = MapObjectTypes(subscription.Options),
				IgnoreWhitespace = subscription.Options.IgnoreWhitespace,
				IgnoreComments = subscription.Options.IgnoreComments
			},
			Health = new SubscriptionHealthDetailResponse
			{
				Database = new SubscriptionHealthCheckResponse
				{
					Status = "unknown",
					LastChecked = null
				},
				FileSystem = new SubscriptionHealthCheckResponse
				{
					Status = "unknown",
					LastChecked = null
				}
			},
			Statistics = new SubscriptionStatisticsResponse
			{
				TotalComparisons = totalComparisons
			}
		};

		if (lastComparison is not null)
		{
			response.LastComparison = new SubscriptionLastComparisonResponse
			{
				Id = lastComparison.Id,
				ComparedAt = lastComparison.ComparedAt,
				Duration = System.Xml.XmlConvert.ToString(lastComparison.Duration),
				DifferenceCount = lastComparison.Summary.TotalDifferences
			};
		}

		return response;
	}

	private static string MapState(SubscriptionState state)
		=> state.ToString().ToLowerInvariant();

	private static string BuildDatabaseDisplayName(string server, string database)
	{
		if (string.IsNullOrWhiteSpace(server) && string.IsNullOrWhiteSpace(database))
		{
			return string.Empty;
		}

		if (string.IsNullOrWhiteSpace(server))
		{
			return database;
		}

		if (string.IsNullOrWhiteSpace(database))
		{
			return server;
		}

		return $"{server}.{database}";
	}

	private static string MapAuthType(AuthenticationType authType)
	{
		return authType switch
		{
			AuthenticationType.SqlServer => "sql",
			AuthenticationType.AzureAD => "azuread",
			AuthenticationType.AzureADInteractive => "azuread-interactive",
			_ => "windows"
		};
	}

	private static string MapFolderStructure(FolderStructure structure)
	{
		return structure switch
		{
			FolderStructure.Flat => "flat",
			FolderStructure.BySchema => "by-schema",
			FolderStructure.BySchemaAndType => "by-schema-and-type",
			_ => "by-type"
		};
	}

	private static string[] MapObjectTypes(ComparisonOptions options)
	{
		var types = new List<string>();

		if (options.IncludeTables)
		{
			types.Add("table");
		}

		if (options.IncludeViews)
		{
			types.Add("view");
		}

		if (options.IncludeStoredProcedures)
		{
			types.Add("stored-procedure");
		}

		if (options.IncludeFunctions)
		{
			types.Add("function");
		}

		if (options.IncludeTriggers)
		{
			types.Add("trigger");
		}

		return types.ToArray();
	}
}
