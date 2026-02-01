# SQL Sync Service – Local Testing Guide

This guide shows how to exercise the **existing HTTP APIs** against your own SQL Server database and SQL project folder. At this point in the implementation, the comparison engine (DacFx + orchestrator) is wired and tested internally, but it is **not yet exposed as a public HTTP endpoint**. You can still verify:

- The service starts correctly.
- Your database connection works.
- Your SQL project folder is recognized and analyzable.

Later milestones will add subscription + comparison endpoints on top of this foundation.

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

## 7. How the comparison engine is exercised today

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

## 8. What’s next to test full comparisons with your data

To run comparisons against **your actual database and project** through HTTP, the next milestone will add subscription + comparison endpoints, for example:

- `POST /api/subscriptions` – register your database + project pair.
- `POST /api/subscriptions/{id}/compare` – trigger `IComparisonOrchestrator.RunComparisonAsync` for that subscription.
- `GET /api/subscriptions/{id}/history` – read back comparison history.

Once those endpoints exist, you will be able to:

1. Register a subscription pointing at your DB and folder.
2. Trigger a comparison via HTTP.
3. Inspect the stored `ComparisonResult` and differences.

Until then, you can:

- Validate **connectivity** with `/api/connections/test`.
- Validate **project visibility and structure** with `/api/folders/validate`.
- Use the **test suite** to confirm that the comparison engine is behaving correctly in-memory.

If you’d like, the next step can be to implement a minimal dev-only comparison endpoint that calls `IComparisonOrchestrator` so you can hit it directly with your own DB and project.

