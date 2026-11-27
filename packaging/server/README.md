# Vigiloom central platform package

The package contains the self-contained `web`, `worker` and `migrator`
applications, along with the `gateway`, `identity`, `resources`, `network`,
`hostmonitoring`, `observability`, `operations` and `security` service hosts.
The Compose example below currently configures the Web/Worker deployment; the
additional service hosts require their own configuration. The package does not
install SQL Server or create a production database.
Choose a supported Windows or Linux x64 host and provide a SQL Server database
at compatibility level 150 or higher. Linux requires the distribution's .NET
native dependencies, including ICU. Self-contained means the .NET runtime is
included; it does not remove operating-system dependencies.

Before a new installation, create an empty database and give the deployment
identity schema-change permissions. Set `VIGILOOM_MIGRATION_SQL_CONNECTION` to
its connection string and run `migrator/Vigiloom.Migrator fresh-install` (use
the `.exe` on Windows). For an existing installation, stop Web and Worker,
back up the database and related files, and run the Migrator without the
`fresh-install` argument. See the repository's
`docs/architecture/sql-server-migrations.md` for the complete procedure.

Configure both Web and Worker with `ConnectionStrings__Vigiloom` and the same
data protection application name, key-ring path and certificate settings. Keep
the key ring and certificate private, persistent and available to both hosts.
The Web host also needs a persistent private
`Vigiloom__AgentPackages__CachePath` for downloaded Agent packages. Set these
values outside the package and back up the database, key ring, certificate and
package library together. Protect secrets in the deployment environment.

Start Web and Worker separately. Run Web with Kestrel or IIS (on Linux,
systemd can supervise the Kestrel process). Run Worker as a Windows Service or
systemd service; Migrator is a one-time deployment command. IIS requires the
ASP.NET Core Module / Hosting Bundle. Configure an HTTPS endpoint for Web and
provide a random `Vigiloom__SetupToken` of at least 32 characters. Open
`/account/setup` over HTTPS and use that token to create the first
administrator. Once bootstrap is complete, remove the token and restart Web.
Configure startup, service accounts, TLS and firewall rules for
the selected environment. Reverse-proxy deployment requires separate trusted
forwarded-header configuration and has not yet been validated.

The Linux archive includes example `systemd` units. They assume the package is
under `/opt/vigiloom`, a dedicated `vigiloom` account exists, and shared
environment settings are in `/etc/vigiloom/vigiloom.env`. Adjust these paths,
permissions and the HTTPS configuration for the host before enabling the units.
On Windows, register the Worker executable as a Windows Service; host Web
separately with Kestrel or IIS. These examples are not installers.

Do not treat a successful archive build as installation, upgrade, backup or
recovery validation. Record the exact OS, package version and SQL Server used
for each validation run. Central backup and recovery verification is tracked
by REQ-40-T04.

## Linux container example

The Linux archive includes a Dockerfile and Compose example. Extract the entire
archive, copy `container.env.example` to `.env`, and supply the runtime and
deployment SQL connections, HTTPS certificate path/password and setup token.
The TLS certificate must contain its private key. Prepare `data/keys` and
`data/packages` so the image's non-root `APP_UID` can write them; restrict
access to the key ring, package library and `.env`. If the key ring is
protected by a certificate, mount that certificate and set the corresponding
Data Protection environment variables for both Web and Worker.

Build with `docker compose build`, then run the Migrator once with
`docker compose --profile setup run --rm migrator fresh-install` for a new,
empty database, or omit `fresh-install` for an existing database upgrade.
Afterwards start the applications with `docker compose up -d`. The example
binds HTTPS to localhost; expose it only after configuring the intended network
and TLS deployment. After creating the first administrator, remove the setup
token from `.env` and run `docker compose up -d --force-recreate web` so the
new container no longer receives it. Keep persistent volumes and secrets when updating the
image. This container example still requires real deployment verification.

## Temporal deployment

The `temporal` directory includes a single-host Temporal and PostgreSQL Compose example and its setup instructions. It is separate from the central platform Compose example. Service-specific Temporal integration is delivered with the corresponding microservice.
