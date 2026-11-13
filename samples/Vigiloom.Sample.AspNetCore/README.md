# ASP.NET Core application example

This .NET 10 sample exports application Logs, Metrics and Traces through OTLP and exposes the standard ASP.NET Core Health Checks response. It does not install the Vigiloom host Agent or require a device association.

## Connect the sample

1. In Vigiloom, an Administrator creates a trusted application source under **待加入服務**. Store its credential in the deployment platform's secret store; it is displayed only when issued or rotated.
2. Configure the sample process with the following non-secret values and a protected exporter header. Use an HTTPS address trusted by the sample's operating system.

   | Setting | Example or purpose |
   | --- | --- |
   | `VIGILOOM_SAMPLE_INSTANCE_ID` | Required, stable and unique for this running service instance, for example `orders-01`. Distinct instances must use different values. |
   | `OTEL_SERVICE_NAME` | Logical service key, for example `orders`. |
   | `VIGILOOM_SAMPLE_NAMESPACE` | Optional service namespace, empty by default. |
   | `VIGILOOM_SAMPLE_ENVIRONMENT` | Environment, for example `production`; defaults to `development`. |
   | `OTEL_EXPORTER_OTLP_PROTOCOL` | `http/protobuf`. |
   | `OTEL_EXPORTER_OTLP_ENDPOINT` | The Vigiloom HTTPS base address followed by `/api/v1/otlp`, or the address of a trusted Collector ingress. The exporter adds signal-specific paths. |
   | `OTEL_EXPORTER_OTLP_HEADERS` | Provide an `Authorization` bearer header through a secret store. For direct delivery it contains the Vigiloom source credential; for the provided Collector example it contains that Collector ingress's separate credential. |

   OTLP header environment values use comma-separated `key=value` pairs. Encode reserved characters as required by the SDK; the space between `Bearer` and the credential is represented as `%20`. Do not paste real credentials into source files, command history, logs or screenshots. If a private CA is used, the .NET OTLP exporter supports `OTEL_EXPORTER_OTLP_CERTIFICATE` pointing to its trusted PEM CA certificate.

3. Build and start the sample with `dotnet run --project samples/Vigiloom.Sample.AspNetCore`. Its default ASP.NET Core development address is local to the host; configure the listener and TLS appropriate to the deployment. Request `/` to generate application activity. The process must have the required instance ID configured before it starts.
4. On the Vigiloom **待加入服務** page, select the recently reporting source and approve the correct logical service. Before approval, only minimal candidate identity is retained; earlier Logs, Metrics and Traces are not backfilled. Send another request after approval, then inspect the service detail page and its last-received times.
5. For health, permit the required destination network in Vigiloom and configure the sample's reachable `/health` URL for the approved service. The sample's health endpoint uses the standard plain-text ASP.NET Core response and demonstrates basic liveness only: no database or external dependency checks are registered. Add application-specific checks when those dependencies need to affect health. Restrict network access and use TLS appropriate to the deployment; do not expose health details unnecessarily.

The sample defaults to ParentBased tracing with a 10% root sampling rate when no SDK sampler is configured. For a short-lived validation environment, set `OTEL_TRACES_SAMPLER=always_on` to make test traces easier to observe; choose an appropriate policy for the real workload. The SDK's default metric export interval is 60 seconds. These are application-side settings and can be changed by the application owner.

For a shared Collector, see [`deploy/collector/README.md`](../../deploy/collector/README.md). Collector ingress credentials and Vigiloom source credentials serve different trust boundaries. A Collector's own health does not prove that the application is healthy.
