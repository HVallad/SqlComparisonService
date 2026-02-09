# Milestone 10: Polish & Performance Implementation Plan

## Overview

**Milestone:** 10 - Polish & Performance (Phase 5)  
**Status:** Not Started  
**Estimated Duration:** 2-3 days  
**Prerequisites:** Milestone 9 (SignalR & Real-time Events) ✅ Complete

This milestone focuses on production-readiness improvements including performance optimization, enhanced error handling, improved diagnostics, and comprehensive documentation.

---

## Objectives

1. **Performance Optimization** - Optimize hot paths and reduce memory usage
2. **Error Handling & Recovery** - Add retry logic, circuit breakers, and graceful degradation
3. **Diagnostics & Observability** - Enhanced diagnostic endpoints and structured logging
4. **Documentation** - Configuration guide, deployment guide, and troubleshooting documentation

---

## Architecture & Design

### Design Principles

- **Non-breaking changes** - All improvements maintain backward compatibility
- **Opt-in complexity** - Advanced features (circuit breakers) configurable via settings
- **Fail-safe defaults** - Conservative defaults that prioritize stability
- **Observable behavior** - All improvements should be measurable/loggable

### Key Components Affected

| Component | Changes |
|-----------|---------|
| `ComparisonOrchestrator` | Performance optimizations |
| `DatabasePollingWorker` | Retry logic, memory cleanup, circuit breaker |
| `FileWatchingWorker` | Watcher recreation with backoff |
| `HealthCheckWorker` | Connection result caching |
| `SchemaSnapshotRepository` | Query optimization |
| `DiagnosticsController` | New diagnostic endpoints |
| `GlobalExceptionHandlingMiddleware` | Extended exception mapping |

---

## Implementation Plan

### Phase 1: Performance Optimization (0.5 days)

#### Task 1.1: Optimize SchemaSnapshotRepository Queries
**File:** `src/SqlSyncService/Persistence/SchemaSnapshotRepository.cs`

**Current Issue:**
```csharp
// Fetches ALL snapshots, sorts in memory
var result = _context.SchemaSnapshots
    .Find(s => s.SubscriptionId == subscriptionId)
    .OrderByDescending(s => s.CapturedAt)
    .FirstOrDefault();
```

**Solution:** Use LiteDB's query capabilities more efficiently.

#### Task 1.2: Optimize BuildSummary Single-Pass
**File:** `src/SqlSyncService/Services/ComparisonOrchestrator.cs`

**Current Issue:** Multiple LINQ iterations over differences collection.

**Solution:** Consolidate into single enumeration with running counters.

#### Task 1.3: Add Memory Cleanup for DatabasePollingWorker
**File:** `src/SqlSyncService/Workers/DatabasePollingWorker.cs`

**Current Issue:** `_lastKnownObjectModifyDates` grows unbounded when subscriptions are deleted.

**Solution:** Add periodic cleanup of entries for non-existent subscriptions.

---

### Phase 2: Error Handling & Recovery (1 day)

#### Task 2.1: Add Retry Logic for Database Connections
**Files:** 
- `src/SqlSyncService/Workers/DatabasePollingWorker.cs`
- `src/SqlSyncService/Workers/HealthCheckWorker.cs`

**Implementation:**
- Add exponential backoff retry (3 attempts: 1s, 2s, 4s)
- Log retry attempts at Warning level
- Only mark as failed after all retries exhausted

#### Task 2.2: Add FileSystemWatcher Recreation with Backoff
**File:** `src/SqlSyncService/Workers/FileWatchingWorker.cs`

**Current Issue:** Failed watchers are removed but never recreated until next sync cycle.

**Solution:**
- Track failed watcher subscriptions with failure timestamp
- Attempt recreation with exponential backoff (30s, 60s, 120s, max 5 min)
- Reset backoff on successful recreation

#### Task 2.3: Add Circuit Breaker for Failing Subscriptions
**Files:**
- `src/SqlSyncService/Configuration/ServiceConfiguration.cs`
- `src/SqlSyncService/Workers/DatabasePollingWorker.cs`

**Implementation:**
- Add `CircuitBreakerSettings` configuration class
- Track consecutive failures per subscription
- After threshold (default: 5), skip subscription for cooldown period (default: 5 min)
- Reset on successful poll
- Emit `SubscriptionHealthChanged` event when circuit opens/closes

#### Task 2.4: Extend GlobalExceptionHandlingMiddleware
**File:** `src/SqlSyncService/Middleware/GlobalExceptionHandlingMiddleware.cs`

**Add mappings for:**
- `SqlException` → 503 Service Unavailable (with specific error codes)
- `TimeoutException` → 504 Gateway Timeout
- `IOException` → 503 Service Unavailable
- `OperationCanceledException` → 499 Client Closed Request

---

### Phase 3: Diagnostics & Observability (0.5 days)

#### Task 3.1: Add Diagnostic Status Endpoint
**File:** `src/SqlSyncService/Controllers/DiagnosticsController.cs`

**New Endpoint:** `GET /api/diagnostics/status`

**Response:**
```json
{
  "service": {
    "version": "1.0.0",
    "uptime": "PT2H30M15S",
    "startedAt": "2024-01-15T10:00:00Z"
  },
  "workers": {
    "databasePolling": { "enabled": true, "lastRun": "...", "errorCount": 0 },
    "fileWatching": { "enabled": true, "activeWatchers": 3 },
    "healthCheck": { "enabled": true, "lastRun": "..." },
    "cacheCleanup": { "enabled": true, "lastRun": "..." },
    "reconciliation": { "enabled": true, "lastRun": "..." }
  },
  "connections": {
    "signalR": { "activeConnections": 5, "groupCount": 3 }
  },
  "subscriptions": {
    "total": 5,
    "healthy": 4,
    "degraded": 1,
    "unhealthy": 0
  }
}
```

#### Task 3.2: Add Worker Status Endpoint
**File:** `src/SqlSyncService/Controllers/DiagnosticsController.cs`

**New Endpoint:** `GET /api/diagnostics/workers`

#### Task 3.3: Add Subscription State Endpoint
**File:** `src/SqlSyncService/Controllers/DiagnosticsController.cs`

**New Endpoint:** `GET /api/diagnostics/subscriptions/{id}/state`

#### Task 3.4: Enhance Structured Logging
**Files:** Various workers and services

**Improvements:**
- Add `ComparisonId` correlation to all comparison-related logs
- Add `Duration` property to operation completion logs
- Standardize property naming (PascalCase)

---

### Phase 4: Documentation (0.5-1 day)

#### Task 4.1: Create Configuration Guide
**File:** `docs/CONFIGURATION-GUIDE.md`

**Content:**
- All configuration sections with descriptions
- Default values and valid ranges
- Environment variable overrides
- Example configurations for different scenarios

#### Task 4.2: Create Deployment Guide
**File:** `docs/DEPLOYMENT-GUIDE.md`

**Content:**
- Prerequisites and system requirements
- Installation steps (Windows Service, Docker, IIS)
- Health monitoring setup
- Backup and recovery procedures
- Scaling considerations

#### Task 4.3: Create Troubleshooting Guide
**File:** `docs/TROUBLESHOOTING-GUIDE.md`

**Content:**
- Common issues and solutions
- Diagnostic steps for each component
- Log analysis guidance
- Recovery procedures


