# SQL Sync Service – Local Testing Guide

This guide shows how to exercise the **existing HTTP APIs** against your own SQL Server database and SQL project folder. At this point in the implementation, the comparison engine (DacFx + orchestrator) is wired and tested internally, but it is **not yet exposed as a public comparison HTTP endpoint**. You can verify:

- The service starts correctly.
- Your database connection works.
- Your SQL project folder is recognized and analyzable.
- Subscriptions that pair a database and project can be created, listed, updated, paused/resumed, and deleted.

Subscription management endpoints are already available; later milestones will add comparison trigger/history endpoints and background processing on top of this foundation.

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

## 8. How the comparison engine is exercised today

Milestone 5 implemented the comparison engine components:

- `DatabaseModelBuilder` (extracts schema via DacFx and builds `SchemaSnapshot`).
- `FileModelBuilder` (scans your project folder and builds `FileModelCache`).
- `SchemaComparer` (compares snapshot vs cache to produce `SchemaDifference` records).
- `ComparisonOrchestrator` (coordinates the above, enforces concurrency, and persists `ComparisonResult`).

Right now these are exercised **via automated tests**, not via a public HTTP endpoint yet. You can run the tests to see the engine in action end-to-end:

```bash
dotnet test tests/SqlSyncService.Tests/SqlSyncService.Tests.csproj
```

The `ComparisonOrchestratorTests` create an in-memory subscription, build snapshots / caches using stubs, run a comparison, and verify that `ComparisonResult` and snapshots are persisted correctly.

---

## 9. What’s next to test full comparisons with your data

You can now **persist subscriptions** (database + project pairs) and manage their lifecycle via `/api/subscriptions`, but there is still no public endpoint that actually runs a DacFx comparison for a given subscription.

The next milestones will add, for example:

- `POST /api/subscriptions/{id}/compare` – trigger `IComparisonOrchestrator.RunComparisonAsync` for that subscription.
- `GET /api/subscriptions/{id}/history` – read back detailed comparison history.

Once those endpoints and the background monitoring pipeline exist, you will be able to:

1. Register a subscription pointing at your DB and folder (already possible now).
2. Trigger a comparison for that subscription via HTTP or background scheduling.
3. Inspect the stored `ComparisonResult` records and per-object differences over time.

Until then, you can:

- Validate **connectivity** with `/api/connections/test`.
- Validate **project visibility and structure** with `/api/folders/validate`.
- Work with **subscriptions** via `/api/subscriptions` to persist your DB + project configuration and test state transitions (active/paused/delete).
- Use the **test suite** to confirm that the comparison engine is behaving correctly in-memory.

If you’d like, a next step can be to implement a minimal dev-only comparison endpoint that calls `IComparisonOrchestrator` so you can hit it directly with your own DB and project before the full background pipeline is complete.

