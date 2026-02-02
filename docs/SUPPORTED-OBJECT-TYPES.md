# Supported Object Types for Comparison

This document describes which SQL object types the SQL Project Synchronization Service actively compares between the **database** and the **project folder**, and which object types are **intentionally ignored**.

The goal is to match the behaviour of SQL Database Projects in VS Code while keeping comparison semantics predictable and debuggable.

---

## Whitelist of Supported Comparison Types

Only the following object types participate in comparisons. Everything else is treated as **unsupported** and excluded from diffing:

- **Table**
- **View**
- **Stored Procedure**
- **Scalar Function**
- **Table-Valued Function**
- **Inline Table-Valued Function**
- **Trigger**
- **User** (database-level security principal)
- **Role** (database-level security principal)

These correspond to the values in `SqlObjectTypeSupport.SupportedComparisonTypes` and are enforced in the comparison engine (DacFx `SchemaComparer`) and orchestration layer (`ComparisonOrchestrator`).

If a database or project object is not in this whitelist, it will **not** generate a `SchemaDifference` entry.

---

## Unsupported Object Types

Objects that are discovered but **not** in the whitelist are treated as **unsupported**. They are:

- Excluded from the comparison logic (no add/modify/delete differences)
- Tracked separately for diagnostics
- Counted in the comparison summary
- Exposed via a dedicated debugging endpoint

Examples of unsupported object types include:

- **Login**
- **Unknown** (files we cannot confidently classify)
- Any additional `SqlObjectType` values that are not in the whitelist

In the domain model, these are represented as `UnsupportedObject` items attached to each `ComparisonResult`.

---

## Why Logins Are Excluded

Logins are **server-level** principals in SQL Server. They live at the instance level and are not part of the database schema that DacFx models inside a `.dacpac`.

SQL Database Projects historically treats logins differently from database objects:

- Database projects focus on **database-scoped** artifacts (tables, views, procedures, users, roles, etc.).
- Logins are typically managed via **separate scripts or server projects** and are not part of standard database schema comparisons.

To align with this behaviour and avoid surprising or noisy comparisons:

- `SqlObjectType.Login` is **not** included in the whitelist.
- Login scripts in the project are still **classified correctly** (so they do not get mis-labelled as tables or other types).
- Database/server logins or login scripts are tracked as **unsupported objects** instead of producing differences.

This keeps comparison output focused on database-level schema, while still giving you visibility into the presence of login-related artifacts when needed.

---

## How to Inspect Unsupported Objects

For debugging and visibility, each comparison records all unsupported objects that were detected on either side.

- The comparison summary includes two counts:
  - `UnsupportedDatabaseObjectCount`
  - `UnsupportedFileObjectCount`
- There is a dedicated endpoint to list these objects for a given comparison:

  - `GET /api/comparisons/{comparisonId}/unsupported-objects`

The response includes, for each unsupported object:

- Source (`database` or `file`)
- Object type key (e.g., `login`, `unknown`)
- Schema name (for database-side objects, when applicable)
- Object name
- File path (for file-side objects, when applicable)

This allows tools (such as the VS Code extension) and users to understand **what was skipped** during the comparison without polluting the main difference list.
