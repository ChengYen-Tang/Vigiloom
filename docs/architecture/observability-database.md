# Observability database migration

The observability service uses its own SQL Server database. Create the database first, set `VIGILOOM_OBSERVABILITY_SQL_CONNECTION`, and run `Vigiloom.Migrator initialize-observability` for a fresh installation.

To migrate an existing installation, stop the legacy telemetry receivers, health workers, administration writes, and the Web catalog event consumer before copying. Set `VIGILOOM_MIGRATION_SQL_CONNECTION` to the old platform database, set `VIGILOOM_OBSERVABILITY_SQL_CONNECTION` to the empty target database, then run `Vigiloom.Migrator migrate-observability`. Copy the Operations health cursor from the same stopped source snapshot, so both services start with the same health generation baseline. Existing application Logs are migrated by Operations.

Configure Web with `Services:Observability:Url` before starting the independent observability service. This stops the old Web catalog event consumer; both consumers must not run against different databases on the same Temporal queue. Start the new consumer only after the data copy completes. Configure the new service's `ConnectionStrings:Observability`, `Services:Resources:Url`, Temporal address and namespace for its deployment.

The migration preserves resource IDs, history and health generations, replaces regenerated SQL rowversions only when the old evidence matched, and clears old worker leases. It refuses to overwrite a populated target or migrate inconsistent health authorization evidence. Keep the source database until the new system has been verified.
