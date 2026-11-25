# Resource database migration

Create the target SQL Server database before running the migrator. Set `VIGILOOM_RESOURCES_SQL_CONNECTION` to that database. Run `Vigiloom.Migrator initialize-resources` for an empty resource database, or set `VIGILOOM_MIGRATION_SQL_CONNECTION` to the existing platform database and run `Vigiloom.Migrator migrate-resources` to copy devices, groups, device targets, device audit records, group memberships, application service identities, discovery candidates, Agent identity and enrollment, and Agent package and assignment records.

The migrator applies the existing EF Core resource migrations and then copies the listed data in a single target transaction. The target tables must be empty. Stop writes to the source during migration. Resource IDs and relationships are preserved; SQL Server assigns new rowversion values, so clients must refresh any previously loaded edit forms.

This command covers the data listed above. Publication templates, other Agent data, and deployment links require the remaining REQ-45 migration work. Agent identity, assignment, and package management and the application observation service still need to be switched to the resource-owned records before the migration is complete.

For package removal, configure the resource service's `Services:Host:Url` to the internal host-monitoring endpoint. During the transition, the existing Web host supplies the internal package-usage check. The shared `Vigiloom:AgentPackages:CachePath` must be accessible to each resource-service instance that serves packages.

The resource service exposes assigned manifest and archive endpoints on its internal network. REQ-47 must authenticate the Agent and pass its verified Agent ID to these endpoints; the resource service checks device eligibility and the current assignment before returning files. These internal routes are not public Agent APIs.
