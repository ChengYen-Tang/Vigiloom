# SQL Server module migrations

REQ-02-T02 supplies the first production migrations for `identity`, `resources`, and `network`. Each module owns its context, schema, and separate `__EFMigrationsHistory`. Devices and groups belong to Resources; network samples refer to a device GUID through a `NO ACTION` cross-schema foreign key. The Network migration deliberately adds that foreign key without a CLR reference to the Resources module. Future migrations must preserve it and follow the dependency order below.

EF Core 10 SQL generation explicitly targets compatibility level 150. The deployment database must have SQL Server compatibility level 150 or higher; configuring EF does not change the database level. Integration tests set temporary databases to 150 and record the actual SQL engine. SQL Server edition, hosting, and higher compatible levels are deployment choices, not hard-coded editions. Microsoft documentation: [SQL Server provider compatibility](https://learn.microsoft.com/en-us/ef/core/providers/sql-server/#compatibility-level).

## Applying migrations

Supply a SQL Server connection with the permissions needed to create schemas, tables, indexes and foreign keys. Back up and review generated migration scripts before applying them to an existing environment; migrations are a deployment operation and do not run automatically on Web or Worker startup. Keep credentials outside the repository. For local development the design-time factories default to `Vigiloom_Development` on `(localdb)\MSSQLLocalDB`; set `VIGILOOM_DESIGN_SQL_CONNECTION` to override it. The target database must already exist.

```powershell
$env:VIGILOOM_MIGRATION_SQL_CONNECTION = '<connection to the existing target database>'
dotnet run --project src/Hosts/Vigiloom.Migrator --configuration Release
```

The deployment tool applies the modules in dependency order and initializes built-in roles; it may be rerun after a successful deployment. Publish the Migrator project with `dotnet publish` when deploying without the SDK. It refuses system databases and requires the target database to exist. Use a deployment identity with schema-changing permission; Web and Worker need only their normal runtime permissions. For development-time EF commands, run `dotnet tool restore` and set `VIGILOOM_DESIGN_SQL_CONNECTION` when targeting something other than the LocalDB development database.

Resources must precede Network and AgentManagement because of their device foreign keys. Identity is independent. Generate a reviewable repeatable deployment script for each module with `dotnet ef migrations script --idempotent --project <module-project> --output <path>` and execute scripts in the same order. Dropping or rolling back Resources while Network or AgentManagement references it is intentionally blocked; roll back dependent migrations first. Do not share migration history tables between modules or edit an already deployed migration.

The Identity data layer provides `RoleInitializer.InitializeAsync` for the two built-in roles; the deployment tool invokes it after migrations; REQ-03 will add authentication bootstrap and authorization. This foundation does not yet create an administrator, turn on login, or grant access. Initialization uses SQL transaction-scoped application locking, preserves existing role IDs, and allocates retry IDs before the execution strategy starts. It is safe for concurrent hosts and is tested against a real SQL database.

Web and Worker register per-operation context factories; callers must dispose each context, and Worker jobs should create a fresh context for each unit of work. Do not keep a context for a Blazor circuit or share it across concurrent operations. Each application configures `ConnectionStrings:Vigiloom`; development uses the separate LocalDB database. See [database isolation](development-and-test-databases.md) for tests and cleanup.

For model changes, read [ADR-0001](ADR-0001-identifiers-and-keys.md), add the owning module migration with the pinned EF tool, inspect it for key and cross-schema constraints, test on SQL Server, and check `dotnet ef migrations has-pending-model-changes --project <module-project>`. Cross-module references are `NO ACTION` and must not introduce direct module implementation dependencies. EF snapshots cannot model Network's and AgentManagement's manually declared cross-module FKs: preserve them explicitly in future relevant migrations.
