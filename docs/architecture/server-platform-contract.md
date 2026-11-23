# Central platform deployment baseline

The release workflow produces separate self-contained `win-x64` and `linux-x64`
archives for Web, Worker and Migrator. The .NET runtime is included; operating
system dependencies such as Linux ICU are not. Check Microsoft's current
[.NET 10 operating system support table](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)
and the operating system lifecycle before deploying. Supported operating systems
do not imply that every deployment method has been tested.

The EF Core 10 SQL Server provider supports SQL Server 2019 onwards according to
its [supported engines](https://learn.microsoft.com/en-us/ef/core/providers/sql-server/#supported-database-engines).
Vigiloom targets database compatibility level 150 or higher. EF's configured
compatibility level controls generated SQL; it does not change the database's
level. Migrator checks the target level. SQL Server hosting and edition are
deployment choices, subject to the selected engine's support and capacity.

| Environment | Evidence so far | Still to verify |
| --- | --- | --- |
| Windows x64, LocalDB SQL Server 17, level 150 | Fresh migration and isolated database backup / restore | Full application install, upgrade and complete recovery |
| Linux x64 containers, SQL Server 2022 | Fresh migration, HTTPS Web and Worker heartbeat | Upgrade and complete recovery; production permissions and TLS |
| Windows IIS / Kestrel and Linux systemd | Package instructions and examples | Installation, upgrade and recovery in each environment |

LocalDB is useful for development and isolated functional checks. It is not the
evidence for production ingestion capacity. For load testing use a real SQL
Server instance, record engine, edition, compatibility level and resources,
and run the workload described in [capacity validation](capacity-validation.md).
Record the actual operating system, architecture and package version for each
deployment run. The separate [Agent platform contract](agent-platform-contract.md)
describes the Agent, including the owner's Windows x86 acceptance policy.

The operator creates the database, provisions secrets and persistent storage,
and runs Migrator before starting Web and Worker. Back up the database together
with the key ring, certificates and package library; see [recovery](recovery.md)
and the package README for the complete procedure. A built archive does not by
itself demonstrate install, upgrade or restore.
