# .NET Background Service - Detailed Specification

## Document Overview

This specification provides a comprehensive definition of the .NET Background Service component within the SQL Project Synchronization system. It serves as the authoritative contract between the VS Code extension and the backend service.

**Related Documents:**
- [Architecture Design](./DESIGN.md) - High-level system architecture

---

## Table of Contents

1. [Service Capabilities & Responsibilities](#1-service-capabilities--responsibilities)
2. [API Contract](#2-api-contract)
3. [Use Case Coverage Analysis](#3-use-case-coverage-analysis)
4. [Data Flow Examples](#4-data-flow-examples)
5. [Error Handling](#5-error-handling)
6. [Appendix: Complete Schema Definitions](#appendix-complete-schema-definitions)

---

## 1. Service Capabilities & Responsibilities

> **Important:** This service is **read-only**. It detects and reports differences between the database and project files but does NOT make any changes to either the database or the file system. Synchronization operations are the responsibility of the VS Code extension or other client tools.

### 1.1 Core Capabilities Overview

The .NET Background Service provides four core capability areas:

#### Subscription Management
- Create/Update/Delete subscriptions
- Validate database connections
- Validate project folder paths
- Persist subscription configuration
- Manage subscription lifecycle (active/paused/error)

#### Schema Operations (Read-Only)
- Extract database schema (via DacFX)
- Parse SQL files into schema model
- Compare schemas (database ↔ files)
- Generate difference reports
- Generate scripts for viewing (not execution)

#### Change Detection
- Monitor database for schema changes (polling)
- Monitor file system for .sql file changes
- Aggregate and debounce change events
- Trigger automatic comparisons on changes
- Notify connected clients of detected changes

#### Caching & Persistence
- Cache database schema snapshots
- Cache file model representations
- Persist comparison history
- Manage cache invalidation

### 1.2 Detailed Responsibility Matrix

| Responsibility | Description | Trigger | Output |
|----------------|-------------|---------|--------|
| **Connection Validation** | Test database connectivity and permissions | API call | Success/failure with details |
| **Schema Extraction** | Extract full database schema using DacFX | API call or scheduled | TSqlModel / cached snapshot |
| **File Parsing** | Parse .sql files into schema objects | API call or file change | File model representation |
| **Schema Comparison** | Compare database model vs file model | API call or change detected | ComparisonResult with differences |
| **Difference Analysis** | Provide detailed property-level diffs | API call | Detailed difference breakdown |
| **Script Generation** | Generate DDL scripts for viewing | API call | SQL script(s) for display only |
| **Change Monitoring** | Detect changes in database/files | Background task | Change events via WebSocket |
| **Health Reporting** | Report service and subscription health | API call / periodic | Health status |

### 1.3 Background Tasks

The service runs several background tasks:

#### Database Schema Polling
- **Frequency:** Every 30 seconds (configurable)
- **Action:** Query `sys.objects` for `modify_date` changes
- **On Change:** Queue comparison, emit WebSocket event

#### File System Monitoring
- **Frequency:** Real-time (FileSystemWatcher)
- **Debounce:** 500ms (configurable)
- **Action:** Track file create/modify/delete events
- **On Change:** Queue comparison, emit WebSocket event

#### Full Reconciliation
- **Frequency:** Every 5 minutes (configurable)
- **Action:** Full schema comparison for all active subscriptions
- **Purpose:** Catch any missed incremental changes

#### Cache Cleanup
- **Frequency:** Every hour
- **Action:** Remove expired snapshots, compact database

#### Health Check
- **Frequency:** Every 60 seconds
- **Action:** Verify database connectivity for all subscriptions
- **On Failure:** Update subscription state, emit WebSocket event

### 1.4 Data Persistence

The service persists data using an embedded database (LiteDB or SQLite):

#### Subscriptions Collection
- Subscription configurations
- Database connection details (encrypted credentials)
- Project folder paths and patterns
- Comparison options
- State and timestamps

#### SchemaSnapshots Collection
- Cached database schema (serialized TSqlModel or .dacpac bytes)
- Object-level hashes for quick comparison
- Capture timestamps
- **Retention:** 7 days or last 10 snapshots per subscription

#### ComparisonHistory Collection
- Past comparison results (summary only, not full diffs)
- Timestamps and durations
- Difference counts by type
- **Retention:** 30 days

#### PendingChanges Collection
- Detected but unprocessed changes
- Used for debouncing and batching
- Cleared after processing

---

## 2. API Contract

### 2.1 API Overview

| Protocol | Port | Purpose |
|----------|------|---------|
| HTTP/REST | 5050 | Commands, queries, CRUD operations |
| WebSocket (SignalR) | 5050 | Real-time events, notifications |

**Base URL:** `http://localhost:5050/api`
**WebSocket Hub:** `http://localhost:5050/hubs/sync`

### 2.2 Authentication & Authorization

For local development, the service operates without authentication. For production/shared scenarios:

**Option A: Local-Only (Default)**
- Bind to localhost only
- No authentication required
- Suitable for single-user development

**Option B: API Key**
- Header: `X-API-Key: <generated-key>`
- Key stored in VS Code settings (encrypted)
- Suitable for shared development servers

**Option C: Windows Authentication**
- Negotiate/NTLM authentication
- Leverages existing Windows credentials
- Suitable for enterprise environments

### 2.3 REST API Endpoints

#### 2.3.1 Health & Status

##### GET /api/health

Basic health check for service availability.

**Response:** `200 OK`

```json
{
  "status": "healthy | degraded | unhealthy",
  "timestamp": "2026-01-31T10:30:00Z",
  "version": "1.0.0",
  "uptime": "PT2H30M15S"
}
```

##### GET /api/status

Detailed service status including all subscriptions.

**Response:** `200 OK`

```json
{
  "service": {
    "status": "healthy",
    "version": "1.0.0",
    "uptime": "PT2H30M15S",
    "memoryUsageMB": 128,
    "activeConnections": 2
  },
  "subscriptions": {
    "total": 3,
    "active": 2,
    "paused": 1,
    "error": 0
  },
  "backgroundTasks": {
    "databasePolling": "running",
    "fileWatching": "running",
    "lastReconciliation": "2026-01-31T10:25:00Z"
  }
}
```

#### 2.3.2 Connection Management

##### POST /api/connections/test

Test a database connection without creating a subscription.

**Response:** `200 OK` (success) | `400 Bad Request` (validation) | `422 Unprocessable Entity` (connection failed)

**Request Body:**
```json
{
  "server": "localhost\\SQLEXPRESS",
  "database": "MyDatabase",
  "authType": "windows | sql | azuread | azuread-interactive",
  "username": "sa",
  "password": "secret",
  "trustServerCertificate": true,
  "connectionTimeoutSeconds": 30
}
```

> **Note:** `username` and `password` are required only if `authType` is `sql`.

**Response Body (Success):**
```json
{
  "success": true,
  "serverVersion": "Microsoft SQL Server 2022",
  "serverEdition": "Developer Edition",
  "databaseExists": true,
  "objectCounts": {
    "tables": 45,
    "views": 12,
    "storedProcedures": 28,
    "functions": 8
  },
  "permissions": {
    "canRead": true,
    "canWrite": true,
    "canExecuteDDL": true
  }
}
```

**Response Body (Failure):**
```json
{
  "success": false,
  "error": {
    "code": "CONNECTION_FAILED",
    "message": "Cannot connect to server 'localhost\\SQLEXPRESS'",
    "details": "A network-related or instance-specific error occurred..."
  }
}
```

##### POST /api/folders/validate

Validate a project folder path and analyze its contents.

**Response:** `200 OK` | `400 Bad Request` | `404 Not Found`

**Request Body:**
```json
{
  "path": "C:\\Projects\\MyDatabase",
  "includePatterns": ["**/*.sql"],
  "excludePatterns": ["**/bin/**", "**/obj/**"]
}
```

**Response Body:**
```json
{
  "valid": true,
  "path": "C:\\Projects\\MyDatabase",
  "exists": true,
  "isWritable": true,
  "detectedStructure": "by-type",
  "sqlFileCount": 85,
  "objectCounts": {
    "tables": 42,
    "views": 10,
    "storedProcedures": 25,
    "functions": 8
  },
  "parseErrors": [
    {
      "file": "Tables/InvalidTable.sql",
      "line": 15,
      "message": "Incorrect syntax near 'COLUMN'"
    }
  ]
}
```

#### 2.3.3 Subscription Management

##### GET /api/subscriptions

List all subscriptions with summary information.

**Query Parameters:** `?state=active|paused|error` (optional filter)

**Response:** `200 OK`

```json
{
  "subscriptions": [
    {
      "id": "550e8400-e29b-41d4-a716-446655440000",
      "name": "MyProject",
      "state": "active",
      "database": {
        "server": "localhost\\SQLEXPRESS",
        "database": "MyDatabase",
        "displayName": "localhost\\SQLEXPRESS.MyDatabase"
      },
      "project": {
        "path": "C:\\Projects\\MyDatabase",
        "sqlFileCount": 85
      },
      "lastComparedAt": "2026-01-31T10:25:00Z",
      "differenceCount": 3,
      "health": {
        "database": "connected",
        "fileSystem": "accessible"
      }
    }
  ],
  "totalCount": 1
}
```

##### POST /api/subscriptions

Create a new subscription.

**Response:** `201 Created` | `400 Bad Request` | `409 Conflict` (duplicate)

**Request Body:**
```json
{
  "name": "MyProject",
  "database": {
    "server": "localhost\\SQLEXPRESS",
    "database": "MyDatabase",
    "authType": "windows",
    "username": null,
    "password": null,
    "trustServerCertificate": true,
    "connectionTimeoutSeconds": 30
  },
  "project": {
    "path": "C:\\Projects\\MyDatabase",
    "includePatterns": ["**/*.sql"],
    "excludePatterns": ["**/bin/**", "**/obj/**"],
    "structure": "by-type"
  },
  "options": {
    "autoCompare": true,
    "compareOnFileChange": true,
    "compareOnDatabaseChange": true,
    "objectTypes": ["table", "view", "stored-procedure", "function"],
    "ignoreWhitespace": true,
    "ignoreComments": false
  }
}
```

**Response Body:**
```json
{
  "id": "550e8400-e29b-41d4-a716-446655440000",
  "name": "MyProject",
  "state": "active",
  "createdAt": "2026-01-31T10:30:00Z",
  "database": { "..." },
  "project": { "..." },
  "options": { "..." }
}
```

##### GET /api/subscriptions/{id}

Get detailed subscription information.

**Response:** `200 OK` | `404 Not Found`

```json
{
  "id": "550e8400-e29b-41d4-a716-446655440000",
  "name": "MyProject",
  "state": "active",
  "createdAt": "2026-01-31T08:00:00Z",
  "updatedAt": "2026-01-31T10:30:00Z",
  "database": {
    "server": "localhost\\SQLEXPRESS",
    "database": "MyDatabase",
    "authType": "windows",
    "displayName": "localhost\\SQLEXPRESS.MyDatabase"
  },
  "project": {
    "path": "C:\\Projects\\MyDatabase",
    "includePatterns": ["**/*.sql"],
    "excludePatterns": ["**/bin/**", "**/obj/**"],
    "structure": "by-type",
    "sqlFileCount": 85
  },
  "options": {
    "autoCompare": true,
    "compareOnFileChange": true,
    "compareOnDatabaseChange": true,
    "objectTypes": ["table", "view", "stored-procedure", "function"],
    "ignoreWhitespace": true,
    "ignoreComments": false
  },
  "lastComparison": {
    "id": "660e8400-e29b-41d4-a716-446655440001",
    "comparedAt": "2026-01-31T10:25:00Z",
    "duration": "PT2.5S",
    "differenceCount": 3
  },
  "health": {
    "database": {
      "status": "connected",
      "lastChecked": "2026-01-31T10:29:00Z"
    },
    "fileSystem": {
      "status": "accessible",
      "lastChecked": "2026-01-31T10:29:00Z"
    }
  },
  "statistics": {
    "totalComparisons": 42
  }
}
```

##### PUT /api/subscriptions/{id}

Update subscription configuration (partial update supported).

**Response:** `200 OK` | `400 Bad Request` | `404 Not Found`

**Request Body:**
```json
{
  "name": "MyProject-Updated",
  "options": {
    "ignoreComments": true
  }
}
```

**Response Body:** Full updated subscription object (same as GET)

##### DELETE /api/subscriptions/{id}

Delete a subscription.

**Query Parameters:** `?deleteHistory=true` (optional, delete comparison history)

**Response:** `204 No Content` | `404 Not Found`

##### POST /api/subscriptions/{id}/pause

Pause monitoring for a subscription.

**Response:** `200 OK` | `404 Not Found`

```json
{
  "id": "550e8400-e29b-41d4-a716-446655440000",
  "state": "paused",
  "pausedAt": "2026-01-31T10:30:00Z"
}
```

##### POST /api/subscriptions/{id}/resume

Resume monitoring for a paused subscription.

**Response:** `200 OK` | `404 Not Found` | `409 Conflict` (not paused)

```json
{
  "id": "550e8400-e29b-41d4-a716-446655440000",
  "state": "active",
  "resumedAt": "2026-01-31T10:35:00Z"
}
```

#### 2.3.4 Comparison Operations

##### POST /api/subscriptions/{id}/compare

Trigger a schema comparison for a subscription.

**Response:** `202 Accepted` | `404 Not Found` | `409 Conflict` (comparison in progress)

**Request Body (optional):**
```json
{
  "forceFullComparison": false,
  "objectTypes": ["table", "view"],
  "objectNames": ["dbo.Users"]
}
```

> **Note:** `forceFullComparison` ignores cache and compares everything. `objectTypes` and `objectNames` filter the comparison scope.

**Response Body:**
```json
{
  "comparisonId": "770e8400-e29b-41d4-a716-446655440002",
  "subscriptionId": "550e8400-e29b-41d4-a716-446655440000",
  "status": "queued",
  "queuedAt": "2026-01-31T10:30:00Z",
  "estimatedDuration": "PT5S"
}
```

##### GET /api/subscriptions/{id}/comparisons

Get comparison history for a subscription.

**Query Parameters:** `?limit=20&offset=0&status=completed|failed`

**Response:** `200 OK` | `404 Not Found`

```json
{
  "comparisons": [
    {
      "id": "770e8400-e29b-41d4-a716-446655440002",
      "status": "completed",
      "startedAt": "2026-01-31T10:30:00Z",
      "completedAt": "2026-01-31T10:30:02Z",
      "duration": "PT2.5S",
      "differenceCount": 3,
      "objectsCompared": 85,
      "trigger": "manual | file-change | database-change | scheduled"
    }
  ],
  "totalCount": 42,
  "limit": 20,
  "offset": 0
}
```

##### GET /api/comparisons/{comparisonId}

Get detailed comparison result.

**Response:** `200 OK` | `404 Not Found`

```json
{
  "id": "770e8400-e29b-41d4-a716-446655440002",
  "subscriptionId": "550e8400-e29b-41d4-a716-446655440000",
  "status": "completed",
  "startedAt": "2026-01-31T10:30:00Z",
  "completedAt": "2026-01-31T10:30:02Z",
  "duration": "PT2.5S",
  "trigger": "manual",
  "summary": {
    "totalDifferences": 3,
    "byType": {
      "table": 1,
      "storedProcedure": 2
    },
    "byAction": {
      "add": 1,
      "delete": 0,
      "change": 2
    },
    "byDirection": {
      "databaseOnly": 1,
      "fileOnly": 0,
      "different": 2
    }
  },
  "objectsCompared": 85,
  "objectsFromDatabase": 85,
  "objectsFromFiles": 84
}
##### GET /api/comparisons/{comparisonId}/differences

Get all differences from a comparison.

**Query Parameters:** `?type=table|view|...&action=add|delete|change&direction=...`

**Response:** `200 OK` | `404 Not Found`

```json
{
  "comparisonId": "770e8400-e29b-41d4-a716-446655440002",
  "differences": [
    {
      "id": "diff-001",
      "objectType": "table",
      "objectName": "dbo.NewTable",
      "action": "add",
      "direction": "databaseOnly",
      "description": "Table exists in database but not in project files",
      "severity": "info",
      "filePath": null,
      "suggestedFilePath": "Tables/dbo.NewTable.sql"
    },
    {
      "id": "diff-002",
      "objectType": "storedProcedure",
      "objectName": "dbo.GetUsers",
      "action": "change",
      "direction": "different",
      "description": "Stored procedure definition differs",
      "severity": "warning",
      "filePath": "StoredProcedures/dbo.GetUsers.sql",
      "changeDetails": {
        "linesAdded": 5,
        "linesRemoved": 2,
        "columnsChanged": []
      }
    }
  ],
  "totalCount": 3
}
```

##### GET /api/comparisons/{comparisonId}/differences/{diffId}

Get detailed difference with full script content and diff.

**Response:** `200 OK` | `404 Not Found`

```json
{
  "id": "diff-002",
  "comparisonId": "770e8400-e29b-41d4-a716-446655440002",
  "objectType": "storedProcedure",
  "objectName": "dbo.GetUsers",
  "action": "change",
  "direction": "different",
  "filePath": "StoredProcedures/dbo.GetUsers.sql",
  "databaseScript": "CREATE PROCEDURE [dbo].[GetUsers]\n@Active BIT...",
  "fileScript": "CREATE PROCEDURE [dbo].[GetUsers]\n@Status INT\n...",
  "unifiedDiff": "@@ -1,10 +1,12 @@\n CREATE PROCEDURE...",
  "sideBySideDiff": {
    "lines": [
      {
        "lineNumber": 2,
        "left": "@Active BIT = 1",
        "right": "@Status INT",
        "type": "modified"
      }
    ]
  }
}
```

##### GET /api/subscriptions/{id}/differences/current

Get current differences (from most recent comparison).

**Query Parameters:** Same filters as `/comparisons/{id}/differences`

**Response:** `200 OK` | `404 Not Found`

**Response Body:** Same as `/comparisons/{id}/differences`

#### 2.3.5 Object Details & Scripts

##### GET /api/subscriptions/{id}/objects

List all objects tracked in a subscription.

**Query Parameters:** `?type=table|view|...&source=database|files|both&search=name`

**Response:** `200 OK` | `404 Not Found`

```json
{
  "objects": [
    {
      "name": "dbo.Users",
      "type": "table",
      "inDatabase": true,
      "inFiles": true,
      "isIdentical": true,
      "filePath": "Tables/dbo.Users.sql",
      "lastModifiedInDb": "2026-01-30T15:00:00Z",
      "lastModifiedInFiles": "2026-01-30T14:55:00Z"
    }
  ],
  "totalCount": 85,
  "byType": {
    "table": 45,
    "view": 12,
    "storedProcedure": 20,
    "function": 8
  }
}
```

##### GET /api/subscriptions/{id}/objects/{objectName}

Get detailed object information.

**Response:** `200 OK` | `404 Not Found`

```json
{
  "name": "dbo.Users",
  "type": "table",
  "database": {
    "exists": true,
    "createDate": "2026-01-15T10:00:00Z",
    "modifyDate": "2026-01-30T15:00:00Z",
    "script": "CREATE TABLE [dbo].[Users]...",
    "dependencies": ["dbo.Roles", "dbo.Departments"],
    "dependents": ["dbo.Orders", "dbo.GetUserDetails"]
  },
  "file": {
    "exists": true,
    "path": "Tables/dbo.Users.sql",
    "lastModified": "2026-01-30T14:55:00Z",
    "script": "CREATE TABLE [dbo].[Users]...",
    "parseErrors": []
  },
  "isIdentical": true,
  "lastCompared": "2026-01-31T10:25:00Z"
}
```

##### GET /api/subscriptions/{id}/objects/{objectName}/script

Get object script from database or file.

**Query Parameters:** `?source=database|file&format=create|alter`

**Response:** `200 OK` | `404 Not Found`

```json
{
  "objectName": "dbo.GetUsers",
  "objectType": "storedProcedure",
  "source": "database",
  "format": "create",
  "script": "CREATE PROCEDURE [dbo].[GetUsers]\n  @Active BIT = 1\nAS..."
}
```

##### GET /api/subscriptions/{id}/scripts/export

Export all database objects as scripts (full schema export).

**Query Parameters:** `?types=table,view&format=single-file|per-object`

**Response:** `200 OK` | `404 Not Found`

**Response Body (format=single-file):**
```json
{
  "format": "single-file",
  "objectCount": 85,
  "script": "-- Full schema export\n-- Generated: 2026-01-31...\n\nCREATE..."
}
```

**Response Body (format=per-object):**
```json
{
  "format": "per-object",
  "objectCount": 85,
  "scripts": [
    {
      "objectName": "dbo.Users",
      "objectType": "table",
      "suggestedPath": "Tables/dbo.Users.sql",
      "script": "CREATE TABLE [dbo].[Users]..."
    }
  ]
}
#### 2.3.7 Error Responses

All API endpoints return consistent error responses.

**HTTP Status Codes:**

| Code | Status | Description |
|------|--------|-------------|
| 400 | Bad Request | Invalid input, validation errors |
| 401 | Unauthorized | Authentication required |
| 403 | Forbidden | Insufficient permissions |
| 404 | Not Found | Resource not found |
| 409 | Conflict | Operation conflicts with current state |
| 422 | Unprocessable Entity | Valid syntax but cannot process |
| 500 | Internal Server Error | Unexpected server error |
| 503 | Service Unavailable | Service temporarily unavailable |

**Standard Error Response Body:**

```json
{
  "error": {
    "code": "VALIDATION_ERROR",
    "message": "The request contains invalid data",
    "details": "Server name is required",
    "field": "database.server",
    "traceId": "00-abc123-def456-00",
    "timestamp": "2026-01-31T10:30:00Z"
  }
}
```

**Error Codes:**

| Code | Description |
|------|-------------|
| `VALIDATION_ERROR` | Input validation failed |
| `CONNECTION_FAILED` | Database connection failed |
| `SUBSCRIPTION_NOT_FOUND` | Subscription does not exist |
| `COMPARISON_IN_PROGRESS` | Another comparison is already running |
| `FILE_ACCESS_DENIED` | Cannot access project folder |
| `DATABASE_ACCESS_DENIED` | Insufficient database permissions |
| `PARSE_ERROR` | SQL file parsing failed |
| `OBJECT_NOT_FOUND` | Specified object does not exist |
| `INTERNAL_ERROR` | Unexpected server error |

### 2.4 WebSocket/SignalR Events

The service uses SignalR for real-time bidirectional communication. Clients connect to
the hub at `http://localhost:5050/hubs/sync` and can subscribe to events per subscription.

#### 2.4.1 Connection & Subscription Management

##### Client → Server: JoinSubscription

Subscribe to events for a specific subscription.

```json
{
  "subscriptionId": "550e8400-e29b-41d4-a716-446655440000"
}
```

##### Client → Server: LeaveSubscription

Unsubscribe from events for a specific subscription.

```json
{
  "subscriptionId": "550e8400-e29b-41d4-a716-446655440000"
}
```

##### Client → Server: JoinAll

Subscribe to events for all subscriptions.

**Payload:** None

#### 2.4.2 Change Detection Events

##### Server → Client: FileChanged

File system change detected.

```json
{
  "subscriptionId": "550e8400-e29b-41d4-a716-446655440000",
  "timestamp": "2026-01-31T10:30:00Z",
  "changeType": "created | modified | deleted | renamed",
  "filePath": "Tables/dbo.NewTable.sql",
  "oldFilePath": null,
  "objectName": "dbo.NewTable",
  "objectType": "table"
}
```

> Note: `oldFilePath` is populated only for rename operations.

##### Server → Client: DatabaseChanged

Database schema change detected.

```json
{
  "subscriptionId": "550e8400-e29b-41d4-a716-446655440000",
  "timestamp": "2026-01-31T10:30:00Z",
  "changeType": "created | modified | deleted",
  "objectName": "dbo.NewTable",
  "objectType": "table",
  "modifiedBy": "DOMAIN\\username"
}
```

> Note: `modifiedBy` is populated if available from the database.

##### Server → Client: DifferencesDetected

New differences found after automatic comparison.

```json
{
  "subscriptionId": "550e8400-e29b-41d4-a716-446655440000",
  "timestamp": "2026-01-31T10:30:05Z",
  "comparisonId": "770e8400-e29b-41d4-a716-446655440002",
  "trigger": "file-change | database-change | scheduled",
  "differenceCount": 3,
  "summary": {
    "byAction": { "add": 1, "delete": 0, "change": 2 }
  }
}
```

#### 2.4.3 Comparison Progress Events

##### Server → Client: ComparisonStarted

Comparison operation started.

```json
{
  "subscriptionId": "550e8400-e29b-41d4-a716-446655440000",
  "comparisonId": "770e8400-e29b-41d4-a716-446655440002",
  "startedAt": "2026-01-31T10:30:00Z",
  "trigger": "manual",
  "estimatedObjects": 85
}
```

##### Server → Client: ComparisonProgress

Comparison progress update.

```json
{
  "subscriptionId": "550e8400-e29b-41d4-a716-446655440000",
  "comparisonId": "770e8400-e29b-41d4-a716-446655440002",
  "phase": "extracting-database | parsing-files | comparing",
  "objectsProcessed": 42,
  "totalObjects": 85,
  "percentComplete": 49,
  "currentObject": "dbo.Users"
}
```

##### Server → Client: ComparisonCompleted

Comparison operation completed.

```json
{
  "subscriptionId": "550e8400-e29b-41d4-a716-446655440000",
  "comparisonId": "770e8400-e29b-41d4-a716-446655440002",
  "completedAt": "2026-01-31T10:30:02Z",
  "duration": "PT2.5S",
  "differenceCount": 3,
  "status": "completed"
}
```

##### Server → Client: ComparisonFailed

Comparison operation failed.

```json
{
  "subscriptionId": "550e8400-e29b-41d4-a716-446655440000",
  "comparisonId": "770e8400-e29b-41d4-a716-446655440002",
  "failedAt": "2026-01-31T10:30:02Z",
  "error": {
    "code": "CONNECTION_FAILED",
    "message": "Cannot connect to database server"
  }
}
```

#### 2.4.4 Subscription Status Events

##### Server → Client: SubscriptionStateChanged

Subscription state changed (active/paused/error).

```json
{
  "subscriptionId": "550e8400-e29b-41d4-a716-446655440000",
  "timestamp": "2026-01-31T10:30:00Z",
  "previousState": "active",
  "newState": "paused | active | error | disabled",
  "reason": "User requested pause",
  "triggeredBy": "user | system | error"
}
```

##### Server → Client: SubscriptionHealthChanged

Subscription health status changed.

```json
{
  "subscriptionId": "550e8400-e29b-41d4-a716-446655440000",
  "timestamp": "2026-01-31T10:30:00Z",
  "previousHealth": "healthy",
  "newHealth": "healthy | degraded | unhealthy",
  "issues": [
    {
      "type": "database-unreachable | folder-inaccessible | stale-data",
      "message": "Cannot connect to database server",
      "since": "2026-01-31T10:25:00Z"
    }
  ]
}
```

##### Server → Client: SubscriptionCreated

New subscription was created.

```json
{
  "subscriptionId": "550e8400-e29b-41d4-a716-446655440000",
  "name": "MyProject",
  "createdAt": "2026-01-31T10:30:00Z",
  "database": {
    "server": "localhost\\SQLEXPRESS",
    "database": "MyDatabase"
  },
  "projectPath": "C:\\Projects\\MyDatabase"
}
```

##### Server → Client: SubscriptionDeleted

Subscription was deleted.

```json
{
  "subscriptionId": "550e8400-e29b-41d4-a716-446655440000",
  "name": "MyProject",
  "deletedAt": "2026-01-31T10:30:00Z"
}
```

#### 2.4.6 Service Status Events

##### Server → Client: ServiceShuttingDown

Service is shutting down gracefully.

```json
{
  "timestamp": "2026-01-31T10:30:00Z",
  "reason": "User requested shutdown",
  "gracePeriodSeconds": 30
}
```

##### Server → Client: ServiceReconnected

Client reconnected after connection loss.

```json
{
  "timestamp": "2026-01-31T10:30:00Z",
  "disconnectedAt": "2026-01-31T10:29:00Z",
  "missedEvents": 5,
  "activeSubscriptions": [
    "550e8400-e29b-41d4-a716-446655440000"
  ]
}
```

---

## 3. Use Case Coverage Analysis

This section maps user-facing use cases to specific API calls, ensuring the service API
is comprehensive enough for a complete VS Code extension experience.

### 3.1 Subscription Management Use Cases

| Use Case | API Calls | WebSocket Events |
|----------|-----------|------------------|
| **Create new subscription** | `POST /api/connections/test` → `POST /api/folders/validate` → `POST /api/subscriptions` | `SubscriptionCreated` |
| **View all subscriptions** | `GET /api/subscriptions` | - |
| **View subscription details** | `GET /api/subscriptions/{id}` | - |
| **Edit subscription settings** | `PUT /api/subscriptions/{id}` | `SubscriptionStateChanged` |
| **Delete subscription** | `DELETE /api/subscriptions/{id}` | `SubscriptionDeleted` |
| **Pause monitoring** | `POST /api/subscriptions/{id}/pause` | `SubscriptionStateChanged` |
| **Resume monitoring** | `POST /api/subscriptions/{id}/resume` | `SubscriptionStateChanged` |
| **View subscription health** | `GET /api/subscriptions/{id}` (includes health) | `SubscriptionHealthChanged` |

### 3.2 Connection & Validation Use Cases

| Use Case | API Calls | WebSocket Events |
|----------|-----------|------------------|
| **Test database connection** | `POST /api/connections/test` | - |
| **Validate project folder** | `POST /api/folders/validate` | - |
| **Check server status** | `GET /api/health` | - |
| **Get detailed service status** | `GET /api/status` | `ServiceShuttingDown` |

### 3.3 Comparison & Difference Use Cases

| Use Case | API Calls | WebSocket Events |
|----------|-----------|------------------|
| **Trigger manual comparison** | `POST /api/subscriptions/{id}/compare` | `ComparisonStarted`, `ComparisonProgress`, `ComparisonCompleted` |
| **View comparison results** | `GET /api/comparisons/{id}` | - |
| **List all differences** | `GET /api/comparisons/{id}/differences` | - |
| **View specific difference details** | `GET /api/comparisons/{id}/differences/{diffId}` | - |
| **Filter differences by type** | `GET /api/comparisons/{id}/differences?objectType=table` | - |
| **Filter differences by action** | `GET /api/comparisons/{id}/differences?action=add` | - |
| **Receive auto-comparison results** | - | `DifferencesDetected` |

### 3.4 Object Details Use Cases

| Use Case | API Calls | WebSocket Events |
|----------|-----------|------------------|
| **View object script** | `GET /api/subscriptions/{id}/objects/{objectName}` | - |
| **Export single object** | `GET /api/subscriptions/{id}/objects/{objectName}/script` | - |
| **Export multiple objects** | `POST /api/subscriptions/{id}/export` | - |
| **View object metadata** | `GET /api/subscriptions/{id}/objects/{objectName}` | - |

### 3.5 Real-Time Monitoring Use Cases

| Use Case | API Calls | WebSocket Events |
|----------|-----------|------------------|
| **Receive file change notifications** | - | `FileChanged` |
| **Receive database change notifications** | - | `DatabaseChanged` |
| **Auto-detect differences** | - | `DifferencesDetected` |
| **Monitor comparison progress** | - | `ComparisonProgress` |

### 3.6 UI Component Mapping

This table maps VS Code UI components to the API calls that power them:

| UI Component | Primary API | Real-Time Updates |
|--------------|-------------|-------------------|
| **Subscription TreeView** | `GET /api/subscriptions` | `SubscriptionCreated`, `SubscriptionDeleted`, `SubscriptionStateChanged` |
| **Differences TreeView** | `GET /api/comparisons/{id}/differences` | `DifferencesDetected`, `ComparisonCompleted` |
| **Status Bar Item** | `GET /api/health`, `GET /api/subscriptions/{id}` | `SubscriptionHealthChanged`, `DifferencesDetected` |
| **Progress Notification** | - | `ComparisonProgress` |
| **Diff Editor Panel** | `GET /api/comparisons/{id}/differences/{diffId}` | - |
| **Object Details Panel** | `GET /api/subscriptions/{id}/objects/{name}` | - |
| **Subscription Wizard** | `POST /api/connections/test`, `POST /api/folders/validate` | - |
| **Settings Editor** | `GET /api/subscriptions/{id}`, `PUT /api/subscriptions/{id}` | - |

---

## 4. Data Flow Examples

This section provides concrete, step-by-step examples of request/response flows for key scenarios.

### 4.1 Creating a New Subscription

This flow shows the complete process of creating a new subscription, from connection testing through initial comparison.

#### Step 1: User opens "Create Subscription" wizard in VS Code

**VS Code Extension → Service:** `POST /api/connections/test`

**Request:**

```json
{
  "server": "localhost\\SQLEXPRESS",
  "database": "AdventureWorks",
  "authType": "windows",
  "trustServerCertificate": true
}
```

**Response (200 OK):**

```json
{
  "success": true,
  "serverVersion": "Microsoft SQL Server 2022 (RTM-CU12)",
  "objectCounts": {
    "tables": 71,
    "views": 20,
    "storedProcedures": 10,
    "functions": 13
  },
  "permissions": { "canRead": true, "canWrite": true, "canExecuteDDL": true }
}
```

#### Step 2: User selects project folder

**VS Code Extension → Service:** `POST /api/folders/validate`

**Request:**

```json
{
  "path": "C:\\Projects\\AdventureWorks",
  "includePatterns": ["**/*.sql"],
  "excludePatterns": ["**/bin/**", "**/obj/**"]
}
```

**Response (200 OK):**

```json
{
  "valid": true,
  "exists": true,
  "writable": true,
  "sqlFileCount": 45,
  "detectedStructure": "by-type"
}
```

#### Step 3: User confirms and creates subscription

**VS Code Extension → Service:** `POST /api/subscriptions`

**Request:**

```json
{
  "name": "AdventureWorks Dev",
  "database": {
    "server": "localhost\\SQLEXPRESS",
    "database": "AdventureWorks",
    "authType": "windows"
  },
  "project": {
    "path": "C:\\Projects\\AdventureWorks",
    "structure": "by-type"
  },
  "options": {
    "autoCompare": true,
    "compareOnFileChange": true,
    "compareOnDatabaseChange": true
  }
}
```

**Response (201 Created):**

```json
{
  "id": "550e8400-e29b-41d4-a716-446655440000",
  "name": "AdventureWorks Dev",
  "state": "active",
  "createdAt": "2026-01-31T10:30:00Z"
}
```

#### Step 4: Service automatically triggers initial comparison (via WebSocket)

**Service → VS Code Extension:** WebSocket events

**Event: ComparisonStarted**

```json
{
  "subscriptionId": "550e8400-e29b-41d4-a716-446655440000",
  "comparisonId": "770e8400-e29b-41d4-a716-446655440002",
  "startedAt": "2026-01-31T10:30:01Z",
  "trigger": "subscription-created",
  "estimatedObjects": 114
}
```

*(multiple ComparisonProgress events...)*

**Event: ComparisonCompleted**

```json
{
  "subscriptionId": "550e8400-e29b-41d4-a716-446655440000",
  "comparisonId": "770e8400-e29b-41d4-a716-446655440002",
  "completedAt": "2026-01-31T10:30:05Z",
  "duration": "PT4S",
  "differenceCount": 7,
  "status": "completed"
}
```

**Event: DifferencesDetected**

```json
{
  "subscriptionId": "550e8400-e29b-41d4-a716-446655440000",
  "comparisonId": "770e8400-e29b-41d4-a716-446655440002",
  "differenceCount": 7,
  "summary": { "byAction": { "add": 3, "delete": 1, "change": 3 } }
}
```

### 4.2 Detecting and Reporting a Database Change

This flow shows how the service detects a database schema change and notifies the extension.

#### Background: Service polls database every 30 seconds

The service queries `sys.objects` to detect changes. When it detects that `dbo.NewProc` has a new `modify_date` of `2026-01-31 10:32:15`, it proceeds to notify connected clients.

#### Step 1: Service detects change and sends WebSocket notification

**Service → VS Code Extension:** WebSocket event

**Event: DatabaseChanged**

```json
{
  "subscriptionId": "550e8400-e29b-41d4-a716-446655440000",
  "timestamp": "2026-01-31T10:32:30Z",
  "changeType": "modified",
  "objectName": "dbo.NewProc",
  "objectType": "storedProcedure",
  "modifiedBy": "DOMAIN\\developer"
}
```

#### Step 2: Service auto-triggers comparison (if autoCompare is enabled)

**Event: ComparisonStarted**

```json
{
  "subscriptionId": "550e8400-e29b-41d4-a716-446655440000",
  "comparisonId": "770e8400-e29b-41d4-a716-446655440003",
  "trigger": "database-change",
  "estimatedObjects": 1
}
```

> Note: `estimatedObjects: 1` indicates incremental comparison

**Event: ComparisonCompleted**

```json
{
  "comparisonId": "770e8400-e29b-41d4-a716-446655440003",
  "completedAt": "2026-01-31T10:32:31Z",
  "duration": "PT0.8S",
  "differenceCount": 1
}
```

**Event: DifferencesDetected**

```json
{
  "subscriptionId": "550e8400-e29b-41d4-a716-446655440000",
  "comparisonId": "770e8400-e29b-41d4-a716-446655440003",
  "trigger": "database-change",
  "differenceCount": 1,
  "summary": { "byAction": { "add": 0, "delete": 0, "change": 1 } }
}
```

#### Step 3: Extension updates UI (TreeView badge, status bar)

The extension receives the events and updates the UI:

**TreeView Display:**
```
SQL SYNC
├── AdventureWorks Dev
│   └── Differences (1)  ← badge updated
│       └── 📝 dbo.NewProc (changed)
```

**Status Bar:** `SQL Sync: 1 difference`

### 4.3 Displaying Differences in the TreeView

This flow shows how the extension fetches and displays differences for user review.

#### Step 1: User expands "Differences" node in TreeView

**VS Code Extension → Service:** `GET /api/comparisons/{compId}/differences`

**Request:** `GET /api/comparisons/770e8400-e29b-41d4-a716-446655440003/differences`

**Response (200 OK):**

```json
{
  "comparisonId": "770e8400-e29b-41d4-a716-446655440003",
  "differences": [
    {
      "id": "diff-001",
      "objectType": "table",
      "objectName": "dbo.NewTable",
      "action": "add",
      "direction": "database-only"
    },
    {
      "id": "diff-002",
      "objectType": "storedProcedure",
      "objectName": "dbo.GetUsers",
      "action": "change",
      "direction": "different"
    },
    {
      "id": "diff-003",
      "objectType": "view",
      "objectName": "dbo.OldView",
      "action": "delete",
      "direction": "file-only"
    }
  ],
  "totalCount": 3
}
```

#### Step 2: Extension renders TreeView with differences

**TreeView Display:**
```
SQL SYNC
└── AdventureWorks Dev
    └── Differences (3)
        ├── 📦 Tables
        │   └── ➕ dbo.NewTable         [View Script]
        ├── 📝 Stored Procedures
        │   └── 📝 dbo.GetUsers         [View Diff]
        └── 📑 Views
            └── ➖ dbo.OldView           [View Script]
```

#### Step 3: User clicks "View Diff" on dbo.GetUsers

**VS Code Extension → Service:** `GET /api/comparisons/{id}/differences/{diffId}`

**Request:** `GET /api/comparisons/770e8400-.../differences/diff-002`

**Response (200 OK):**

```json
{
  "id": "diff-002",
  "objectType": "storedProcedure",
  "objectName": "dbo.GetUsers",
  "action": "change",
  "databaseScript": "CREATE PROCEDURE [dbo].[GetUsers]\n@Active BIT = 1\n...",
  "fileScript": "CREATE PROCEDURE [dbo].[GetUsers]\n@Status INT\n...",
  "unifiedDiff": "@@ -1,10 +1,12 @@\n CREATE PROCEDURE [dbo].[GetUsers]...",
  "sideBySideDiff": {
    "lines": [
      { "lineNumber": 1, "left": "CREATE PROCEDURE...", "right": "CREATE...", "type": "unchanged" },
      { "lineNumber": 2, "left": "@Active BIT = 1", "right": "@Status INT", "type": "modified" }
    ]
  }
}
```

#### Step 4: Extension opens VS Code Diff Editor

The extension creates two virtual documents from `databaseScript` and `fileScript`, then opens them in VS Code's built-in diff editor:

| Database Version | File Version |
|------------------|--------------|
| `CREATE PROCEDURE` | `CREATE PROCEDURE` |
| `[dbo].[GetUsers]` | `[dbo].[GetUsers]` |
| `- @Active BIT = 1` | `+ @Status INT` ← highlighted |
| `AS` | `AS` |
| `BEGIN` | `BEGIN` |
| `...` | `...` |

---

## 5. Summary

This specification defines a comprehensive API for the .NET Background Service component
that enables the VS Code extension to provide a complete, professional user experience
for SQL Server database schema comparison and difference detection.

> **Note:** This service is **read-only**. It detects and reports differences between the database
> and project files but does NOT make any changes to either the database or the file system.
> Synchronization operations (applying changes) are the responsibility of the VS Code extension
> or other client tools.

### 5.1 API Surface Summary

| Category | REST Endpoints | WebSocket Events |
|----------|----------------|------------------|
| Health & Status | 2 | 2 |
| Connection Management | 2 | 0 |
| Subscription Management | 7 | 4 |
| Comparison Operations | 3 | 4 |
| Object Details | 4 | 0 |
| **Total** | **18** | **10** |

### 5.2 Key Design Decisions

1. **Read-Only Design**: The service only reads from the database and file system to detect
   and report differences. It never modifies the database or writes to files.

2. **Asynchronous Comparison Operations**: Long-running comparison operations return immediately
   with an operation ID and send progress via WebSocket.

3. **Comprehensive Diff Support**: The API provides both unified diff and side-by-side diff
   formats, plus raw scripts for maximum flexibility in displaying differences.

4. **Subscription-Centric Model**: All operations are scoped to subscriptions, making it
   easy to manage multiple database-folder pairs.

5. **Real-Time Updates**: WebSocket events provide immediate feedback for all state changes,
   enabling responsive UI updates.

6. **Error Transparency**: Consistent error response format with trace IDs for debugging.

### 5.3 Extension Implementation Notes

The VS Code extension can implement a complete user experience using only HTTP and WebSocket
calls to this service. The extension does NOT need to:

- Parse or understand T-SQL syntax
- Implement schema comparison logic
- Connect directly to SQL Server
- Implement change detection algorithms

The extension is responsible for:

- Rendering UI components (TreeView, WebView, notifications)
- Managing WebSocket connection and event subscriptions
- Orchestrating user workflows through API calls
- Presenting diffs using VS Code's built-in diff editor
- **Implementing synchronization logic** (if desired) - writing files or executing DDL scripts

---

*Document Version: 1.1*
*Last Updated: 2026-02-01*
*Related Documents: [DESIGN.md](./DESIGN.md)*

