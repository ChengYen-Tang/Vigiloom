# Resource database migration

Create the target SQL Server database before running the migrator. Set `VIGILOOM_RESOURCES_SQL_CONNECTION` to that database. Run `Vigiloom.Migrator initialize-resources` for an empty resource database, or set `VIGILOOM_MIGRATION_SQL_CONNECTION` to the existing platform database and run `Vigiloom.Migrator migrate-resources` to copy devices, groups, device targets, device audit records, group memberships, application service identities, discovery candidates, Agent identity and enrollment, and Agent package and assignment records.

In Windows Development, the Resources host defaults to the separate LocalDB database `Vigiloom_Resources_Development`. Create it and initialize it with `VIGILOOM_RESOURCES_SQL_CONNECTION` set to the same connection string before starting the host. The initialization also creates its scheduling outbox for Temporal work. Configure `Temporal:Address` when Temporal is not available at the local default `localhost:7233`.

The migrator applies the existing EF Core resource migrations and then copies the listed data in a single target transaction. The target tables must be empty. Stop writes to the source during migration. Resource IDs and relationships are preserved; SQL Server assigns new rowversion values, so clients must refresh any previously loaded edit forms.

This command also copies publication templates, retaining device references. Agent monitoring data and application observation data remain in their existing databases until their respective services are migrated. Publication management and public status can use the resource-owned records; Agent identity, assignment, package management, and application observation consumers still need to be switched before the end-to-end migration is complete.

For package removal, configure the resource service's `Services:Host:Url` to the internal host-monitoring endpoint. During the transition, the existing Web host supplies the internal package-usage check. The shared `Vigiloom:AgentPackages:CachePath` must be accessible to each resource-service instance that serves packages.

The resource service exposes assigned manifest and archive endpoints on its internal network. REQ-47 must authenticate the Agent and pass its verified Agent ID to these endpoints; the resource service checks device eligibility and the current assignment before returning files. These internal routes are not public Agent APIs.

REQ-46 and REQ-48 can read current public policy from the internal publication endpoints. They must check again after fetching monitoring data and before returning a public response, compare the returned version tokens, and suppress the response if the policy is unavailable or changed. A pending public history boundary is returned as null.
