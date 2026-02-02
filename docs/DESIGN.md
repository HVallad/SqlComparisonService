# SQL Project Synchronization Service - Architecture Design

## Executive Summary

This document outlines the architectural design for a bidirectional synchronization service between SQL Server databases and local SQL project folders (.sql files). The service integrates with Visual Studio Code and leverages Microsoft's DacFX for accurate schema comparison.

---

## Table of Contents

1. [System Overview](#1-system-overview)
2. [Architectural Options](#2-architectural-options)
3. [DacFX Integration Strategy](#3-dacfx-integration-strategy)
4. [Change Detection Mechanisms](#4-change-detection-mechanisms)
5. [VS Code Communication](#5-vs-code-communication)
6. [Data Structures](#6-data-structures)
7. [Performance Considerations](#7-performance-considerations)
8. [Recommended Architecture](#8-recommended-architecture)

---

## 1. System Overview

### 1.1 Core Requirements

| Requirement | Description |
|-------------|-------------|
| **Schema Synchronization** | Compare SQL Server database schema with .sql files |
| **Real-time Detection** | Detect changes in both database and file system |
| **Subscription Model** | Link database connections to project folders |
| **VS Code Integration** | Provide notifications and UI within VS Code |
| **Bidirectional Awareness** | Track changes from both database and file system |

### 1.2 Supported Object Types

- Tables (columns, constraints, indexes)
- Stored Procedures
- Views
- Functions (Scalar, Table-valued, Inline)
- Triggers
- User-defined Types
- Schemas
- Synonyms

### 1.3 High-Level Component Diagram

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                              VS Code Extension                               │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐ │
│  │   UI/UX     │  │ Subscription│  │ Diff Viewer │  │ Command Palette     │ │
│  │  TreeView   │  │   Manager   │  │             │  │ Integration         │ │
│  └──────┬──────┘  └──────┬──────┘  └──────┬──────┘  └──────────┬──────────┘ │
└─────────┼────────────────┼────────────────┼────────────────────┼────────────┘
          │                │                │                    │
          └────────────────┴────────────────┴────────────────────┘
                                    │
                            ┌───────┴───────┐
                            │  IPC/RPC Layer │
                            └───────┬───────┘
                                    │
┌───────────────────────────────────┴─────────────────────────────────────────┐
│                         Synchronization Service                              │
│  ┌─────────────────┐  ┌─────────────────┐  ┌─────────────────────────────┐  │
│  │  File System    │  │   Database      │  │      Comparison Engine      │  │
│  │    Watcher      │  │   Monitor       │  │        (DacFX)              │  │
│  └────────┬────────┘  └────────┬────────┘  └─────────────┬───────────────┘  │
│           │                    │                         │                   │
│  ┌────────┴────────────────────┴─────────────────────────┴───────────────┐  │
│  │                      State Management & Caching                        │  │
│  └───────────────────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────────────────┘
          │                                              │
          ▼                                              ▼
┌─────────────────────┐                    ┌─────────────────────────────────┐
│   SQL Project       │                    │       SQL Server Database       │
│   (.sql files)      │                    │                                 │
└─────────────────────┘                    └─────────────────────────────────┘
```

---

## 2. Architectural Options

### 2.1 Option A: Monolithic VS Code Extension

**Description:** All functionality contained within a single VS Code extension written in TypeScript/JavaScript with native Node.js bindings to DacFX (.NET).

```
┌──────────────────────────────────────────────────────────────┐
│                    VS Code Extension                          │
│  ┌────────────────────────────────────────────────────────┐  │
│  │                    TypeScript Core                      │  │
│  │  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐  │  │
│  │  │ File Watcher │  │  DB Monitor  │  │    UI/UX     │  │  │
│  │  └──────────────┘  └──────────────┘  └──────────────┘  │  │
│  │                         │                               │  │
│  │  ┌──────────────────────┴───────────────────────────┐  │  │
│  │  │         Node.js ←→ .NET Interop                   │  │  │
│  │  │         (edge.js / child_process)                 │  │  │
│  │  └──────────────────────┬───────────────────────────┘  │  │
│  │                         │                               │  │
│  │  ┌──────────────────────┴───────────────────────────┐  │  │
│  │  │              DacFX .NET Library                   │  │  │
│  │  └──────────────────────────────────────────────────┘  │  │
│  └────────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────────┘
```

| Pros | Cons |
|------|------|
| ✅ Single deployment unit | ❌ Complex Node.js/.NET interop |
| ✅ Simpler architecture | ❌ Extension host may become blocked |
| ✅ No external dependencies | ❌ Limited scalability |
| ✅ Easy user installation | ❌ Memory constraints in extension host |
| | ❌ Harder to test .NET components |

### 2.2 Option B: Hybrid Architecture (Recommended)

**Description:** VS Code extension (TypeScript) communicates with a separate .NET background service via IPC. The .NET service handles DacFX operations and heavy lifting.

```
┌─────────────────────────────────────────┐
│         VS Code Extension               │
│         (TypeScript/Node.js)            │
│  ┌───────────────────────────────────┐  │
│  │  • UI Components (TreeView, etc.) │  │
│  │  • File System Watcher            │  │
│  │  • Command Handlers               │  │
│  │  • IPC Client                     │  │
│  └───────────────────┬───────────────┘  │
└──────────────────────┼──────────────────┘
                       │
            ┌──────────┴──────────┐
            │   IPC / gRPC / HTTP │
            │   (localhost:port)  │
            └──────────┬──────────┘
                       │
┌──────────────────────┼──────────────────┐
│     .NET Background Service             │
│  ┌───────────────────┴───────────────┐  │
│  │  • DacFX Schema Comparison        │  │
│  │  • Database Change Detection      │  │
│  │  • Model Building & Caching       │  │
│  │  • Subscription Management        │  │
│  └───────────────────────────────────┘  │
└─────────────────────────────────────────┘
```

| Pros | Cons |
|------|------|
| ✅ Clean separation of concerns | ❌ Two components to deploy |
| ✅ Native DacFX integration | ❌ IPC complexity |
| ✅ Non-blocking VS Code UI | ❌ Service lifecycle management |
| ✅ Better memory management | ❌ Slightly more complex debugging |
| ✅ Independent scaling | |
| ✅ Easier testing | |
| ✅ Can run as Windows Service | |

### 2.3 Option C: Language Server Protocol (LSP) Based

**Description:** Implement the service as an LSP server, leveraging VS Code's built-in LSP client infrastructure.

```
┌─────────────────────────────────────────┐
│         VS Code Extension               │
│  ┌───────────────────────────────────┐  │
│  │  LSP Client (built-in support)    │  │
│  │  Custom Commands & UI             │  │
│  └───────────────────┬───────────────┘  │
└──────────────────────┼──────────────────┘
                       │
            ┌──────────┴──────────┐
            │  LSP Protocol       │
            │  (JSON-RPC/stdio)   │
            └──────────┬──────────┘
                       │
┌──────────────────────┼──────────────────┐
│     LSP Server (.NET)                   │
│  ┌───────────────────┴───────────────┐  │
│  │  • Custom LSP Methods             │  │
│  │  • DacFX Integration              │  │
│  │  • File/DB Synchronization        │  │
│  └───────────────────────────────────┘  │
└─────────────────────────────────────────┘
```

| Pros | Cons |
|------|------|
| ✅ Leverages VS Code LSP infrastructure | ❌ LSP designed for language features, not sync |
| ✅ Well-defined protocol | ❌ May feel forced/unnatural |
| ✅ Good tooling support | ❌ Limited notification patterns |
| ✅ stdio communication is simple | ❌ Custom methods need careful design |

### 2.4 Option D: MCP (Model Context Protocol) Based Service

**Description:** Use the Model Context Protocol pattern with a standalone service that can be consumed by VS Code and other tools.

| Pros | Cons |
|------|------|
| ✅ Modern, flexible protocol | ❌ Newer technology, less established |
| ✅ Tool-agnostic design | ❌ May be overkill for this use case |
| ✅ Built-in resource/tool patterns | ❌ Learning curve |

---

### 2.5 Comparison Matrix

| Criteria | Option A | Option B | Option C | Option D |
|----------|----------|----------|----------|----------|
| **Development Complexity** | Medium | Medium-High | Medium | High |
| **Performance** | Low | High | Medium | High |
| **Maintainability** | Medium | High | High | Medium |
| **User Experience** | Good | Excellent | Good | Good |
| **DacFX Integration** | Difficult | Native | Native | Native |
| **Scalability** | Low | High | Medium | High |
| **Deployment** | Simple | Medium | Medium | Complex |

**Recommendation:** Option B (Hybrid Architecture) provides the best balance of performance, maintainability, and user experience.

---

## 3. DacFX Integration Strategy

### 3.1 DacFX Overview

Microsoft DacFX (Data-tier Application Framework) provides:
- **TSqlModel**: In-memory representation of a database schema
- **SchemaComparison**: Compare two schema sources
- **DacPackage**: Deploy and extract .dacpac files
- **ScriptDom**: Parse and generate T-SQL scripts

### 3.2 Key DacFX Components to Leverage

```csharp
// Core namespaces
Microsoft.SqlServer.Dac              // DacPackage, DacServices
Microsoft.SqlServer.Dac.Model        // TSqlModel, TSqlObject
Microsoft.SqlServer.Dac.Compare      // SchemaComparison, SchemaComparisonResult
Microsoft.SqlServer.TransactSql.ScriptDom  // T-SQL parsing
```

### 3.3 Schema Model Building Approaches

#### Approach A: Build from Database (Live Connection)

```
┌─────────────────┐         ┌─────────────────┐
│   SQL Server    │ ──────► │   TSqlModel     │
│   Database      │  Extract│   (In-Memory)   │
└─────────────────┘         └─────────────────┘
```

```csharp
// Pseudo-code for database extraction
public TSqlModel BuildModelFromDatabase(string connectionString)
{
    using var package = DacServices.Extract(connectionString, options);
    return TSqlModel.LoadFromDacpac(package);
}
```

**Pros:** Accurate representation of live database
**Cons:** Requires database connectivity, slower for large databases

#### Approach B: Build from SQL Files (File System)

```
┌─────────────────┐         ┌─────────────────┐
│   .sql Files    │ ──────► │   TSqlModel     │
│   (Project)     │  Parse  │   (In-Memory)   │
└─────────────────┘         └─────────────────┘
```

```csharp
// Pseudo-code for file-based model building
public TSqlModel BuildModelFromFiles(string projectPath)
{
    var model = new TSqlModel(SqlServerVersion.Sql150, new TSqlModelOptions());
    foreach (var sqlFile in Directory.GetFiles(projectPath, "*.sql", SearchOption.AllDirectories))
    {
        var script = File.ReadAllText(sqlFile);
        model.AddObjects(script);
    }
    return model;
}
```

**Pros:** Works offline, faster for incremental changes
**Cons:** May not capture all database-specific settings

#### Approach C: Hybrid (Recommended)

- Cache database schema as .dacpac
- Build file model incrementally
- Compare cached models with incremental updates

### 3.4 Schema Comparison Strategy

```
┌─────────────────────────────────────────────────────────────────┐
│                    Schema Comparison Flow                        │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│   ┌─────────────┐                      ┌─────────────┐          │
│   │  Database   │                      │  SQL Files  │          │
│   │   Model     │                      │   Model     │          │
│   └──────┬──────┘                      └──────┬──────┘          │
│          │                                    │                  │
│          └──────────────┬─────────────────────┘                  │
│                         │                                        │
│                         ▼                                        │
│          ┌──────────────────────────────┐                        │
│          │     SchemaComparison         │                        │
│          │     • Compare()              │                        │
│          │     • GetDifferences()       │                        │
│          └──────────────┬───────────────┘                        │
│                         │                                        │
│                         ▼                                        │
│          ┌──────────────────────────────┐                        │
│          │  SchemaComparisonResult      │                        │
│          │  • Differences[]             │                        │
│          │  • GenerateScript()          │                        │
│          └──────────────────────────────┘                        │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

### 3.5 Comparison Options Configuration

```csharp
public class ComparisonOptions
{
    // Object types to compare
    public bool IncludeTables { get; set; } = true;
    public bool IncludeViews { get; set; } = true;
    public bool IncludeStoredProcedures { get; set; } = true;
    public bool IncludeFunctions { get; set; } = true;
    public bool IncludeTriggers { get; set; } = true;

    // Comparison behavior
    public bool IgnoreWhitespace { get; set; } = true;
    public bool IgnoreComments { get; set; } = false;
    public bool IgnoreColumnOrder { get; set; } = true;
    public bool IgnoreTableOptions { get; set; } = false;

    // Extended properties
    public bool IncludeExtendedProperties { get; set; } = false;
    public bool IncludePermissions { get; set; } = false;
}
```

### 3.6 DacFX NuGet Packages

```xml
<!-- Required packages -->
<PackageReference Include="Microsoft.SqlServer.DacFx" Version="162.x.x" />
<PackageReference Include="Microsoft.SqlServer.TransactSql.ScriptDom" Version="161.x.x" />
```

---

## 4. Change Detection Mechanisms

### 4.1 File System Monitoring

#### Option A: FileSystemWatcher (.NET)

```csharp
public class SqlFileWatcher : IDisposable
{
    private FileSystemWatcher _watcher;

    public void StartWatching(string projectPath)
    {
        _watcher = new FileSystemWatcher(projectPath)
        {
            Filter = "*.sql",
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
        };

        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileCreated;
        _watcher.Deleted += OnFileDeleted;
        _watcher.Renamed += OnFileRenamed;
        _watcher.EnableRaisingEvents = true;
    }
}
```

| Pros | Cons |
|------|------|
| ✅ Built into .NET | ❌ Can miss events under heavy load |
| ✅ Low overhead | ❌ Buffer overflow issues |
| ✅ Real-time notifications | ❌ Duplicate events common |

**Mitigation:** Implement debouncing and event coalescing.

#### Option B: VS Code File System API (Extension Side)

```typescript
// In VS Code extension
const watcher = vscode.workspace.createFileSystemWatcher('**/*.sql');
watcher.onDidChange(uri => notifyService('file-changed', uri));
watcher.onDidCreate(uri => notifyService('file-created', uri));
watcher.onDidDelete(uri => notifyService('file-deleted', uri));
```

| Pros | Cons |
|------|------|
| ✅ Integrated with VS Code | ❌ Only works when VS Code is open |
| ✅ Reliable within editor context | ❌ Misses external changes sometimes |
| ✅ Respects .gitignore patterns | |

#### Option C: Hybrid (Recommended)

- VS Code extension watches for immediate feedback
- .NET service uses FileSystemWatcher as backup
- Periodic full scan for reconciliation

### 4.2 Database Change Detection

#### Option A: SQL Server Query Notifications (SqlDependency)

```csharp
public class DatabaseChangeMonitor
{
    public void StartMonitoring(string connectionString)
    {
        SqlDependency.Start(connectionString);

        using var connection = new SqlConnection(connectionString);
        var command = new SqlCommand("SELECT * FROM sys.objects", connection);
        var dependency = new SqlDependency(command);
        dependency.OnChange += OnDatabaseChanged;
    }
}
```

| Pros | Cons |
|------|------|
| ✅ Real-time notifications | ❌ Limited query support |
| ✅ Built into SQL Server | ❌ Requires Service Broker |
| | ❌ Connection overhead |
| | ❌ Complex setup |

#### Option B: Polling with Change Tracking

```sql
-- Query to detect schema changes
SELECT
    o.name AS ObjectName,
    o.type_desc AS ObjectType,
    o.modify_date AS LastModified
FROM sys.objects o
WHERE o.modify_date > @LastCheckTime
    AND o.type IN ('U', 'V', 'P', 'FN', 'IF', 'TF', 'TR')
ORDER BY o.modify_date DESC
```

| Pros | Cons |
|------|------|
| ✅ Simple implementation | ❌ Not truly real-time |
| ✅ Works with any SQL Server | ❌ Polling overhead |
| ✅ No special permissions needed | ❌ May miss rapid changes |
| ✅ Reliable | |

#### Option C: Extended Events / SQL Trace

```sql
-- Create Extended Events session for DDL tracking
CREATE EVENT SESSION [SchemaChanges] ON SERVER
ADD EVENT sqlserver.object_created,
ADD EVENT sqlserver.object_altered,
ADD EVENT sqlserver.object_deleted
ADD TARGET package0.ring_buffer
WITH (STARTUP_STATE = ON);
```

| Pros | Cons |
|------|------|
| ✅ Comprehensive tracking | ❌ Requires elevated permissions |
| ✅ Real-time | ❌ Performance impact |
| ✅ Detailed event data | ❌ Complex to consume |

#### Option D: DDL Triggers

```sql
CREATE TRIGGER [TrackSchemaChanges]
ON DATABASE
FOR CREATE_TABLE, ALTER_TABLE, DROP_TABLE,
    CREATE_PROCEDURE, ALTER_PROCEDURE, DROP_PROCEDURE,
    CREATE_VIEW, ALTER_VIEW, DROP_VIEW
AS
BEGIN
    -- Log change to tracking table
    INSERT INTO SchemaChangeLog (EventType, ObjectName, EventTime)
    SELECT EVENTDATA().value('(/EVENT_INSTANCE/EventType)[1]', 'NVARCHAR(100)'),
           EVENTDATA().value('(/EVENT_INSTANCE/ObjectName)[1]', 'NVARCHAR(256)'),
           GETUTCDATE();
END
```

| Pros | Cons |
|------|------|
| ✅ Guaranteed capture | ❌ Requires DB modification |
| ✅ Custom logging | ❌ May not be allowed in prod |
| ✅ Detailed event data | ❌ Maintenance overhead |

### 4.3 Recommended Change Detection Strategy

```
┌────────────────────────────────────────────────────────────────────┐
│                  Change Detection Architecture                      │
├────────────────────────────────────────────────────────────────────┤
│                                                                     │
│  FILE SYSTEM                         DATABASE                       │
│  ──────────                          ────────                       │
│  ┌─────────────────┐                 ┌─────────────────┐           │
│  │ VS Code Watcher │ (Primary)       │ Polling Monitor │ (Primary) │
│  │ (TypeScript)    │                 │ (sys.objects)   │           │
│  └────────┬────────┘                 └────────┬────────┘           │
│           │                                   │                     │
│  ┌────────┴────────┐                 ┌────────┴────────┐           │
│  │ .NET FSWatcher  │ (Backup)        │ Query Notify    │ (Optional)│
│  │ (Background)    │                 │ (If available)  │           │
│  └────────┬────────┘                 └────────┬────────┘           │
│           │                                   │                     │
│           └───────────────┬───────────────────┘                     │
│                           │                                         │
│                           ▼                                         │
│           ┌───────────────────────────────────┐                     │
│           │       Change Aggregator           │                     │
│           │  • Debouncing (500ms default)     │                     │
│           │  • Deduplication                  │                     │
│           │  • Batching                       │                     │
│           └───────────────┬───────────────────┘                     │
│                           │                                         │
│                           ▼                                         │
│           ┌───────────────────────────────────┐                     │
│           │     Comparison Trigger            │                     │
│           └───────────────────────────────────┘                     │
│                                                                     │
└────────────────────────────────────────────────────────────────────┘
```

### 4.4 Polling Configuration

```csharp
public class PollingConfiguration
{
    /// <summary>Interval for database schema checks</summary>
    public TimeSpan DatabasePollInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Debounce time for file system events</summary>
    public TimeSpan FileSystemDebounce { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Full reconciliation interval</summary>
    public TimeSpan FullScanInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Enable aggressive polling for dev environments</summary>
    public bool AggressivePolling { get; set; } = false;
}

---

## 5. VS Code Communication

### 5.1 Communication Protocol Options

#### Option A: HTTP/REST API

```
┌─────────────────┐         ┌─────────────────┐
│   VS Code Ext   │  HTTP   │  .NET Service   │
│                 │ ──────► │  (Kestrel)      │
│                 │ ◄────── │                 │
└─────────────────┘   JSON  └─────────────────┘
```

```typescript
// Extension client
class SyncServiceClient {
    private baseUrl = 'http://localhost:5050';

    async getSubscriptions(): Promise<Subscription[]> {
        const response = await fetch(`${this.baseUrl}/api/subscriptions`);
        return response.json();
    }

    async getDifferences(subscriptionId: string): Promise<Difference[]> {
        const response = await fetch(`${this.baseUrl}/api/subscriptions/${subscriptionId}/differences`);
        return response.json();
    }
}
```

| Pros | Cons |
|------|------|
| ✅ Well understood | ❌ Request/response only (no push) |
| ✅ Easy to debug | ❌ Need SSE/WebSocket for notifications |
| ✅ Cross-platform | ❌ HTTP overhead |
| ✅ Testable with curl/Postman | |

#### Option B: gRPC

```protobuf
syntax = "proto3";

service SyncService {
    rpc GetSubscriptions(Empty) returns (SubscriptionList);
    rpc CreateSubscription(CreateSubscriptionRequest) returns (Subscription);
    rpc GetDifferences(GetDifferencesRequest) returns (DifferenceList);
    rpc StreamChanges(StreamRequest) returns (stream ChangeEvent);
}

message ChangeEvent {
    string subscription_id = 1;
    ChangeType type = 2;
    string object_name = 3;
    string object_type = 4;
    google.protobuf.Timestamp detected_at = 5;
}
```

| Pros | Cons |
|------|------|
| ✅ Efficient binary protocol | ❌ More complex setup |
| ✅ Streaming support built-in | ❌ gRPC-web needed for browser |
| ✅ Strong typing | ❌ Harder to debug |
| ✅ Bidirectional streaming | ❌ Node.js gRPC can be tricky |

#### Option C: Named Pipes (Windows) / Unix Domain Sockets

```csharp
// .NET Service
var server = new NamedPipeServerStream("SqlSyncService",
    PipeDirection.InOut,
    maxConnections: 10);
```

```typescript
// Node.js client
import * as net from 'net';
const client = net.createConnection('\\\\.\\pipe\\SqlSyncService');
```

| Pros | Cons |
|------|------|
| ✅ Fast (no network stack) | ❌ Platform-specific |
| ✅ Low latency | ❌ More complex client code |
| ✅ Secure (local only) | ❌ Less tooling support |

#### Option D: WebSocket (Recommended for Notifications)

```
┌─────────────────┐         ┌─────────────────┐
│   VS Code Ext   │   WS    │  .NET Service   │
│                 │ ◄─────► │                 │
│                 │  Full   │                 │
│                 │ Duplex  │                 │
└─────────────────┘         └─────────────────┘
```

| Pros | Cons |
|------|------|
| ✅ Bidirectional | ❌ Connection management |
| ✅ Real-time push | ❌ Reconnection logic needed |
| ✅ Good Node.js support | |
| ✅ JSON or binary | |

### 5.2 Recommended: Hybrid HTTP + WebSocket

```
┌────────────────────────────────────────────────────────────────────┐
│                  Communication Architecture                         │
├────────────────────────────────────────────────────────────────────┤
│                                                                     │
│   VS Code Extension                    .NET Service                 │
│   ──────────────────                   ────────────                 │
│                                                                     │
│   ┌─────────────────┐                 ┌─────────────────┐          │
│   │  HTTP Client    │ ──── REST ────► │  REST API       │          │
│   │  (fetch/axios)  │   (Commands)    │  (ASP.NET Core) │          │
│   └─────────────────┘                 └─────────────────┘          │
│                                                                     │
│   ┌─────────────────┐                 ┌─────────────────┐          │
│   │  WebSocket      │ ◄── Events ───► │  WebSocket Hub  │          │
│   │  Client         │  (Real-time)    │  (SignalR)      │          │
│   └─────────────────┘                 └─────────────────┘          │
│                                                                     │
└────────────────────────────────────────────────────────────────────┘
```

### 5.3 API Design

#### REST Endpoints

```
POST   /api/subscriptions              Create subscription
GET    /api/subscriptions              List all subscriptions
GET    /api/subscriptions/{id}         Get subscription details
DELETE /api/subscriptions/{id}         Remove subscription
PUT    /api/subscriptions/{id}         Update subscription

GET    /api/subscriptions/{id}/differences     Get current differences
POST   /api/subscriptions/{id}/compare         Trigger comparison
POST   /api/subscriptions/{id}/sync            Apply sync (with options)

GET    /api/connections/test           Test database connection
GET    /api/health                     Service health check
GET    /api/status                     Service status
```

#### WebSocket Events

```typescript
// Events from Service to Extension
interface ChangeDetectedEvent {
    type: 'change-detected';
    subscriptionId: string;
    changes: {
        source: 'database' | 'filesystem';
        objectType: string;
        objectName: string;
        changeType: 'added' | 'modified' | 'deleted';
    }[];
}

interface ComparisonCompleteEvent {
    type: 'comparison-complete';
    subscriptionId: string;
    differenceCount: number;
    timestamp: string;
}

interface ServiceStatusEvent {
    type: 'service-status';
    status: 'healthy' | 'degraded' | 'error';
    message?: string;
}

// Events from Extension to Service
interface SubscribeToChangesEvent {
    type: 'subscribe';
    subscriptionIds: string[];
}
```

### 5.4 Service Discovery

```typescript
// Extension looks for service on startup
class ServiceDiscovery {
    private ports = [5050, 5051, 5052]; // Fallback ports

    async findService(): Promise<string | null> {
        for (const port of this.ports) {
            try {
                const response = await fetch(`http://localhost:${port}/api/health`);
                if (response.ok) {
                    return `http://localhost:${port}`;
                }
            } catch {
                continue;
            }
        }
        return null;
    }
}
```

### 5.5 VS Code Extension UI Components

```
┌─────────────────────────────────────────────────────────────────┐
│                     VS Code UI Integration                       │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│  1. TREE VIEW (Activity Bar)                                    │
│     ├── Subscriptions                                           │
│     │   ├── 📁 MyProject ↔ localhost\SQLEXPRESS.MyDB            │
│     │   │   ├── ⚠️ Tables (3 differences)                       │
│     │   │   ├── ✅ Views (synchronized)                         │
│     │   │   └── ⚠️ Stored Procedures (1 difference)             │
│     │   └── 📁 OtherProject ↔ server.Production                 │
│     └── + Add Subscription                                      │
│                                                                  │
│  2. STATUS BAR ITEM                                             │
│     [SQL Sync: 4 differences | ⟳ Syncing...]                    │
│                                                                  │
│  3. NOTIFICATIONS                                               │
│     "Schema change detected in database MyDB"                   │
│     [View Differences] [Dismiss]                                │
│                                                                  │
│  4. WEBVIEW PANEL                                               │
│     ┌──────────────────────────────────────────────────────┐   │
│     │ Schema Comparison                                     │   │
│     │ ────────────────────────────────────────────────────  │   │
│     │ Database                    │  SQL Files              │   │
│     │ ─────────                   │  ─────────              │   │
│     │ CREATE TABLE Users (...)    │  CREATE TABLE Users (   │   │
│     │                             │    -- different cols    │   │
│     │ [Apply to Files] [Apply to DB] [Ignore]               │   │
│     └──────────────────────────────────────────────────────┘   │
│                                                                  │
│  5. COMMAND PALETTE                                             │
│     > SQL Sync: Create Subscription                             │
│     > SQL Sync: View Differences                                │
│     > SQL Sync: Refresh                                         │
│     > SQL Sync: Start/Stop Service                              │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘

---

## 6. Data Structures

### 6.1 Core Domain Models

```csharp
// ==================== Subscription Management ====================

/// <summary>
/// Represents a link between a database and a project folder
/// </summary>
public class Subscription
{
    public Guid Id { get; set; }
    public string Name { get; set; }
    public DatabaseConnection Database { get; set; }
    public ProjectFolder Project { get; set; }
    public SubscriptionState State { get; set; }
    public ComparisonOptions Options { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastComparedAt { get; set; }
    public DateTime? LastSyncedAt { get; set; }
}

public enum SubscriptionState
{
    Active,
    Paused,
    Error,
    Comparing,
    Syncing
}

/// <summary>
/// Database connection configuration
/// </summary>
public class DatabaseConnection
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Server { get; set; }
    public string Database { get; set; }
    public AuthenticationType AuthType { get; set; }
    public string? Username { get; set; }
    public string? EncryptedPassword { get; set; }
    public bool TrustServerCertificate { get; set; }
    public int ConnectionTimeoutSeconds { get; set; } = 30;

    // Computed
    public string ConnectionString => BuildConnectionString();
}

public enum AuthenticationType
{
    WindowsIntegrated,
    SqlServer,
    AzureAD,
    AzureADInteractive
}

/// <summary>
/// Project folder configuration
/// </summary>
public class ProjectFolder
{
    public string RootPath { get; set; }
    public string[] IncludePatterns { get; set; } = ["**/*.sql"];
    public string[] ExcludePatterns { get; set; } = ["**/bin/**", "**/obj/**"];
    public FolderStructure Structure { get; set; } = FolderStructure.ByObjectType;
}

public enum FolderStructure
{
    Flat,              // All .sql files in root
    ByObjectType,      // /Tables, /Views, /StoredProcedures
    BySchema,          // /dbo, /sales, /hr
    BySchemaAndType    // /dbo/Tables, /dbo/Views
}
```

### 6.2 Schema Comparison Models

```csharp
// ==================== Comparison Results ====================

/// <summary>
/// Result of a schema comparison
/// </summary>
public class ComparisonResult
{
    public Guid Id { get; set; }
    public Guid SubscriptionId { get; set; }
    public DateTime ComparedAt { get; set; }
    public TimeSpan Duration { get; set; }
    public ComparisonStatus Status { get; set; }
    public List<SchemaDifference> Differences { get; set; } = [];
    public ComparisonSummary Summary { get; set; }
}

public class ComparisonSummary
{
    public int TotalDifferences { get; set; }
    public int Additions { get; set; }
    public int Modifications { get; set; }
    public int Deletions { get; set; }
    public Dictionary<string, int> ByObjectType { get; set; } = [];
}

public enum ComparisonStatus
{
    Synchronized,     // No differences
    HasDifferences,   // Differences found
    Error,            // Comparison failed
    Partial           // Some objects couldn't be compared
}

/// <summary>
/// Represents a single difference between database and files
/// </summary>
public class SchemaDifference
{
    public Guid Id { get; set; }
    public string ObjectName { get; set; }
    public string SchemaName { get; set; }
    public SqlObjectType ObjectType { get; set; }
    public DifferenceType DifferenceType { get; set; }
    public DifferenceSource Source { get; set; }

    // Content
    public string? DatabaseDefinition { get; set; }
    public string? FileDefinition { get; set; }
    public string? FilePath { get; set; }

    // For modifications: specific changes
    public List<PropertyDifference>? PropertyChanges { get; set; }
}

public enum SqlObjectType
{
	Table,
	View,
	StoredProcedure,
	ScalarFunction,
	TableValuedFunction,
	InlineTableValuedFunction,
	Trigger,
	Index,
	Constraint,
	UserDefinedType,
	Schema,
	Synonym,
	Login,
	Role,
	Unknown
}

public enum DifferenceType
{
    Add,       // Object exists in source but not target
    Delete,    // Object exists in target but not source
    Modify,    // Object exists in both but differs
    Rename     // Object was renamed (detected via similarity)
}

public enum DifferenceSource
{
    Database,   // Change originates from database
    FileSystem  // Change originates from files
}

public class PropertyDifference
{
    public string PropertyName { get; set; }
    public string? DatabaseValue { get; set; }
    public string? FileValue { get; set; }
}
```

### 6.3 Change Tracking Models

```csharp
// ==================== Change Detection ====================

/// <summary>
/// Tracks detected changes before comparison
/// </summary>
public class DetectedChange
{
    public Guid Id { get; set; }
    public Guid SubscriptionId { get; set; }
    public ChangeSource Source { get; set; }
    public ChangeType Type { get; set; }
    public string ObjectIdentifier { get; set; }  // Table name or file path
    public DateTime DetectedAt { get; set; }
    public bool IsProcessed { get; set; }
}

public enum ChangeSource
{
    Database,
    FileSystem
}

public enum ChangeType
{
    Created,
    Modified,
    Deleted,
    Renamed
}

/// <summary>
/// Aggregated changes waiting for processing
/// </summary>
public class PendingChangeBatch
{
    public Guid SubscriptionId { get; set; }
    public List<DetectedChange> Changes { get; set; } = [];
    public DateTime BatchStartedAt { get; set; }
    public DateTime? BatchCompletedAt { get; set; }
}
```

### 6.4 Caching Models

```csharp
// ==================== Caching ====================

/// <summary>
/// Cached database schema snapshot
/// </summary>
public class SchemaSnapshot
{
    public Guid SubscriptionId { get; set; }
    public DateTime CapturedAt { get; set; }
    public string DatabaseVersion { get; set; }
    public byte[] DacpacBytes { get; set; }  // Serialized .dacpac
    public string Hash { get; set; }          // For quick comparison
    public List<SchemaObjectSummary> Objects { get; set; } = [];
}

public class SchemaObjectSummary
{
    public string SchemaName { get; set; }
    public string ObjectName { get; set; }
    public SqlObjectType ObjectType { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string DefinitionHash { get; set; }
}

/// <summary>
/// Cached file model
/// </summary>
public class FileModelCache
{
    public Guid SubscriptionId { get; set; }
    public DateTime CapturedAt { get; set; }
    public Dictionary<string, FileObjectEntry> FileEntries { get; set; } = [];
}

public class FileObjectEntry
{
    public string FilePath { get; set; }
    public string ObjectName { get; set; }
    public SqlObjectType ObjectType { get; set; }
    public string ContentHash { get; set; }
    public DateTime LastModified { get; set; }
}
```

### 6.5 Configuration Models

```csharp
// ==================== Service Configuration ====================

public class ServiceConfiguration
{
    public ServerSettings Server { get; set; } = new();
    public MonitoringSettings Monitoring { get; set; } = new();
    public CacheSettings Cache { get; set; } = new();
    public LoggingSettings Logging { get; set; } = new();
}

public class ServerSettings
{
    public int HttpPort { get; set; } = 5050;
    public int WebSocketPort { get; set; } = 5051;
    public bool EnableHttps { get; set; } = false;
    public string? CertificatePath { get; set; }
}

public class MonitoringSettings
{
    public TimeSpan DatabasePollInterval { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan FileSystemDebounce { get; set; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan FullReconciliationInterval { get; set; } = TimeSpan.FromMinutes(5);
    public int MaxConcurrentComparisons { get; set; } = 2;
}

public class CacheSettings
{
    public string CacheDirectory { get; set; } = "./cache";
    public TimeSpan SnapshotRetention { get; set; } = TimeSpan.FromDays(7);
    public int MaxCachedSnapshots { get; set; } = 10;
}
```

### 6.6 TypeScript/Extension Models

```typescript
// ==================== Extension Side Models ====================

interface Subscription {
    id: string;
    name: string;
    database: DatabaseConnection;
    project: ProjectFolder;
    state: 'active' | 'paused' | 'error' | 'comparing' | 'syncing';
    lastComparedAt?: string;
    differenceCount: number;
}

interface DatabaseConnection {
    id: string;
    name: string;
    server: string;
    database: string;
    authType: 'windows' | 'sql' | 'azuread' | 'azuread-interactive';
}

interface ProjectFolder {
    rootPath: string;
    includePatterns: string[];
    excludePatterns: string[];
    structure: 'flat' | 'by-type' | 'by-schema' | 'by-schema-and-type';
}

interface SchemaDifference {
    id: string;
    objectName: string;
    schemaName: string;
    objectType: SqlObjectType;
    differenceType: 'add' | 'delete' | 'modify' | 'rename';
    source: 'database' | 'filesystem';
    databaseDefinition?: string;
    fileDefinition?: string;
    filePath?: string;
}

type SqlObjectType =
    | 'table' | 'view' | 'stored-procedure'
    | 'scalar-function' | 'table-valued-function'
    | 'trigger' | 'index' | 'constraint' | 'schema';
```

---

## 7. Performance Considerations

### 7.1 Performance Challenges

| Challenge | Impact | Severity |
|-----------|--------|----------|
| Large database schemas (1000+ objects) | Slow comparison | High |
| Many .sql files in project | File system overhead | Medium |
| Frequent file saves (typing) | Event flooding | High |
| Network latency to SQL Server | Slow extraction | Medium |
| Memory consumption | Extension host limits | High |
| Concurrent subscriptions | Resource contention | Medium |

### 7.2 Optimization Strategies

#### 7.2.1 Incremental Comparison

```
FULL COMPARISON (Initial/Periodic)
──────────────────────────────────
┌─────────────┐      ┌─────────────┐
│  Database   │      │  SQL Files  │
│  (Full)     │      │  (Full)     │
└──────┬──────┘      └──────┬──────┘
       │                    │
       └─────────┬──────────┘
                 ▼
       ┌─────────────────────┐
       │  Full Comparison    │
       │  (Store baseline)   │
       └─────────────────────┘

INCREMENTAL COMPARISON (Ongoing)
────────────────────────────────
       Change Detected
             │
             ▼
┌─────────────────────────────┐
│  Compare ONLY affected      │
│  objects against baseline   │
└─────────────────────────────┘
```

```csharp
public class IncrementalComparer
{
    private readonly Dictionary<string, string> _baselineHashes;

    public async Task<IEnumerable<SchemaDifference>> CompareIncremental(
        IEnumerable<string> changedObjects)
    {
        var differences = new List<SchemaDifference>();

        foreach (var objectName in changedObjects)
        {
            var currentHash = await GetCurrentHash(objectName);
            if (_baselineHashes.TryGetValue(objectName, out var baselineHash))
            {
                if (currentHash != baselineHash)
                {
                    differences.Add(await GetDetailedDifference(objectName));
                }
            }
            else
            {
                differences.Add(CreateAddDifference(objectName));
            }
        }

        return differences;
    }
}
```

#### 7.2.2 Debouncing and Batching

```csharp
public class ChangeDebouncer
{
    private readonly TimeSpan _debounceInterval = TimeSpan.FromMilliseconds(500);
    private readonly ConcurrentDictionary<string, DateTime> _pendingChanges = new();
    private readonly Timer _processTimer;

    public void OnChange(string objectIdentifier)
    {
        _pendingChanges[objectIdentifier] = DateTime.UtcNow;
        _processTimer.Change(_debounceInterval, Timeout.InfiniteTimeSpan);
    }

    private async void ProcessBatch(object? state)
    {
        var cutoff = DateTime.UtcNow.Subtract(_debounceInterval);
        var ready = _pendingChanges
            .Where(kvp => kvp.Value < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();

        if (ready.Any())
        {
            await _comparer.CompareIncremental(ready);
            foreach (var key in ready)
                _pendingChanges.TryRemove(key, out _);
        }
    }
}
```

#### 7.2.3 Object Hash Caching

```csharp
public class SchemaHashCache
{
    private readonly ConcurrentDictionary<string, ObjectHashEntry> _cache = new();

    public async Task<bool> HasChanged(string objectName, string currentDefinition)
    {
        var currentHash = ComputeHash(currentDefinition);

        if (_cache.TryGetValue(objectName, out var entry))
        {
            return entry.Hash != currentHash;
        }

        return true; // Not in cache = treat as new/changed
    }

    private static string ComputeHash(string content)
    {
        // Normalize SQL before hashing
        var normalized = NormalizeSql(content);
        using var sha256 = SHA256.Create();
        var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToBase64String(bytes);
    }

    private static string NormalizeSql(string sql)
    {
        // Remove comments, normalize whitespace, etc.
        return SqlNormalizer.Normalize(sql);
    }
}
```

#### 7.2.4 Parallel Processing

```csharp
public class ParallelSchemaProcessor
{
    private readonly int _maxDegreeOfParallelism = Environment.ProcessorCount;

    public async Task<IEnumerable<SchemaDifference>> CompareParallel(
        IEnumerable<SqlObject> objects)
    {
        var results = new ConcurrentBag<SchemaDifference>();

        await Parallel.ForEachAsync(
            objects,
            new ParallelOptions { MaxDegreeOfParallelism = _maxDegreeOfParallelism },
            async (obj, ct) =>
            {
                var diff = await CompareObject(obj, ct);
                if (diff != null)
                    results.Add(diff);
            });

        return results;
    }
}
```

#### 7.2.5 Lazy Loading for UI

```typescript
// Load differences on-demand for tree view
class DifferenceTreeProvider implements vscode.TreeDataProvider<DifferenceItem> {
    private _differences: Map<string, SchemaDifference[]> = new Map();

    async getChildren(element?: DifferenceItem): Promise<DifferenceItem[]> {
        if (!element) {
            // Root: return object types with counts only
            return this.getObjectTypeSummaries();
        }

        // Expand: load actual differences for this type
        if (!this._differences.has(element.objectType)) {
            const diffs = await this.loadDifferencesForType(element.objectType);
            this._differences.set(element.objectType, diffs);
        }

        return this._differences.get(element.objectType)!.map(d => new DifferenceItem(d));
    }
}
```

### 7.3 Memory Management

```csharp
public class MemoryOptimizedModelManager
{
    private readonly MemoryCache _modelCache;
    private readonly long _maxCacheSize = 500 * 1024 * 1024; // 500MB

    public async Task<TSqlModel> GetOrLoadModel(Guid subscriptionId)
    {
        var cacheKey = $"model_{subscriptionId}";

        if (_modelCache.TryGetValue(cacheKey, out TSqlModel model))
        {
            return model;
        }

        // Check memory pressure
        if (GetCacheSize() > _maxCacheSize * 0.8)
        {
            EvictOldestModels();
        }

        model = await LoadModelAsync(subscriptionId);
        _modelCache.Set(cacheKey, model, new MemoryCacheEntryOptions
        {
            SlidingExpiration = TimeSpan.FromMinutes(10),
            Size = EstimateModelSize(model)
        });

        return model;
    }
}
```

### 7.4 Large Schema Handling

```
┌────────────────────────────────────────────────────────────────────┐
│              Large Schema Processing Pipeline                       │
├────────────────────────────────────────────────────────────────────┤
│                                                                     │
│  Step 1: Metadata Extraction (Fast)                                │
│  ─────────────────────────────────                                 │
│  • Extract object names, types, modify dates only                  │
│  • Build lightweight index                                         │
│  • Compare metadata first                                          │
│                                                                     │
│  Step 2: Changed Object Identification                             │
│  ────────────────────────────────────                              │
│  • Use modify_date from sys.objects                                │
│  • Hash-based change detection for files                           │
│  • Identify subset requiring full comparison                       │
│                                                                     │
│  Step 3: Definition Extraction (Targeted)                          │
│  ───────────────────────────────────────                           │
│  • Only extract definitions for changed objects                    │
│  • Batch extraction for efficiency                                 │
│  • Stream large definitions                                        │
│                                                                     │
│  Step 4: Detailed Comparison (Selective)                           │
│  ──────────────────────────────────────                            │
│  • Full DacFX comparison only for changed subset                   │
│  • Property-level diff for modifications                           │
│                                                                     │
└────────────────────────────────────────────────────────────────────┘
```

### 7.5 Performance Metrics & Thresholds

```csharp
public class PerformanceThresholds
{
    // Trigger warnings if exceeded
    public static readonly TimeSpan MaxComparisonTime = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxExtractionTime = TimeSpan.FromMinutes(2);
    public static readonly long MaxMemoryUsageMB = 512;
    public static readonly int MaxConcurrentSubscriptions = 5;

    // Switch to incremental-only mode if exceeded
    public static readonly int LargeSchemaThreshold = 500;  // objects
    public static readonly int VeryLargeSchemaThreshold = 2000;
}
```

### 7.6 Performance Recommendations by Schema Size

| Schema Size | Objects | Recommended Strategy |
|-------------|---------|---------------------|
| Small | < 100 | Full comparison on every change |
| Medium | 100-500 | Incremental with periodic full sync |
| Large | 500-2000 | Incremental only, hash-based detection |
| Very Large | > 2000 | Selective monitoring, manual triggers |

---

## 8. Recommended Architecture

### 8.1 Final Recommendation: Hybrid Architecture

Based on the analysis above, the recommended architecture is **Option B: Hybrid Architecture** with the following specific choices:

```
┌────────────────────────────────────────────────────────────────────────────┐
│                    RECOMMENDED ARCHITECTURE                                 │
├────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│   ┌─────────────────────────────────────────────────────────────────────┐  │
│   │                     VS Code Extension (TypeScript)                   │  │
│   │   ┌────────────┐ ┌────────────┐ ┌────────────┐ ┌────────────────┐   │  │
│   │   │ TreeView   │ │ Commands   │ │ Status Bar │ │ WebView Panel  │   │  │
│   │   │ Provider   │ │ Handler    │ │ Item       │ │ (Diff Viewer)  │   │  │
│   │   └─────┬──────┘ └─────┬──────┘ └─────┬──────┘ └───────┬────────┘   │  │
│   │         │              │              │                │            │  │
│   │   ┌─────┴──────────────┴──────────────┴────────────────┴────────┐   │  │
│   │   │                    Extension Host Services                   │   │  │
│   │   │  • FileWatcher (vscode.workspace.createFileSystemWatcher)   │   │  │
│   │   │  • ServiceClient (HTTP + WebSocket)                         │   │  │
│   │   │  • StateManager (vscode.Memento)                            │   │  │
│   │   └──────────────────────────┬──────────────────────────────────┘   │  │
│   └──────────────────────────────┼──────────────────────────────────────┘  │
│                                  │                                          │
│                      ┌───────────┴───────────┐                              │
│                      │   IPC Layer           │                              │
│                      │   HTTP :5050          │                              │
│                      │   WebSocket :5051     │                              │
│                      └───────────┬───────────┘                              │
│                                  │                                          │
│   ┌──────────────────────────────┼──────────────────────────────────────┐  │
│   │              .NET Background Service (C#)                            │  │
│   │   ┌──────────────────────────┴──────────────────────────────────┐   │  │
│   │   │                    ASP.NET Core Host                         │   │  │
│   │   │   ┌───────────────┐  ┌───────────────┐  ┌───────────────┐   │   │  │
│   │   │   │ REST API      │  │ SignalR Hub   │  │ Health Checks │   │   │  │
│   │   │   │ Controllers   │  │ (WebSocket)   │  │               │   │   │  │
│   │   │   └───────┬───────┘  └───────┬───────┘  └───────────────┘   │   │  │
│   │   └───────────┼──────────────────┼──────────────────────────────┘   │  │
│   │               │                  │                                   │  │
│   │   ┌───────────┴──────────────────┴──────────────────────────────┐   │  │
│   │   │                    Core Services                             │   │  │
│   │   │  ┌─────────────────┐  ┌─────────────────┐  ┌──────────────┐ │   │  │
│   │   │  │ Subscription    │  │ Comparison      │  │ Change       │ │   │  │
│   │   │  │ Manager         │  │ Engine          │  │ Detector     │ │   │  │
│   │   │  └────────┬────────┘  └────────┬────────┘  └──────┬───────┘ │   │  │
│   │   │           │                    │                  │         │   │  │
│   │   │  ┌────────┴────────────────────┴──────────────────┴───────┐ │   │  │
│   │   │  │                 Infrastructure                          │ │   │  │
│   │   │  │  • DacFX Integration    • Database Connector            │ │   │  │
│   │   │  │  • File System Monitor  • Caching (LiteDB/SQLite)       │ │   │  │
│   │   │  │  • Model Builder        • Logging (Serilog)             │ │   │  │
│   │   │  └─────────────────────────────────────────────────────────┘ │   │  │
│   │   └─────────────────────────────────────────────────────────────┘   │  │
│   └─────────────────────────────────────────────────────────────────────┘  │
│                                                                             │
└────────────────────────────────────────────────────────────────────────────┘
```

### 8.2 Technology Stack

| Layer | Technology | Rationale |
|-------|------------|-----------|
| **VS Code Extension** | TypeScript | Native VS Code support |
| **HTTP Client** | axios or fetch | Well-supported, async |
| **WebSocket Client** | @microsoft/signalr | Matches SignalR server |
| **Service Runtime** | .NET 8 | Native DacFX support, performance |
| **Web Framework** | ASP.NET Core Minimal APIs | Lightweight, fast |
| **Real-time** | SignalR | Built-in reconnection, groups |
| **DacFX** | Microsoft.SqlServer.DacFx 162+ | Latest features |
| **Local Storage** | LiteDB or SQLite | Lightweight, embedded |
| **Logging** | Serilog | Structured logging |
| **Configuration** | appsettings.json | Standard .NET config |

### 8.3 Deployment Model

```
┌────────────────────────────────────────────────────────────────────┐
│                     Deployment Options                              │
├────────────────────────────────────────────────────────────────────┤
│                                                                     │
│  Option A: Extension-Managed Service                               │
│  ───────────────────────────────────                               │
│  • Extension starts/stops service as child process                 │
│  • Service binary bundled with extension                           │
│  • Simplest user experience                                        │
│                                                                     │
│  Option B: Windows Service                                         │
│  ─────────────────────────                                         │
│  • Installed separately via MSI/MSIX                               │
│  • Runs continuously in background                                 │
│  • Survives VS Code restarts                                       │
│  • Better for always-on monitoring                                 │
│                                                                     │
│  Option C: Docker Container                                        │
│  ──────────────────────────                                        │
│  • Cross-platform deployment                                       │
│  • Isolated environment                                            │
│  • Good for team/shared scenarios                                  │
│                                                                     │
│  RECOMMENDED: Option A for initial release, Option B for power     │
│               users requiring persistent monitoring                 │
│                                                                     │
└────────────────────────────────────────────────────────────────────┘
```

### 8.4 Project Structure

```
SqlProjectSync/
├── src/
│   ├── SqlProjectSync.Service/           # .NET Background Service
│   │   ├── Program.cs
│   │   ├── appsettings.json
│   │   ├── Controllers/
│   │   │   ├── SubscriptionsController.cs
│   │   │   ├── ComparisonController.cs
│   │   │   └── HealthController.cs
│   │   ├── Hubs/
│   │   │   └── SyncHub.cs
│   │   ├── Services/
│   │   │   ├── SubscriptionManager.cs
│   │   │   ├── ComparisonEngine.cs
│   │   │   ├── ChangeDetector.cs
│   │   │   └── ModelBuilder.cs
│   │   ├── Infrastructure/
│   │   │   ├── DacFx/
│   │   │   ├── Database/
│   │   │   ├── FileSystem/
│   │   │   └── Caching/
│   │   └── Models/
│   │       ├── Subscription.cs
│   │       ├── ComparisonResult.cs
│   │       └── SchemaDifference.cs
│   │
│   └── sqlproject-sync-vscode/            # VS Code Extension
│       ├── package.json
│       ├── src/
│       │   ├── extension.ts
│       │   ├── services/
│       │   │   ├── ServiceClient.ts
│       │   │   ├── FileWatcher.ts
│       │   │   └── StateManager.ts
│       │   ├── providers/
│       │   │   ├── SubscriptionTreeProvider.ts
│       │   │   └── DifferenceTreeProvider.ts
│       │   ├── commands/
│       │   │   ├── subscription.commands.ts
│       │   │   └── comparison.commands.ts
│       │   ├── views/
│       │   │   └── DiffViewerPanel.ts
│       │   └── models/
│       │       └── types.ts
│       └── resources/
│           └── icons/
│
├── tests/
│   ├── SqlProjectSync.Service.Tests/
│   └── sqlproject-sync-vscode.tests/
│
├── docs/
│   └── architecture/
│       └── DESIGN.md
│
└── README.md
```

### 8.5 Implementation Phases

```
Phase 1: Foundation (Weeks 1-2)
────────────────────────────────
□ Set up .NET service project with ASP.NET Core
□ Implement basic REST API endpoints
□ Set up VS Code extension scaffolding
□ Implement HTTP client communication
□ Basic subscription CRUD operations

Phase 2: DacFX Integration (Weeks 3-4)
──────────────────────────────────────
□ Integrate DacFX for model building
□ Implement database extraction
□ Implement file-based model building
□ Schema comparison engine
□ Difference detection and reporting

Phase 3: Change Detection (Weeks 5-6)
─────────────────────────────────────
□ File system watcher (VS Code + .NET)
□ Database polling mechanism
□ Change aggregation and debouncing
□ Incremental comparison support

Phase 4: Real-time & UI (Weeks 7-8)
───────────────────────────────────
□ SignalR hub implementation
□ WebSocket client in extension
□ TreeView providers
□ Status bar integration
□ Notification system

Phase 5: Polish & Performance (Weeks 9-10)
──────────────────────────────────────────
□ Performance optimization
□ Caching implementation
□ Error handling and recovery
□ Logging and diagnostics
□ Documentation
```

---

## 9. Decision Summary

| Decision Point | Recommendation | Rationale |
|----------------|----------------|-----------|
| **Architecture** | Hybrid (Option B) | Best separation of concerns, native DacFX |
| **Communication** | HTTP + SignalR | REST for commands, WebSocket for events |
| **File Watching** | VS Code API primary | Best integration, reliable |
| **DB Change Detection** | Polling sys.objects | Simple, reliable, no special permissions |
| **Comparison** | Incremental with baseline | Performance for large schemas |
| **Deployment** | Extension-managed | Best UX for initial release |

---

## 10. Next Steps

1. **Review and approve this design document**
2. **Set up development environment** (.NET 8, Node.js, VS Code Extension Dev)
3. **Create project scaffolding** per the project structure above
4. **Begin Phase 1 implementation**

---

*Document Version: 1.0*
*Last Updated: 2026-01-31*
*Author: Architecture Design Phase*
```
```

