# Service discovery and approval

Status: REQ-22 implementation design. The ServiceObservability module does not yet implement this flow. REQ-23 owns OTLP ingestion; this document does not claim an OTLP receiver or formal telemetry storage exists.

## Identity and trust

An administrator provisions a dedicated application-source credential. Store only its verifier; authenticate the credential to derive a source ID on the server. Human sessions, third-party API credentials and host Agent credentials are separate identities. Discovery is permitted for an authenticated, enabled source, but it does not grant permission to write telemetry. An unauthenticated sender cannot create candidates.

Within a trusted source, use a bounded logical service key and environment as the discovery identity. The display name is metadata, never authorization; a rename does not change the key or ServiceId. A different environment or logical key cannot silently take over an existing ServiceId and requires an explicit administrative operation. Candidate and approved-service bindings are unique for that source, key and environment; the same name from a different source is a distinct candidate. `service.instance.id` stays in its original format and cannot by itself create or select a central resource ID. A Collector acting for multiple applications must authenticate or otherwise establish an administrator-defined trusted mapping tied to the actual ingress connection or credential, and enforce or overwrite the source identity: forwarding self-asserted names under a shared credential is not proof of application identity. Configuration of source credentials belongs in the product's onboarding flow; it does not require every application to deploy a Collector.

## Discovery and approval

The discovery boundary accepts bounded identity metadata only. Persist only the source binding, logical key, environment, display name and platform FirstSeen/LastSeen needed to manage candidates. Do not persist unapproved log bodies, metric points, spans or full incoming request bodies, including through queues or diagnostic logs. Enforce request, field, rate and candidate-count limits. REQ-23 must extract metadata at its real ingestion boundary without first buffering unapproved payloads to durable storage; a separate metadata endpoint is not a substitute for that integration.

Filter candidates using server time and `LastSeen >= now - 3 minutes`; scheduled cleanup is not the visibility rule. A continuing report updates LastSeen. Once expired, the next report begins a new window and resets FirstSeen. A future LastSeen after a server clock rollback must not be presented indefinitely as a fresh candidate. Only administrators can query candidates or approve them.

Implementation must choose and document bounded field, request, per-source and global candidate limits, and return an explicit retryable capacity response without silently dropping or falsely approving a candidate. Tests must exercise exact limits and concurrent admission. Use one database clock for both admission and listing across nodes; hide a candidate whose LastSeen is ahead of that clock. If the database clock catches up without a new report, the candidate may become visible until its ordinary three-minute window expires; a fresh observation during the rollback resets its window. These are test requirements, not claims that a schema or endpoint has already been built.

Approval and concurrent discovery serialize on the same source/key/environment binding. In one SQL transaction, approval creates or selects the unique approved service, records its source authorization and start time, and removes it from the pending view. Repeated or concurrent approval cannot create duplicates. New services are Private. Service and source identities use the resource key policy in ADR-0001; source-supplied keys do not become central GUIDs.

## Formal reception boundary

REQ-22 owns the source, service binding and authorization lifecycle; REQ-23 owns actual OTLP parsing and persistence. A receiver must revalidate the current source and service authorization under the same SQL transaction and compatible locking as its telemetry write. An earlier authentication result or cached approval alone cannot protect against concurrent revocation. Stop/revoke takes effect centrally; reauthorization starts a new collection period. Apply the existing event-time rules in `resource-lifecycles.md`; never backfill unapproved payload or fabricate receipt time.

REQ-22 can deliver candidate management, approval and the transactional authorization contract before REQ-23. End-to-end acceptance of real OTLP reception, revocation races and pre-approval payload exclusion remains pending until REQ-23 supplies the actual receiver. Public projection and group integration remain owned by REQ-12 and REQ-08 respectively.

## Integration references

- [OpenTelemetry Collector configuration and authentication](https://opentelemetry.io/docs/collector/configuration/): receiver and exporter authentication are configured separately; credentials must be assigned at an appropriate trust boundary.
- [OpenTelemetry Collector hosting guidance](https://opentelemetry.io/docs/security/hosting-best-practices/): receiver access and Collector resource use need controls.
- [OpenTelemetry .NET OTLP exporter](https://www.nuget.org/packages/OpenTelemetry.Exporter.OpenTelemetryProtocol): sends telemetry; it is not a receiver or an approval gate.
