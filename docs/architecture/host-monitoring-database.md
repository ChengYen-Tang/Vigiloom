# Host monitoring database

The Host Monitoring service owns Agent receipts, heartbeat observations, host metrics, and their settings. Agent registration and package assignments remain in Resources; host Logs are stored by Operations.

Create an empty SQL Server database and set `VIGILOOM_HOST_SQL_CONNECTION` before running `Vigiloom.Migrator initialize-host-observations` for a new installation. For an existing installation, stop the old Agent receivers, host-metric workers and related settings changes, set `VIGILOOM_MIGRATION_SQL_CONNECTION` to the original platform database, and run `Vigiloom.Migrator migrate-host-observations`. Keep the old processes stopped until the target has been verified and traffic is ready to switch. The copy refuses a populated destination; keep the source database until verification is complete.

The migration uses the Host Monitoring EF Core migrations, initializes its outbox tables, and copies Agent receipt and status history, host metrics, settings and their audit history. Host Logs themselves are migrated to Operations; Host keeps their message identities for retry deduplication. Migrate Resources and Operations from the same stopped source before starting the new services.

Configure Host Monitoring with `ConnectionStrings:HostMonitoring`, the shared `ConnectionStrings:Scheduling`, and internal `Services:Resources:Url` and `Services:Operations:Url`. Configure Web and the legacy Worker with `Services:HostMonitoring:Url`, and configure Resources with `Services:Host:Url`. Each URL must point to the corresponding internal service. The Gateway uses `Services:vigiloom-host-monitoring` for its Host route. Configure the Temporal address and namespace for security and audit delivery.

Multiple Host instances use the same Host database and Hangfire storage. They also need a shared persistent Data Protection key ring for settings previews; see [identity and access](identity-and-access.md). When SignalR uses multiple instances, configure `ConnectionStrings:SignalR` for the shared backplane so a notification can reach an Agent connected to another instance.

The complete Agent batch cutover and security consumer depend on the remaining REQ-47 and REQ-49 work; initializing or copying the database alone does not complete that cutover.
