# Temporal for local development

Run `docker compose -f packaging/temporal/compose.yaml up -d` from the repository root. The Temporal endpoint is `localhost:7233` and the developer UI is at `http://localhost:8233`. The PostgreSQL volume persists across restarts. These local credentials and loopback ports are for development, not a production deployment.

Run `docker compose -f packaging/temporal/compose.yaml down` to stop the services. Add `-v` only when the local Temporal history is no longer needed, to remove the project-owned PostgreSQL volume.
