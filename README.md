SQL Project Synchronization Service
===================================

This service compares a SQL Server database schema with a local SQL project folder (set of `.sql` files) and exposes REST/SignalR APIs for a VS Code extension.

Key documentation:

- `docs/DESIGN.md` – high-level architecture and background
- `docs/SUPPORTED-OBJECT-TYPES.md` – whitelist of object types that participate in comparisons, details on unsupported objects, and why logins are excluded

The comparison engine intentionally focuses on a curated set of database-level objects (tables, views, procedures, functions, triggers, users, roles). Other artifacts such as logins are still detected and classified, but are treated as **unsupported** and reported via dedicated diagnostics instead of regular schema differences.
