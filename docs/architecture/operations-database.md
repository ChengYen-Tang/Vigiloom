# Operations database

Create an empty, non-system SQL Server database for the Operations service. Set `VIGILOOM_OPERATIONS_SQL_CONNECTION` to its connection string and run `Vigiloom.Migrator initialize-operations`. The command applies the operations, shared Log quota, centralized Log, and scheduling EF Core migrations. Configure the Operations host's `ConnectionStrings:Operations` with the same connection string.

On Windows, the development host defaults to LocalDB database `Vigiloom_Operations_Development`; create that database and initialize it before starting the host. The `/internal/v1/log-quota` endpoints are for the trusted service network and must not be exposed through the public gateway.

This initializes an empty database. The trusted host-monitoring service can submit authenticated Agent Logs to `/internal/v1/host-logs`; the Operations service commits new records, shared quota usage and security work together. The security service consumes the queued references once its REQ-49 integration is available. The trusted observability service can submit approved application Logs to `/internal/v1/application-logs`; both sources use the same global Log quota. Existing data migration and integration with the actual host and observability services remain to be completed.
