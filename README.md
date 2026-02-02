# SQL Project Synchronization Service

This service compares a SQL Server database schema with a local SQL project folder (set of `.sql` files) and exposes REST/SignalR APIs that are consumed by a VS Code extension.

The comparison engine intentionally focuses on a curated set of database-level objects (tables, views, procedures, functions, triggers, users, roles). Other artifacts such as logins are still detected and classified, but are treated as **unsupported** and reported via dedicated diagnostics instead of regular schema differences.

## Features

- Compare a live SQL Server database against a folder of `.sql` files representing a SQL project.
- Expose REST APIs for running comparisons, inspecting differences, and managing comparison history.
- Provide SignalR-based realtime notifications for long-running comparisons and subscription updates.
- Classify unsupported object types and surface them as diagnostics instead of normal schema diffs.

## Documentation

This repository includes more detailed design and reference docs:

- `docs/DESIGN.md` – high-level architecture and background.
- `docs/SERVICE-SPECIFICATION.md` – HTTP/SignalR surface area and contract details.
- `docs/SUPPORTED-OBJECT-TYPES.md` – whitelist of supported object types and rationale for exclusions.
- `docs/BACKGROUND-SERVICE-IMPLEMENTATION-PLAN.md` – background processing and service lifetime plan.
- `docs/TESTING-GUIDE.md` – guidance on testing strategy and scenarios.

## Getting started

### Prerequisites

- .NET SDK installed (see `src/SqlSyncService/SqlSyncService.csproj` for the target framework).
- Access to a SQL Server instance that you can use for schema comparison.
- A local SQL project folder containing the `.sql` files you want to compare.

### Cloning and building

```bash
git clone https://github.com/HVallad/SqlComparisonService.git
cd SqlComparisonService

dotnet restore
dotnet build SqlSyncService.sln
dotnet test
```

### Running the service locally

From the repository root:

```bash
dotnet run --project src/SqlSyncService/SqlSyncService.csproj
```

By default the service reads its configuration (for example, connection strings and project folder paths) from:

- `src/SqlSyncService/appsettings.json`
- `src/SqlSyncService/appsettings.Development.json`

Update these files to point at your SQL Server instance and the local SQL project folder you want to compare.

## Project structure

- `src/SqlSyncService/` – main ASP.NET Core service:
  - `Controllers/` – HTTP endpoints for comparisons, connections, folders, diagnostics, subscriptions, and health checks.
  - `Realtime/` – SignalR hub for realtime notifications.
  - `DacFx/` – helpers for building database/file models and running comparisons.
  - `Persistence/` – LiteDB-backed repositories for comparison history, schema snapshots, and subscriptions.
- `tests/SqlSyncService.Tests/` – unit and integration tests for the service.
- `docs/` – design, specification, and testing documentation.

## Contributing

This is an early-stage service; contributions and feedback are welcome.

1. Fork the repository.
2. Create a feature branch.
3. Make your changes and add tests where appropriate.
4. Run `dotnet test`.
5. Open a pull request.

## License

TODO: Add license information here (for example, MIT or Apache 2.0).
