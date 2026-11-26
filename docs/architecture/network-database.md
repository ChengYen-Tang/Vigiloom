# Network database migration

The network service owns its Ping and speed-test settings, targets, samples, rollups, events, and results. Its SQL Server database is separate from the resource and operations databases.

For an existing installation, stop the legacy monitoring workers before copying either Operations or Network data. Keep them stopped until both copies are complete: the Operations Ping cursor and the Network transition baseline must come from the same unchanged source generations. Start the new services only after both databases are ready.

Create the target database first and set `VIGILOOM_NETWORK_SQL_CONNECTION` to it. Run the migrator with `initialize-network` for a new installation. For an existing installation, also set `VIGILOOM_MIGRATION_SQL_CONNECTION` to the old platform database and run `migrate-network`. The migration copies network data into an empty target and initializes its own scheduling tables. The shared Hangfire storage is configured separately.

Before restarting the legacy Worker, configure its `Services:Network:Url` to the network service URL. This disables the legacy Ping and speed workers and their recurring-job registrations. Start the network service with the shared Hangfire connection so it registers the replacement jobs in the `network` queue.
