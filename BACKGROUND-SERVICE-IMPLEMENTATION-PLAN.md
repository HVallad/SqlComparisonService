# SQL Project Synchronization – .NET Background Service Implementation Plan

This document captures the implementation plan for the .NET Background Service, based on `DESIGN.md` and `SERVICE-SPECIFICATION.md`.

---

## 1. Project Structure & Setup

### 1.1 Solution & folders

- `SqlSyncService.sln`
- `src/SqlSyncService/`
  - `Program.cs`, `SqlSyncService.csproj`
  - `Configuration/` – `ServiceConfiguration` & related option classes
  - `Domain/`
    - `Subscriptions/` – `Subscription`, `DatabaseConnection`, `ProjectFolder`, enums
    - `Comparisons/` – `ComparisonResult`, `SchemaDifference`, etc.
    - `Changes/` – `DetectedChange`, `PendingChangeBatch`
    - `Caching/` – `SchemaSnapshot`, `FileModelCache`, `SchemaObjectSummary`
  - `Persistence/` – LiteDB context + repositories for 4 collections
  - `DacFx/` – database/file model builders and schema comparer
  - `ChangeDetection/` – file watcher, DB polling, debouncer
  - `BackgroundTasks/` – 5 workers
  - `Api/` – controllers, DTOs, error handling
  - `Realtime/` – `SyncHub` + event payloads
  - `Services/` – orchestration & domain services
- `tests/SqlSyncService.Tests/` – unit + integration tests

### 1.2 Target framework & runtime

- Target: **.NET 8.0** (`net8.0`).
- Runtime: primarily Windows (for DPAPI), but keep code cross-platform where possible.

### 1.3 NuGet packages

- Web/hosting: `Microsoft.AspNetCore.App` (implicit), `Microsoft.Extensions.Hosting`.
- DacFx & T‑SQL parsing:
  - `Microsoft.SqlServer.DacFx` (≈162.x.x)
  - `Microsoft.SqlServer.TransactSql.ScriptDom` (≈161.x.x)
- Persistence: `LiteDB`.
- Diffing: `DiffPlex` for unified and side-by-side diffs.
- Logging (recommended): `Serilog.AspNetCore`, `Serilog.Sinks.File`.
- Optional security: `Microsoft.AspNetCore.DataProtection` for cross‑platform secrets.
- Tests: `xunit`, `xunit.runner.visualstudio`, `Moq`/`NSubstitute`, `Microsoft.AspNetCore.Mvc.Testing`.

### 1.4 Configuration files

- `appsettings.json`
  - `Service` section mapping to `ServiceConfiguration` (server, monitoring, cache, logging).
  - `LiteDb.DatabasePath`.
  - Logging levels.
- `launchSettings.json`
  - HTTP endpoint: `http://localhost:5050` (matching SERVICE-SPEC base URL).

---

## 2. Core Components & Implementation Order

1. **Bootstrap & skeleton** – minimal `Program.cs`, controllers, `SyncHub`, health checks.
2. **Domain models** – all C# models from DESIGN §6.1–6.5.
3. **Persistence (LiteDB)** – context + repositories for:
   - `Subscriptions`, `SchemaSnapshots`, `ComparisonHistory`, `PendingChanges`.
4. **DacFx + file integration** – `DatabaseModelBuilder`, `FileModelBuilder`, `SchemaComparer`.
5. **Application services** – `SubscriptionService`, `ComparisonOrchestrator`, `ScriptGenerationService`, `HealthService`.
6. **Background workers** – DB polling, file watching, reconciliation, cache cleanup, health.
7. **API controllers** – implement all REST endpoints from SERVICE-SPEC §2.3.
8. **SignalR hub** – implement all events from SERVICE-SPEC §2.4.
9. **Cross-cutting** – exception middleware, logging, health reporting.
10. **Tests** – unit + integration coverage for core workflows.

Dependencies flow from Domain → Persistence/DacFx → Services → BackgroundTasks → API/Hub.

---

## 3. API Implementation Details

### 3.1 Controller organization

- **HealthController** (`/api`)
  - `GET /api/health`
  - `GET /api/status`
- **ConnectionsController** (`/api/connections`)
  - `POST /api/connections/test`
- **FoldersController** (`/api/folders`)
  - `POST /api/folders/validate`
- **SubscriptionsController** (`/api/subscriptions`)
  - `GET /api/subscriptions`
  - `POST /api/subscriptions`
  - `GET /api/subscriptions/{id}`
  - `PUT /api/subscriptions/{id}`
  - `DELETE /api/subscriptions/{id}` (with `deleteHistory`)
  - `POST /api/subscriptions/{id}/pause`
  - `POST /api/subscriptions/{id}/resume`
  - `GET /api/subscriptions/{id}/comparisons`
  - `GET /api/subscriptions/{id}/differences/current`
  - `GET /api/subscriptions/{id}/objects`
  - `GET /api/subscriptions/{id}/objects/{objectName}`
  - `GET /api/subscriptions/{id}/objects/{objectName}/script`
  - `GET /api/subscriptions/{id}/scripts/export`
- **ComparisonsController**
  - `POST /api/subscriptions/{id}/compare`
  - `GET /api/comparisons/{comparisonId}`
  - `GET /api/comparisons/{comparisonId}/differences`
  - `GET /api/comparisons/{comparisonId}/differences/{diffId}`

### 3.2 DTOs & middleware

- Separate DTOs for all request/response bodies, matching SERVICE-SPEC JSON exactly.
- Global exception middleware mapping domain/validation errors to standard error envelope with:
  - HTTP status, `error.code`, `message`, `details`, `field`, `traceId`, `timestamp`.

---

## 4. DacFX Integration Strategy

- Namespaces: `Microsoft.SqlServer.Dac`, `.Dac.Model`, `.Dac.Compare`, and `Microsoft.SqlServer.TransactSql.ScriptDom`.
- **Database model** (`DatabaseModelBuilder`):
  - Use `DacServices.Extract` to get `.dacpac` in memory.
  - Load `TSqlModel` and build `SchemaSnapshot` (with `DacpacBytes` + `SchemaObjectSummary`).
- **File model** (`FileModelBuilder`):
  - Enumerate `.sql` files per `ProjectFolder`.
  - Optionally parse via ScriptDom to infer object names/types.
  - Build `FileModelCache` containing `FileObjectEntry` with hashes and timestamps.
- **Comparison** (`SchemaComparer`):
  - Full comparisons: DacFx `SchemaComparison` between cached dacpacs.
  - Incremental: use hash-based detection (DESIGN §7.2.1/§7.2.3) and only compare changed objects.
- Cache snapshots in `SchemaSnapshots` with 7‑day/10‑snapshot retention per subscription.

---

## 5. Background Tasks Architecture

Each implemented as a `BackgroundService` using `PeriodicTimer`.

1. **DatabasePollingWorker**
   - Every ~30s: query `sys.objects` for modify_date changes.
   - On change: record `DetectedChange` (source=Database), emit `DatabaseChanged`, optionally trigger incremental comparison.
2. **FileWatchingWorker**
   - Use `.NET FileSystemWatcher` per subscription.
   - Pipe events into `ChangeDebouncer`, record `DetectedChange` (source=FileSystem), emit `FileChanged`, optionally compare.
3. **ReconciliationWorker**
   - Every ~5 minutes: full comparison for all active subscriptions; updates snapshots and caches.
4. **CacheCleanupWorker**
   - Hourly: enforce snapshot and history retention; compact LiteDB.
5. **HealthCheckWorker**
   - Every ~60s: check DB connectivity & folder access, update subscription health, emit `SubscriptionHealthChanged`/`SubscriptionStateChanged`.

Throttling: `ComparisonOrchestrator` enforces `MaxConcurrentComparisons` and returns `COMPARISON_IN_PROGRESS` when overloaded.

---

## 6. Data Persistence Layer

- Embedded DB: **LiteDB** chosen over SQLite for document-style collections and simpler setup.
- Collections:
  - **Subscriptions** – subscription config, DB connection (with encrypted password), project info, options, state, timestamps, health, last comparison.
  - **SchemaSnapshots** – cached dacpacs and object summaries per subscription.
  - **ComparisonHistory** – summaries per comparison (counts, durations, trigger, status).
  - **PendingChanges** – unprocessed `DetectedChange` entries used for debouncing/batching.
- Repositories (`ISubscriptionRepository`, etc.) encapsulate LiteDB access.
- Credential encryption via an `ICredentialProtector` abstraction:
  - Default DPAPI on Windows; optional Data Protection on other platforms.

---

## 7. Error Handling, Logging, Health

- Central exception middleware implementing SERVICE-SPEC §2.3.7 error format and codes.
- Logging with `ILogger<T>` (and optionally Serilog) including subscription/comparison IDs in scopes.
- Health:
  - `/api/health` – high-level status, uptime, version, memory usage.
  - `/api/status` – includes subscription counts and background task status.
  - Backed by `HealthCheckWorker` state and ASP.NET Core health checks.

---

## 8. Testing Strategy

- **Unit tests**
  - Domain logic (hashing, debouncing, state transitions).
  - Repositories using temporary LiteDB files.
  - `ComparisonOrchestrator` queueing and concurrency limits.
- **Integration tests**
  - `WebApplicationFactory` to exercise HTTP APIs end-to-end with test LiteDB.
  - Verify flows from SERVICE-SPEC §4 (create subscription, compare, list differences).
- **DacFx tests**
  - Optional integration tests against a test SQL Server instance (localdb/Docker).
- Use mocks for DB metadata and filesystem when not running full integration.

---

## 9. Development Milestones

This section breaks the work into concrete, measurable milestones with approximate effort and clear “definition of done”.

### Milestone 1 – Service skeleton & hosting (0.5–1 day)

**Scope**
- Create solution/project structure.
- Implement minimal `Program.cs` with controllers, SignalR, and health checks wired.
- Add base configuration (`appsettings.json`, `launchSettings.json`).

**Definition of done**
- Service runs locally on `http://localhost:5050`.
- `/api/health` returns a hard-coded `healthy` response.
- `/hubs/sync` accepts connections (no events yet).

### Milestone 2 – Domain models & configuration (0.5–1 day)

**Scope**
- Implement C# domain models from DESIGN §6.1–6.5 (subscriptions, comparisons, changes, caching, configuration).
- Implement `ServiceConfiguration` and bind it from `Service` config section.

**Definition of done**
- All models compile, with reasonable defaults for options and enums.
- Configuration binding validated at startup (e.g., throws or logs clearly if invalid).

### Milestone 3 – Persistence layer with LiteDB (1–1.5 days)

**Scope**
- Implement `LiteDbContext` and configuration for database path.
- Implement repositories for: `Subscriptions`, `SchemaSnapshots`, `ComparisonHistory`, `PendingChanges`.
- Add basic indexing (e.g., by `SubscriptionId`, `CapturedAt`).

**Definition of done**
- CRUD operations for each collection tested via unit tests.
- Subscriptions can be created, queried, updated, and deleted via repository layer only.
- LiteDB file is created at the configured path.

### Milestone 4 – Connection & folder validation APIs (1–1.5 days)

**Scope**
- Implement `ConnectionsController` (`POST /api/connections/test`).
- Implement `FoldersController` (`POST /api/folders/validate`).
- Implement global error-handling middleware returning the standard error envelope.

**Definition of done**
- Both endpoints return responses that match the SERVICE-SPEC examples (success and failure cases).
- Validation errors produce `400` with `VALIDATION_ERROR`.
- Connection failures produce `422` with `CONNECTION_FAILED`.

### Milestone 5 – DacFx integration & comparison engine (2–3 days)

**Scope**
- Implement `DatabaseModelBuilder` to extract dacpacs and build `SchemaSnapshot` objects.
- Implement `FileModelBuilder` to scan project folders and build `FileModelCache`.
- Implement `SchemaComparer` to run DacFx `SchemaComparison` and map results to `SchemaDifference`.
- Implement `ComparisonOrchestrator` to queue comparisons, enforce concurrency limits, and persist `ComparisonHistory`.

**Definition of done**
- A comparison can be triggered programmatically (e.g., from a test) and results are stored in LiteDB.
- `ComparisonHistory` entries contain accurate timing, counts, and triggers.
- Incremental vs full comparison modes are supported by configuration flags.

### Milestone 6 – Subscription management API (1.5–2 days)

**Scope**
- Implement `SubscriptionsController` for all CRUD + pause/resume endpoints.
- Implement `SubscriptionService` to encapsulate validation and lifecycle transitions.
- Wire up optional initial comparison trigger on subscription creation.

**Definition of done**
- All subscription endpoints behave per SERVICE-SPEC (including `state`, timestamps, health fields where applicable).
- Duplicate subscription scenarios return `409` where required.
- Creating a subscription can optionally enqueue an initial comparison.

### Milestone 7 – Comparison & object detail APIs (2 days)

**Scope**
- Implement `ComparisonsController` endpoints:
  - `POST /api/subscriptions/{id}/compare`.
  - `GET /api/comparisons/{comparisonId}`.
  - `GET /api/comparisons/{comparisonId}/differences`.
  - `GET /api/comparisons/{comparisonId}/differences/{diffId}`.
- Implement `ObjectsController` endpoints for objects and script export.
- Implement `ScriptGenerationService` and diff generation (`unifiedDiff`, `sideBySideDiff`) using DiffPlex.

**Definition of done**
- Flows from SERVICE-SPEC §4.3 (TreeView + Diff Editor) can be reproduced via HTTP calls.
- Detailed difference responses include scripts and diff payloads in the expected shape.
- Filtering by type/action/direction query parameters works as specified.

### Milestone 8 – Background workers & change detection (2–3 days)

**Scope**
- Implement `DatabasePollingWorker`, `FileWatchingWorker`, `ReconciliationWorker`, `CacheCleanupWorker`, `HealthCheckWorker`.
- Implement `ChangeDebouncer` and integrate with file and DB events.
- Integrate workers with `PendingChanges`, `ComparisonOrchestrator`, and repositories.

**Definition of done**
- Simulated DB and file changes result in `DetectedChange` entries and queued comparisons when auto-compare is enabled.
- Periodic reconciliation refreshes snapshots and captures missed changes.
- Cache retention and cleanup behavior is verified by tests.

### Milestone 9 – SignalR hub & real-time events (1.5–2 days)

**Scope**
- Implement `SyncHub` methods: `JoinSubscription`, `LeaveSubscription`, `JoinAll`.
- Implement server-to-client events for all messages in SERVICE-SPEC §2.4 (change, progress, subscription, and service status events).
- Use `IHubContext<SyncHub>` from workers and services to publish events.

**Definition of done**
- A test or sample client can subscribe to a subscription and receive:
  - `FileChanged`, `DatabaseChanged`, `DifferencesDetected`.
  - `ComparisonStarted`, `ComparisonProgress`, `ComparisonCompleted`, `ComparisonFailed`.
  - `SubscriptionStateChanged`, `SubscriptionHealthChanged`, `SubscriptionCreated`, `SubscriptionDeleted`.
  - `ServiceShuttingDown`, `ServiceReconnected`.

### Milestone 10 – Hardening, health, and test coverage (2–3 days)

**Scope**
- Implement `/api/health` and `/api/status` responses exactly as specified.
- Review and harden error handling and logging (including trace IDs and correlation where useful).
- Expand unit and integration tests to cover edge cases, larger schemas (mocked), and concurrent workloads.

**Definition of done**
- All 18 REST endpoints and 10 SignalR events behave per the specification.
- Core workflows from SERVICE-SPEC §4 (creating subscriptions, detecting changes, viewing differences) are covered by automated tests.
- Logs and error responses provide enough information for troubleshooting in a real dev environment.
