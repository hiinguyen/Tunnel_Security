---
name: add-migration
description: Create an EF Core migration for BackendV2 (AppDbContext) the right way. Use when a model or schema changes.
disable-model-invocation: true
argument-hint: <MigrationName>
---

Only `AppDbContext` has migrations. `TimeSeriesDbContext` is created by `TimescaleInitializer` (tables and hypertables), not by `dotnet ef`.
Do not hand-edit `Migrations/*.Designer.cs` or `AppDbContextModelSnapshot.cs`; EF generates them (a hook blocks edits).

1. Load the environment: `. ~/.cache/tunnel-security-dotnet/env.sh`.
2. Create the migration: `dotnet ef migrations add $ARGUMENTS --project BackendV2 --context AppDbContext --output-dir Migrations`.
3. Read the generated migration. Check that `Up` and `Down` match the intent and nothing drops a column or table unexpectedly.
4. Rebuild: `dotnet build BackendV2`.
5. If the dev SQL Server is running, apply with `dotnet ef database update --project BackendV2 --context AppDbContext`, then run `dotnet test tests/BackendV2.Tests`.
6. Update `docs/database_schema.sql` if it describes a table that changed.
7. Undo an unapplied migration with `dotnet ef migrations remove --project BackendV2 --context AppDbContext`.
