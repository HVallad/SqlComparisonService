# SQL Sync Service – Local Testing Guide

This guide shows how to exercise the **existing HTTP APIs** against your own SQL Server database and SQL project folder. The comparison engine (DacFx + orchestrator) is wired and available via manual comparison endpoints for subscriptions, including per-object difference APIs. **Background monitoring via Milestone 8 workers is now active**, providing automated change detection and health monitoring. You can verify:

- The service starts correctly.
- Your database connection works.
- Your SQL project folder is recognized and analyzable.
- Subscriptions that pair a database and project can be created, listed, updated, paused/resumed, and deleted.

Subscription management, comparison trigger/history, and per-object difference endpoints are already available; later milestones will add background processing on top of this foundation.

---

## 1. Prerequisites

1. **.NET SDK 8+** installed.
2. **SQL Server instance** you can reach from this machine.
3. A **SQL database** with some schema objects (tables, views, procedures, etc.).
4. A **SQL project folder** on disk containing `.sql` files that roughly mirror that schema.
5. This repository checked out and restored:

   ```bash
   dotnet restore
   ```

---

## 2. Configure the service

Open `src/SqlSyncService/appsettings.json` and adjust at least these sections:

- **Service.Server** – HTTP port / HTTPS:
  - The template uses port `5050` by default.
- **LiteDb.DatabasePath** – where LiteDB will store state; any writable path is fine.

Example (abridged):

```jsonc
{
  "Service": {
    "Server": {
      "HttpPort": 5050,
      "EnableHttps": false
    },
    "Monitoring": {
      "MaxConcurrentComparisons": 2
    }
  },
  "LiteDb": {
    "DatabasePath": "Data/sql-sync.db"
  }
}
```

You do **not** need to wire your specific database or project folder into configuration yet; the current public APIs accept those as request bodies.

---

## 3. Run the service

From the repo root:

```bash
cd src/SqlSyncService
dotnet run
```

By default this will listen on **http://localhost:5050** (per `Service.Server.HttpPort`). The console output should mention the actual URLs.

You can now call the APIs using **curl**, **Postman**, or your browser.

---

## 4. Health check

### Endpoint

- `GET /api/health`

### Example

```bash
curl http://localhost:5050/api/health
```

You should receive a simple `200 OK` JSON response indicating the service is alive.

---

## 5. Test your database connection

### Endpoint

- `POST /api/connections/test`

This uses the `DatabaseConnectionTester` service and the `Microsoft.Data.SqlClient` provider to try opening a connection to your SQL Server.

### Request body

The exact DTO may evolve, but it follows the **DatabaseConnection** shape from the domain. A minimal SQL authentication example:

```json
{
  "server": "YOUR_SERVER_NAME_OR_ADDRESS",
  "database": "YourDatabaseName",
  "authType": "SqlAuth",
  "username": "your_user",
  "password": "your_password",
  "trustServerCertificate": true,
  "connectionTimeoutSeconds": 15
}
```

> Note: In the domain model the password is stored encrypted; for the test endpoint we send a plain password field as described when this API was implemented.

### Example call

```bash
curl -X POST http://localhost:5050/api/connections/test \
  -H "Content-Type: application/json" \
  -d '{
        "server": "localhost\\SQLEXPRESS",
        "database": "MyTestDb",
        "authType": "SqlAuth",
        "username": "sa",
        "password": "YourStrong!Passw0rd",
        "trustServerCertificate": true,
        "connectionTimeoutSeconds": 15
      }'
```

### Expected responses

- **Success (200)** – JSON with `success: true` plus server info / object counts.
- **Connection failure (422)** – Error envelope with `error.code = "CONNECTION_FAILED"`.
- **Validation error (400)** – Error envelope with `error.code = "VALIDATION_ERROR"`.

This validates that the service can reach your database with the settings you intend to use for comparisons.

---

## 6. Validate your SQL project folder

### Endpoint

- `POST /api/folders/validate`

This endpoint checks:

- Whether the folder exists.
- Whether it is writable.
- Basic structure / heuristics for a SQL project.
- Counts of discovered `.sql` files.

### Request body

A minimal request:

```json
{
  "path": "C:/path/to/your/sql/project"
}
```

> Use a **full absolute path** to avoid confusion about the working directory of the service.

### Example call

```bash
curl -X POST http://localhost:5050/api/folders/validate \
  -H "Content-Type: application/json" \
  -d '{ "path": "C:/Dev/SqlProjects/MyDb" }'
```

### Expected responses

- **Success (200)** – JSON including fields like:
  - `valid`, `exists`, `isWritable`, `detectedStructure`, `sqlFileCount`, `objectCounts`, `parseErrors`.
- **Not found (404)** – Error envelope with `error.code = "NOT_FOUND"` if the directory does not exist.

This tells you the service can see and analyze your SQL project folder and that it looks reasonable.

---

## 7. Manage subscriptions (database + project pairs)

Subscriptions represent a **pairing between a database and a SQL project folder**, plus options that describe when and how comparisons should run. They are stored in LiteDB and are the foundation for future background monitoring.

> Note: As of now, creating a subscription does **not** yet trigger a comparison by itself. Comparison execution is still done via the test suite; future milestones will wire comparisons to subscriptions.

### 7.1 Endpoint overview

- `POST /api/subscriptions` – Create a subscription.
- `GET /api/subscriptions` – List subscriptions (optionally filtered by state).
- `GET /api/subscriptions/{id}` – Detailed view of a single subscription.
- `PUT /api/subscriptions/{id}` – Update an existing subscription.
- `POST /api/subscriptions/{id}/pause` – Mark a subscription as paused.
- `POST /api/subscriptions/{id}/resume` – Resume a previously paused subscription.
- `DELETE /api/subscriptions/{id}?deleteHistory=true` – Delete a subscription and optionally its comparison history.

### 7.2 Create a subscription

Use this to persist the DB + project configuration you validated with the earlier endpoints.

#### Request body shape

```json
{
  "name": "My database subscription",
  "database": {
    "server": "localhost\\SQLEXPRESS",
    "database": "MyTestDb",
    "authType": "windows",
    "username": null,
    "password": null,
    "trustServerCertificate": true,
    "connectionTimeoutSeconds": 15
  },
  "project": {
    "path": "C:/Dev/SqlProjects/MyDb",
    "includePatterns": ["**/*.sql"],
    "excludePatterns": ["**/bin/**", "**/obj/**"],
    "structure": "by-type"
  },
  "options": {
    "autoCompare": true,
    "compareOnFileChange": true,
    "compareOnDatabaseChange": true,
    "objectTypes": ["table", "view", "stored-procedure"],
    "ignoreWhitespace": true,
    "ignoreComments": false
  }
}
```

Key notes:

- `authType` is case-insensitive. Supported values include:
  - `"windows"` (default if omitted) – Windows integrated security.
  - `"sql"` / `"sqlserver"` – SQL authentication (use `username` + `password`).
  - `"azuread"`, `"azuread-interactive"` – Azure AD modes.
- `structure` controls how project files are organized; supported values include:
  - `"flat"`, `"by-schema"`, `"by-schema-and-type"`, and `"by-type"` (the default) which maps to the internal “by object type” layout.
- `options.objectTypes` controls which object kinds are included (e.g. `"table"`, `"view"`, `"stored-procedure"`, `"function"`, `"trigger"`). If this field is **omitted or an empty array**, the service keeps the defaults from `ComparisonOptions`, which means **all of the main object types above are included**. When you do supply `objectTypes`, it becomes an explicit allow-list and only those types are compared.

#### Example call

```bash
curl -X POST http://localhost:5050/api/subscriptions \
  -H "Content-Type: application/json" \
  -d '{
        "name": "My database subscription",
        "database": {
          "server": "localhost\\SQLEXPRESS",
          "database": "MyTestDb",
          "authType": "windows",
          "trustServerCertificate": true,
          "connectionTimeoutSeconds": 15
        },
        "project": {
          "path": "C:/Dev/SqlProjects/MyDb",
          "structure": "by-type"
        },
        "options": {
          "autoCompare": true,
          "compareOnFileChange": true,
          "compareOnDatabaseChange": true,
          "ignoreWhitespace": true,
          "ignoreComments": false,
          "objectTypes": ["table", "view"]
        }
      }'
```

On success you should see:

- **201 Created**
- A `Location` header like `/api/subscriptions/{id}`.
- A JSON body with the created subscription, including `id`, `state` (initially `"active"`), and timestamps.

If you try to create another subscription with the same name (case-insensitive), you’ll get:

- **409 Conflict** with an error payload where `error.code = "CONFLICT"` and `error.field = "name"`.

### 7.3 List and filter subscriptions

#### List all

```bash
curl http://localhost:5050/api/subscriptions
```

This returns a response with `subscriptions` (array) and `totalCount`.

#### Filter by state

```bash
curl "http://localhost:5050/api/subscriptions?state=active"
```

Valid `state` values are `active`, `paused`, and (in future) `error`. An invalid value returns:

- **400 Bad Request** with `error.code = "VALIDATION_ERROR"` and `error.field = "state"`.

### 7.4 Inspect, pause, resume, and delete

#### Get a single subscription

```bash
curl http://localhost:5050/api/subscriptions/{id}
```

You’ll receive detailed information including database, project, options, basic health placeholders, and statistics (difference counts / last comparison info if any exist).

- **404 Not Found** is returned if the `id` does not exist.

#### Pause and resume

```bash
# Pause
curl -X POST http://localhost:5050/api/subscriptions/{id}/pause

# Resume
curl -X POST http://localhost:5050/api/subscriptions/{id}/resume
```

Rules:

- Pausing sets the subscription state to `"paused"` and records `pausedAt`.
- Resuming requires the subscription to currently be paused; otherwise you’ll get:
  - **409 Conflict** with `error.code = "CONFLICT"` and `error.field = "state"`.

#### Delete a subscription

```bash
curl -X DELETE "http://localhost:5050/api/subscriptions/{id}?deleteHistory=true"
```

- **204 No Content** indicates successful deletion.
- Subsequent `GET /api/subscriptions/{id}` will return **404 Not Found**.

---

## 8. Trigger comparisons and browse history

Milestone 7 exposes the comparison engine via HTTP for **manual comparisons per subscription**.

### 8.1 Trigger a comparison for a subscription

#### Endpoint

- `POST /api/subscriptions/{id}/compare`

`{id}` is the subscription ID you created earlier.

#### Request body

The body is optional. When omitted, the service uses the subscription’s stored options and performs a normal comparison.

Minimal example forcing a full comparison:

```json
{
  "forceFullComparison": true
}
```

Fields:

- `forceFullComparison` (bool, optional)
  - `true` – force a full snapshot rebuild before comparison.
  - `false` or omitted – allow the orchestrator to use an incremental comparison if available.
- `objectTypes` (string[], optional)
  - Reserved for future use. You can send values like `"table"`, `"view"`, etc., but the current implementation always uses the subscription’s configured object type options.
- `objectNames` (string[], optional)
  - Reserved for future use; currently ignored.

#### Example call

```bash
curl -X POST "http://localhost:5050/api/subscriptions/{id}/compare" \
  -H "Content-Type: application/json" \
  -d '{
        "forceFullComparison": true
      }'
```

#### Expected responses

- **202 Accepted** – comparison was started successfully.

Response shape (abridged):

```json
{
  "comparisonId": "GUID",
  "subscriptionId": "GUID",
  "status": "has-differences",
  "queuedAt": "2024-01-01T10:00:00Z",
  "estimatedDuration": "PT5S"
}
```

Notes:

- `status` reflects the comparison result (`"synchronized"`, `"has-differences"`, `"error"`, or `"partial"`).
- `queuedAt` is derived from `comparedAt - duration` on the stored result.
- `estimatedDuration` is the actual duration encoded as an ISO 8601 duration string.

Error cases:

- **404 Not Found** – subscription does not exist (`error.code = "NOT_FOUND"`).
- **409 Conflict** – a comparison is already in progress (`error.code = "COMPARISON_IN_PROGRESS"`).

### 8.2 List comparison history for a subscription

#### Endpoint

- `GET /api/subscriptions/{id}/comparisons?limit=20&offset=0&status=completed|failed`

Query parameters:

- `limit` – max number of items to return (default `20`, must be > 0).
- `offset` – number of items to skip (default `0`, must be >= 0).
- `status` – optional filter:
  - `completed` – comparisons whose status is **not** `error`.
  - `failed` – comparisons whose status **is** `error`.

#### Example calls

List the first page of all comparisons for a subscription:

```bash
curl "http://localhost:5050/api/subscriptions/{id}/comparisons"
```

List failed comparisons, 10 per page, skipping the first 10:

```bash
curl "http://localhost:5050/api/subscriptions/{id}/comparisons?status=failed&limit=10&offset=10"
```

#### Response shape

On success (**200 OK**):

```json
{
  "comparisons": [
    {
      "id": "GUID",
      "status": "has-differences",
      "startedAt": "2024-01-01T09:59:55Z",
      "completedAt": "2024-01-01T10:00:00Z",
      "duration": "PT5S",
      "differenceCount": 3,
      "objectsCompared": 0,
      "trigger": "manual"
    }
  ],
  "totalCount": 1,
  "limit": 20,
  "offset": 0
}
```

Notes:

- `objectsCompared` is currently `0` (placeholder for future enrichment).
- `trigger` is currently always `"manual"`.

Error cases:

- **404 Not Found** – subscription does not exist (`error.code = "NOT_FOUND"`).
- **400 Bad Request** – invalid `status` value (`error.code = "VALIDATION_ERROR"`, `error.field = "status"`).

### 8.3 Inspect a specific comparison

#### Endpoint

- `GET /api/comparisons/{comparisonId}`

#### Example call

```bash
curl "http://localhost:5050/api/comparisons/{comparisonId}"
```

#### Response shape

On success (**200 OK**):

```json
{
  "id": "GUID",
  "subscriptionId": "GUID",
  "status": "has-differences",
  "comparedAt": "2024-01-01T10:00:00Z",
  "duration": "PT5S",
  "differenceCount": 3,
  "summary": {
    "totalDifferences": 3,
    "byType": {
      "table": 1,
      "function": 1,
      "trigger": 1
    },
    "byAction": {
      "add": 1,
      "modify": 1,
      "delete": 1
    },
    "byDirection": {
      "database-only": 1,
      "file-only": 1,
      "different": 1
    }
  }
}
```

Error case:

- **404 Not Found** – comparison does not exist (`error.code = "NOT_FOUND"`).

This endpoint is ideal for populating a high-level “summary panel” for a selected comparison in your client.

---

## 9. Inspect per-object differences for a comparison

Once you have a comparison ID (from section 8), you can drill into the individual object-level differences.

### 9.1 List differences for a comparison

#### Endpoint

- `GET /api/comparisons/{comparisonId}/differences`

#### Query parameters

- `type` (optional) – filter by object type:
  - `table`, `view`, `stored-procedure`, `function`, `trigger`.
- `action` (optional) – filter by change type:
  - `add`, `delete`, `change` (you may also pass `modify`, which is treated as `change`).
- `direction` (optional) – filter by where the object exists:
  - `database-only`, `file-only`, or `different`.
  - The API also accepts camelCase values (`databaseOnly`, `fileOnly`) for convenience.

#### Example call

```bash
curl "http://localhost:5050/api/comparisons/{comparisonId}/differences?type=table&action=add&direction=file-only"
```

#### Example response (abridged)

```json
{
  "comparisonId": "770e8400-e29b-41d4-a716-446655440002",
  "differences": [
    {
      "id": "diff-001",
      "objectType": "table",
      "objectName": "dbo.NewTable",
      "action": "add",
      "direction": "file-only",
      "description": "Object exists in project files but not in database.",
      "severity": "info",
      "filePath": "Tables/dbo.NewTable.sql",
      "suggestedFilePath": null
    }
  ],
  "totalCount": 1
}
```

### 9.2 Inspect a single difference in detail

#### Endpoint

- `GET /api/comparisons/{comparisonId}/differences/{diffId}`

#### Example call

```bash
curl "http://localhost:5050/api/comparisons/{comparisonId}/differences/{diffId}"
```

#### Example response (abridged)

```json
{
  "id": "diff-002",
  "comparisonId": "770e8400-e29b-41d4-a716-446655440002",
  "subscriptionId": "770e8400-e29b-41d4-a716-446655440001",
  "objectType": "stored-procedure",
  "objectName": "dbo.GetUsers",
  "action": "change",
  "direction": "different",
  "filePath": "StoredProcedures/dbo.GetUsers.sql",
  "databaseScript": "CREATE PROCEDURE [dbo].[GetUsers] AS SELECT 1...",
  "fileScript": "CREATE PROCEDURE [dbo].[GetUsers] AS SELECT 2...",
  "unifiedDiff": null,
  "sideBySideDiff": null,
  "propertyChanges": [
    {
      "propertyName": "DefinitionHash",
      "databaseValue": "hash-db",
      "fileValue": "hash-file"
    }
  ]
}
```

#### Error cases

- **404 Not Found** – comparison does not exist (`error.code = "NOT_FOUND"`).
- **404 Not Found** – difference ID is not part of the comparison (`error.code = "NOT_FOUND"`).

## 10. Background Workers (Milestone 8)

Milestone 8 introduces **five background workers** that provide automated change detection and health monitoring.

### 10.1 Background Worker Overview

| Worker | Default Interval | Purpose |
|--------|------------------|---------|
| **HealthCheckWorker** | 60 seconds | Monitors database connectivity and folder accessibility |
| **CacheCleanupWorker** | 1 hour | Enforces retention policies for snapshots and history |
| **FileWatchingWorker** | Real-time + 30s sync | Monitors SQL project folders for file changes |
| **DatabasePollingWorker** | 30 seconds | Polls `sys.objects.modify_date` to detect schema changes |
| **ReconciliationWorker** | 5 minutes | Runs periodic full comparisons to catch missed changes |

### 10.2 Worker Configuration

Workers are configured in `appsettings.json` under `Service.Workers`. You can disable any worker by setting its `Enable*` flag to `false`.

### 10.3 Change Detection Pipeline

1. **FileWatchingWorker** or **DatabasePollingWorker** detects a change
2. Change is recorded in the **ChangeDebouncer** (default 500ms window)
3. After the debounce window, a batch is emitted to the **ChangeProcessor**
4. ChangeProcessor persists changes, emits SignalR events, and triggers comparisons

### 10.4 SignalR Events

| Event | Source |
|-------|--------|
| `ChangesDetected` | ChangeProcessor |
| `DatabaseChanged` | DatabasePollingWorker |
| `SubscriptionHealthChanged` | HealthCheckWorker |

### 10.5 Subscription Health

Health status values: `Healthy`, `Degraded`, `Unhealthy`, `Unknown`

---

## 11. Testing Background Workers

The test suite includes 28 unit tests for background workers:

- `ChangeDebouncerTests.cs` - Debounce window, batch aggregation
- `ChangeProcessorTests.cs` - Batch persistence, SignalR notifications
- `CacheCleanupWorkerTests.cs` - Retention policy enforcement
- `HealthCheckWorkerTests.cs` - Health status determination

Run all 177 tests with `dotnet test`

---

## 12. Summary

With Milestones 7 and 8, you can register subscriptions, trigger comparisons, browse history, benefit from automatic change detection, and monitor subscription health via SignalR events.
