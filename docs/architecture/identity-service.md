# Identity service data protection

When splitting Identity from Web, configure both hosts to use the existing
`Vigiloom:DataProtection:ApplicationName`, `KeyRingPath`, and, when configured,
the same `CertificatePath` and `CertificatePassword`. The key ring must be
accessible to every relevant instance. Preserve the existing keys: account
authenticator keys and recovery codes are encrypted with them. A new key ring
cannot decrypt previously stored two-factor secrets.

The Identity service and its `/internal/identity` endpoints are for the
restricted internal network and must not be exposed by the public gateway.
For initial setup, configure `Vigiloom:SetupToken` on the Identity service;
the Web host forwards the administrator's submitted value for verification.
For a new installation, create an empty Identity database, set
`VIGILOOM_IDENTITY_SQL_CONNECTION`, and run `Vigiloom.Migrator initialize-identity`
to apply the EF migrations and initialize the built-in roles. Administrator
recovery uses `VIGILOOM_IDENTITY_SQL_CONNECTION` when set, with the existing
`VIGILOOM_MIGRATION_SQL_CONNECTION` as a legacy fallback.

For Windows development, Identity defaults to the separate LocalDB database
`Vigiloom_Identity_Development` and listens on `http://localhost:5054` when
using its launch profile. Set `Services:Identity:Url` on Web and Worker to that
address to use the separate service; their existing database settings remain
unchanged. Create and initialize the Identity database before starting it.
The Identity EF design-time factory uses the same development database by
default; `VIGILOOM_DESIGN_SQL_CONNECTION` overrides it when required.

To copy an existing installation to a separate Identity database, stop the
platform and other writers first. Create an empty destination database, set
`VIGILOOM_MIGRATION_SQL_CONNECTION` to the existing platform database and
`VIGILOOM_IDENTITY_SQL_CONNECTION` to the destination, then run
`Vigiloom.Migrator migrate-identity`. EF applies the Identity migrations before
the copy. The copy requires empty Identity tables and commits all copied tables
together; keep the original database and key ring until the migrated service is
verified. This command prepares the data; deployment must also point the Identity
service and its remaining audit integrations at the appropriate databases.

When Identity runs separately, the platform holds its retention policy lock while
the Identity service counts or deletes its own audit records in a local transaction.
The two databases do not share a transaction: if the remote response is lost,
the scheduled job retries and an already-deleted batch is harmless. The permanent
initial-administrator marker remains excluded from cleanup.
