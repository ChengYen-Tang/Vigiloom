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

On 2026-11-23, `tools/server-deployment-smoke.ps1 -Recovery` performed an
additional same-machine Linux x64 container exercise using self-contained
current Web, Worker and Migrator builds published with SDK 11.0.100, and an
isolated SQL Server 2022 test database. It created an administrator through
the setup page, signed in, stopped both hosts, backed up SQL with checksum,
copied the Data Protection
key ring and HTTPS certificate to new paths, restored SQL under a new database
name and files, and ran the current Migrator. The restored Web accepted the
pre-backup authenticated session and the restored Worker registered a new
Hangfire heartbeat. A test file in the package directory survived a copy and
hash comparison; no real Agent package was restored or deployed.

The elapsed time from starting SQL restore to the authenticated overview and
new Worker heartbeat was 11.48 seconds for this small fixture. It excludes the
backup, file-copy preparation and incident response; it does not establish
the installation's RPO or complete RTO. TLS used a test certificate without
trust validation, and this was not an older-version upgrade or a cross-machine
restore. SQL and Worker used an internal Docker network; Web additionally used
a separate network for a loopback-published HTTPS port. Test containers,
networks, images, databases and scratch files were removed afterwards.

On 2026-11-23 the same exercise was repeated with
`-Recovery -AgentPackageVersion 0.1.0-preview.3` and SDK 11.0.100. Before
backup, the central platform downloaded the existing `agent-linux-x64.zip`
from the `agent-v0.1.0-preview.3` GitHub Release, registered a test Agent via
the normal enrollment API and assigned that package. After SQL, key-ring and
package-directory restoration, the original Agent credential could read the
same assignment and manifest and download the 77,325,466-byte archive from
the restored platform. The archive size and SHA-256
`1a2bb297f4480c68d28b1fcfb65775958d5982b743405bf5256d2e5945e68e3e`
matched the release. The restored Web mapped `api.github.com` and `github.com`
to loopback during this check; this does not claim all external networking was
disabled. The original administrator session and a new Worker heartbeat were
also verified. Restore, Migrator, host startup and these checks took 15.7
seconds for the small fixture, excluding backup and file-copy preparation.

This confirms an assigned cached release can be retrieved after the same-host
restore without fetching it again from GitHub. It does not run an Agent or
Updater, install the archive, prove cross-machine recovery, test an older server
upgrade or establish RPO/RTO. Test resources and the downloaded archive were
removed afterwards.

On 2026-11-23 the release-package exercise was repeated with `-SeparateSql`.
After the checksum backup, the backup was copied through the test scratch
directory, the original SQL container was stopped, and the database was
restored on a newly started SQL Server 2022 container. The restored Migrator,
Web and Worker used that second SQL instance. The earlier administrator
session, new Worker heartbeat, Agent assignment, manifest and cached archive
download were verified again, including archive size and SHA-256. The restored
Web mapped the GitHub hosts to loopback during the package check.

Moving the backup, stopping the original SQL instance, starting the new SQL
instance, restoring, migrating and checking the small fixture took 41.01 seconds.
This excludes the backup itself, the earlier key-ring and package copy, initial
setup and incident response. Both SQL instances ran on the same computer and
Docker environment; this is not evidence of recovery across physical machines,
Agent installation or an installation's RPO/RTO.
