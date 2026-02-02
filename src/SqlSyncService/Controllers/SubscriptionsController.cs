using Microsoft.AspNetCore.Mvc;
using SqlSyncService.Contracts;
using SqlSyncService.Contracts.Comparisons;
using SqlSyncService.Contracts.Folders;
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
		private readonly IComparisonOrchestrator _comparisonOrchestrator;
		private readonly IFolderValidator _folderValidator;

	public SubscriptionsController(
			ISubscriptionService subscriptionService,
			IComparisonHistoryRepository historyRepository,
			IComparisonOrchestrator comparisonOrchestrator,
			IFolderValidator folderValidator)
	{
		_subscriptionService = subscriptionService;
		_historyRepository = historyRepository;
			_comparisonOrchestrator = comparisonOrchestrator;
			_folderValidator = folderValidator;
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

				var folderValidation = await _folderValidator
					.ValidateFolderAsync(
						new ValidateFolderRequest
						{
							Path = subscription.Project.RootPath,
							IncludePatterns = subscription.Project.IncludePatterns,
							ExcludePatterns = subscription.Project.ExcludePatterns
						},
						cancellationToken)
					.ConfigureAwait(false);

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
						SqlFileCount = folderValidation.SqlFileCount
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

			var folderValidation = await _folderValidator
				.ValidateFolderAsync(
					new ValidateFolderRequest
					{
						Path = subscription.Project.RootPath,
						IncludePatterns = subscription.Project.IncludePatterns,
						ExcludePatterns = subscription.Project.ExcludePatterns
					},
					cancellationToken)
				.ConfigureAwait(false);
			
			var response = MapToDetailResponse(subscription, last, totalComparisons, folderValidation.SqlFileCount);
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

			var folderValidation = await _folderValidator
				.ValidateFolderAsync(
					new ValidateFolderRequest
					{
						Path = subscription.Project.RootPath,
						IncludePatterns = subscription.Project.IncludePatterns,
						ExcludePatterns = subscription.Project.ExcludePatterns
					},
					cancellationToken)
				.ConfigureAwait(false);
			
			var response = MapToDetailResponse(subscription, lastComparison: null, totalComparisons: 0, sqlFileCount: folderValidation.SqlFileCount);

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

			var folderValidation = await _folderValidator
				.ValidateFolderAsync(
					new ValidateFolderRequest
					{
						Path = subscription.Project.RootPath,
						IncludePatterns = subscription.Project.IncludePatterns,
						ExcludePatterns = subscription.Project.ExcludePatterns
					},
					cancellationToken)
				.ConfigureAwait(false);
			
			var response = MapToDetailResponse(subscription, last, totalComparisons, folderValidation.SqlFileCount);
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

		[HttpPost("{id:guid}/compare")]
		public async Task<ActionResult<TriggerComparisonResponse>> TriggerComparison(
			Guid id,
			[FromBody] TriggerComparisonRequest? request,
			CancellationToken cancellationToken)
		{
			var fullComparison = request?.ForceFullComparison ?? false;

			var result = await _comparisonOrchestrator
				.RunComparisonAsync(id, fullComparison, cancellationToken)
				.ConfigureAwait(false);

			var startedAt = result.ComparedAt - result.Duration;

			var response = new TriggerComparisonResponse
			{
				ComparisonId = result.Id,
				SubscriptionId = result.SubscriptionId,
				Status = MapComparisonStatus(result.Status),
				QueuedAt = startedAt,
				EstimatedDuration = System.Xml.XmlConvert.ToString(result.Duration)
			};

			return Accepted(response);
		}

		[HttpGet("{id:guid}/comparisons")]
		public async Task<ActionResult<GetSubscriptionComparisonsResponse>> GetComparisons(
			Guid id,
			[FromQuery] string? status,
			[FromQuery] int? limit,
			[FromQuery] int? offset,
			CancellationToken cancellationToken)
		{
			var subscription = await _subscriptionService
				.GetByIdAsync(id, cancellationToken)
				.ConfigureAwait(false);

			if (subscription is null)
			{
				var notFound = new ErrorDetail
				{
					Code = ErrorCodes.NotFound,
					Message = $"Subscription '{id}' was not found.",
					TraceId = HttpContext.TraceIdentifier,
					Timestamp = DateTime.UtcNow
				};

				return NotFound(new ErrorResponse { Error = notFound });
			}

			Func<ComparisonResult, bool> predicate = _ => true;
			if (!string.IsNullOrWhiteSpace(status))
			{
				var normalized = status.Trim().ToLowerInvariant();
				switch (normalized)
				{
					case "completed":
						predicate = r => r.Status != ComparisonStatus.Error;
						break;
					case "failed":
						predicate = r => r.Status == ComparisonStatus.Error;
						break;
					default:
						var error = new ErrorDetail
						{
							Code = ErrorCodes.ValidationError,
							Message = $"Invalid comparison status '{status}'.",
							Field = "status",
							TraceId = HttpContext.TraceIdentifier,
							Timestamp = DateTime.UtcNow
						};

						return BadRequest(new ErrorResponse { Error = error });
				}
			}

			var all = await _historyRepository
				.GetBySubscriptionAsync(id, maxCount: null, cancellationToken)
				.ConfigureAwait(false);

			var filtered = all.Where(predicate).ToList();

			var take = limit.GetValueOrDefault(20);
			if (take <= 0)
			{
				take = 20;
			}

			var skip = offset.GetValueOrDefault(0);
			if (skip < 0)
			{
				skip = 0;
			}

			var page = filtered
				.Skip(skip)
				.Take(take)
				.ToList();

			var response = new GetSubscriptionComparisonsResponse
			{
				TotalCount = filtered.Count,
				Limit = take,
				Offset = skip
			};

			foreach (var comparison in page)
			{
				var startedAt = comparison.ComparedAt - comparison.Duration;

				response.Comparisons.Add(new SubscriptionComparisonListItemResponse
				{
					Id = comparison.Id,
					Status = MapComparisonStatus(comparison.Status),
					StartedAt = startedAt,
					CompletedAt = comparison.ComparedAt,
					Duration = System.Xml.XmlConvert.ToString(comparison.Duration),
					DifferenceCount = comparison.Summary.TotalDifferences,
						ObjectsCompared = comparison.Summary.ObjectsCompared,
					Trigger = "manual"
				});
			}

			return Ok(response);
		}

		private static SubscriptionDetailResponse MapToDetailResponse(
			Subscription subscription,
			ComparisonResult? lastComparison,
			int totalComparisons,
			int sqlFileCount)
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
						SqlFileCount = sqlFileCount
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

		private static string MapComparisonStatus(ComparisonStatus status)
		{
			return status switch
			{
				ComparisonStatus.Synchronized => "synchronized",
				ComparisonStatus.HasDifferences => "has-differences",
				ComparisonStatus.Error => "error",
				ComparisonStatus.Partial => "partial",
				_ => "unknown"
			};
		}
}
