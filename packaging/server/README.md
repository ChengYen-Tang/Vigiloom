# Vigiloom central platform package

The package contains the Web, Worker, Migrator, Gateway and service hosts. The
Linux archive includes `compose.microservices.yaml`, which starts the services,
Temporal and PostgreSQL. SQL Server databases must be created separately with
compatibility level 150 or higher. The self-contained binaries include the .NET
runtime but still require operating-system dependencies such as ICU on Linux.

## Container deployment

Copy `container.env.example` to `.env` and configure the platform and service
SQL connections, TLS certificate and password, setup token, and Temporal
database password. Protect `.env`, certificates and the persistent data folder.
The Web and Resources services share the Agent package directory; all service
instances share the Data Protection key ring and application name.

For a new installation, create empty databases and run:

```sh
docker compose -f compose.microservices.yaml build
mkdir -p data/keys data/packages
vigiloom_uid=$(docker run --rm --entrypoint id vigiloom-server:local -u)
vigiloom_gid=$(docker run --rm --entrypoint id vigiloom-server:local -g)
sudo chown -R "$vigiloom_uid:$vigiloom_gid" data/keys data/packages
docker compose -f compose.microservices.yaml --profile setup run --rm migrator fresh-install
docker compose -f compose.microservices.yaml --profile setup run --rm migrator initialize-identity
docker compose -f compose.microservices.yaml --profile setup run --rm migrator initialize-resources
docker compose -f compose.microservices.yaml --profile setup run --rm migrator initialize-network
docker compose -f compose.microservices.yaml --profile setup run --rm migrator initialize-host-observations
docker compose -f compose.microservices.yaml --profile setup run --rm migrator initialize-observability
docker compose -f compose.microservices.yaml --profile setup run --rm migrator initialize-security
docker compose -f compose.microservices.yaml --profile setup run --rm migrator initialize-operations
docker compose -f compose.microservices.yaml up -d
```

If `VIGILOOM_DATA_PATH` or `VIGILOOM_SERVER_IMAGE` is customized, use those
values instead of `data` or `vigiloom-server:local` when preparing permissions.
Restrict access to the persistent directories and preserve them during updates.

Open Gateway's HTTPS `/account/setup` and create the first administrator using
the setup token. Remove the token from `.env` and recreate both `vigiloom-web`
and `vigiloom-identity`. Gateway is the only service with a published port.

For the first migration from a single-database installation, stop writers,
back up the database and persistent files, then use the corresponding
`migrate-*` commands with empty destination databases. For later upgrades of
an already split deployment, run the Migrator without arguments for the
platform database and `initialize-*` for each existing service database;
these commands apply migrations without copying the old data again. Preserve
the platform database and Worker while their remaining responsibilities are
being moved to services.

The full fresh-install Compose startup has been checked with isolated SQL
databases. Upgrades and backup restoration still require deployment validation.
