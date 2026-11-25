# Operations database

Create an empty, non-system SQL Server database for the Operations service. Set `VIGILOOM_OPERATIONS_SQL_CONNECTION` to its connection string and run `Vigiloom.Migrator initialize-operations`. The command applies the operations, shared Log quota, and centralized Log EF Core migrations. Configure the Operations host's `ConnectionStrings:Operations` with the same connection string.

On Windows, the development host defaults to LocalDB database `Vigiloom_Operations_Development`; create that database and initialize it before starting the host. The `/internal/v1/log-quota` endpoints are for the trusted service network and must not be exposed through the public gateway.

This initializes an empty database. Existing data migration and centralized Log intake are delivered by the remaining REQ-50 tasks.
