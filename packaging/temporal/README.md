# Temporal for local development

Run `docker compose -f packaging/temporal/compose.yaml up -d` from the repository root. The Temporal endpoint is `localhost:7233` and the developer UI is at `http://localhost:8233`. The PostgreSQL volume persists across restarts. These local credentials and loopback ports are for development, not a production deployment.

Run `docker compose -f packaging/temporal/compose.yaml down` to stop the services. Add `-v` only when the local Temporal history is no longer needed, to remove the project-owned PostgreSQL volume.

## Single-host deployment example

`compose.deploy.yaml` is a separate example for a trusted host. Set `VIGILOOM_TEMPORAL_DB_PASSWORD` to a private PostgreSQL password before running `docker compose -f compose.deploy.yaml up -d --wait` from this directory. PostgreSQL uses a persistent named volume. Keep its password and volume when updating; changing the environment variable alone does not change an existing PostgreSQL user's password. Back up the PostgreSQL data along with your deployment.

The Temporal endpoint and Web UI bind to localhost by default. Services on another host need an accessible private address: set `VIGILOOM_TEMPORAL_BIND` to the host's private IP and configure the consuming services to use that address on port 7233. The UI binding can be changed independently with `VIGILOOM_TEMPORAL_UI_BIND`. These example endpoints do not configure authentication or TLS; restrict access to trusted operators and services. The example does not itself connect Vigiloom services to Temporal; the service integrations are delivered by their respective requirements.

Use `docker compose -f compose.deploy.yaml down` to stop the example without deleting its data. Do not use `-v` unless the Temporal history is intentionally being removed.
