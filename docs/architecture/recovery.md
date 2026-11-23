# Central platform backup and recovery

This procedure applies to the central Web, Worker and SQL database. An Agent's
local queue and the Agent package download mechanism have separate lifecycles.
It is an operational procedure, not a claim that recovery objectives have been
met. The first planning targets are at most 24 hours of data loss and four
hours to restore service; each installation must set its own backup schedule
and validate those targets against its data volume and infrastructure.

## What to preserve

- The complete Vigiloom SQL database, including Identity, monitoring, security,
  publication, operations, scheduling and Hangfire schemas. Use a consistent
  SQL Server backup and retain it in a separate protected location.
- The ASP.NET Core Data Protection key-ring directory, its application name,
  and the private certificate and password used to protect the keys, if set.
  Web and Worker must use the same key ring and settings. A database backup
  alone cannot restore protected values if the key material is lost.
- The private Agent package library configured by
  `Vigiloom:AgentPackages:CachePath`, deployment configuration, HTTPS
  certificates, and the versioned Web, Worker and Migrator packages. Keep
  credentials outside the backup instructions and control access to them.

Coordinate the database backup with the key-ring and package-library backup;
retain old Data Protection keys needed by restored records. Package files can
be restored separately from the database, so verify the selected Agent packages
exist after recovery. Merely copying the SQL database is not a complete restore.
Check how the original key ring is protected: Windows DPAPI may bind keys to a
specific machine or account, so copying its files alone may not allow decryption
elsewhere. For a cross-machine recovery, retain the required platform key
material or configure and back up a portable certificate before the incident.
See [Microsoft's Data Protection key encryption guidance](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/implementation/key-encryption-at-rest?view=aspnetcore-10.0).

## Restore into an isolated environment

1. Stop Web and Worker. Select a SQL Server instance and restore the backup to
   a **new** database name and new data/log file paths. Do not overwrite the
   source database during a rehearsal. SQL Server's `RESTORE FILELISTONLY`
   identifies the logical file names needed by `RESTORE DATABASE ... WITH MOVE`.
2. Restore the matching key ring, its protection certificate, and the Agent
   package library to private persistent locations. Supply the corresponding
   `Vigiloom:DataProtection` and `Vigiloom:AgentPackages:CachePath` settings.
3. With the deployment SQL identity, point
   `VIGILOOM_MIGRATION_SQL_CONNECTION` to the restored database and run the
   version-matched Migrator **without** `fresh-install`. Use the documented
   [migration procedure](sql-server-migrations.md); the target database must
   already exist. Web and Worker use their runtime SQL identity.
4. **Before starting Web or Worker**, isolate the rehearsal network from
   production Agents, monitored targets and external notification services,
   while allowing the isolated SQL connection. A restored Worker can otherwise
   resume pending jobs and deliver real notifications. Start the processes
   only after the isolation is in place. Verify administrator login, protected
   settings, previously registered Agents, package availability, monitoring
   data and scheduled work using test destinations.
5. Record backup age, restore start and end, data checks, package and key
   versions, host/SQL versions and failures. Remove only resources created for
   the rehearsal after the result is recorded.

For a real recovery, coordinate the cutover and endpoint/network changes with
the operator. Stop old receivers and Workers before the restored environment is
activated; avoid assuming zero downtime or multi-replica safety. Extending a
retention policy cannot recover data already deleted before the backup.

## Evidence and remaining work

The initial database-only LocalDB backup, restore and current-Migrator rerun is
recorded in [capacity-validation.md](capacity-validation.md). A separate
`DataProtectionTests` check on 2026-11-23 moved a certificate-protected key ring
and its PFX to new paths and verified that a new host with the same application
name could decrypt an earlier value; a different application name could not.
A second isolated LocalDB exercise on 2026-11-23 backed up a fixture database
with checksum and restored it under a different test-owned name and file paths.
After moving its key ring and certificate, a new in-process Web host could
decrypt the restored disabled LINE test channel secret; its existing device
was present and both Administrator and Read only test accounts could sign in.
No notification was sent, and both test databases and the backup were removed.
This same-machine exercise did not restore Agent packages, start Worker, run the
Migrator after restore, validate an old-version upgrade or measure RPO/RTO.
REQ-40-T04 stays open until those checks and the chosen RPO/RTO have been
measured in a representative deployment.
