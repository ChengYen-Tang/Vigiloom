# Release capacity validation

This document defines the first repeatable workload for REQ-40. It is a test
profile, not a capacity claim or a limit on the number of devices. Record the
tested commit, configuration versions, hardware, OS, SQL Server version and
compatibility level alongside every result. A different workload or retention
period needs its own measurement.

## Scope

The release covers the existing device and Ping workflows, scheduled network
speed tests, Agent installation and updates, host metrics and inventory, host
logs and security monitoring, application telemetry and health checks, public
status, notifications, events, audit and platform administration. The Project
requirements are the acceptance source for each feature. AI Agent / LLM analysis
is deferred; this does not defer host Agents or rule-based security monitoring.
The initial platform has one shared organization and two built-in roles; this
profile does not assume multi-tenant isolation or add SSO.

The release acceptance must include all of these requirement groups:

| Requirements | Coverage |
| --- | --- |
| REQ-01–06 | Platform architecture, storage, accounts, API, UI and scheduling |
| REQ-07–16 | Devices, groups, Ping, speed tests, publication, alerts and LINE |
| REQ-17–21 | Agent platform, enrollment, transport, installation and versions |
| REQ-22–25 | Application discovery, telemetry, health and service details |
| REQ-26–31 | Host inventory, performance, processes, logs, integrity and details |
| REQ-32–37 | Intelligence, Sigma, vulnerabilities, baselines, IOC/YARA and events |
| REQ-38–41 | Audit, diagnostics, retention, release validation and CI |

This is a scope index, not a claim that every release scenario has passed.
Use the current requirement acceptance and actual test evidence when running
T05; do not infer end-to-end completion from the list or from a feature being
merged. REQ-40 release validation remains in progress at the time of this
profile.

Agent distribution targets Windows x64 and x86 and Linux x64 within the .NET 10
support conditions. Linux x86 and legacy Windows are excluded. The owner has
approved using the Windows x64 result for corresponding Windows x86 acceptance;
record this separately from a native x86 run. The tested service environments
and native dependencies are documented in [the Agent platform contract](agent-platform-contract.md).

## Implemented starting values

These are application defaults, not measured throughput. Administrators may
change the settings exposed by each feature; capture the actual effective values
and versions for a test rather than assuming the defaults are still active.

| Signal | Starting value | Source |
| --- | --- | --- |
| Ping | 30 s; 2 s timeout; 3 failures / 2 successes; concurrency 64 | `PingSetting` |
| Host / process metrics | 30 s / 60 s | `AgentMetricSettings` |
| Inventory | 6 h | `InventorySettings` |
| Agent batch | At most 1 MiB uncompressed or 1,000 items | `AgentBatchCodec` |
| Logs admission | Per source 20 records/s, burst 100; global 1,000/s, burst 2,000 | `LogQuotaValues.Default` |
| Trace admission | Per source 200 spans/s, burst 1,000; global 2,000/s, burst 4,000 | `LogQuotaValues.TraceDefault` |
| OTLP Log / Trace record | Up to 256 KiB per record and 4 MiB per request | `LogIngestion`, `TraceIngestion`, exporters |
| Application sample traces | Parent-based; 10% of root traces unless SDK configuration overrides it | `Vigiloom.Sample.AspNetCore` |
| Raw / rolled-up Ping | 30 / 365 days | `PingSetting` |
| Application logs / traces | 14 / 7 days | `TelemetryRetentionSetting` |
| Application metrics raw / rollup and health | 30 / 365 / 365 days | `TelemetryRetentionSetting` |
| Audit and background history | 365 days | `AuditRetentionSetting` |
| Speed test | Automatic every 6 h; results 365 days | `SpeedSetting` |

The application owns neither third-party SDK sampling nor a universal Log
level. The sample SDK's configuration describes the sample only; record the
actual application and Collector configuration in every run.

The Administrator settings pages provide the effective Ping policy and
retention (`/settings/ping`, `/settings/ping/retention`), Agent collection
settings (`/settings/agents`), application retention
(`/settings/service-reception/retention`), Log and Trace quotas
(`/settings/log-quota`, `/settings/trace-quota`), and audit retention
(`/settings/audit/retention`). Application-side sampling and hard request-size
limits are separate from those pages and must not be described as adjustable
there. Record source versions and any per-device overrides along with the
global values.

## Workload profile v1

Use one Ping target and one Agent per host, and one independent application
service per ten hosts; these are test assumptions, not a restriction on
deployment. Each host has 20 metric series sampled every 30 s and emits an
average of 0.2 host logs/s. Each service receives one root request/s, producing
one application log and, when sampled, one server span. It also emits ten OTLP
metric series every 60 s and receives a health check every 30 s. Use 512-byte
average Log bodies, 1 KiB per span and 100-byte metric points; each application
metric series has five labels. These sizes are input payload assumptions, not
SQL row sizes. Exercise the sample's 10% root sampling with no upstream
sampling override. Use the implemented retention defaults above.

| Devices / Agents | Services | Ping checks/s | Host metric points/s | Host logs/s | Application logs/s | Sampled spans/s | Concurrent UI readers |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 30 | 3 | 1 | 20 | 6 | 3 | 0.3 | 2 |
| 100 | 10 | 3.33 | 66.67 | 20 | 10 | 1 | 5 |
| 300 | 30 | 10 | 200 | 60 | 30 | 3 | 10 |
| 500 | 50 | 16.67 | 333.33 | 100 | 50 | 5 | 15 |

Health checks contribute services / 30 per second; application metrics add
services / 6 points per second. These are averages; record
batch sizes and burst shape as well. Keep per-source and global quota values in
the result so admission rejection is not mistaken for successful ingestion.

For each size, run a steady period and a separate short burst, disconnect and
resume a subset of Agents, and record the input and accepted counts, backlog,
retries and any lost or rejected data. Measure API/UI latency, ingestion lag,
Web/Worker/SQL and Agent CPU, memory, disk I/O, database and index growth.
Record actual duration and extrapolate storage only with the observed rate and
stated assumptions. Exercise time-window reads and deduplication receipt growth;
do not infer performance from UUID ordering or a successful migration.

Test results, SQL edition and compatibility level, infrastructure and the exact
scripts will be supplied by REQ-40-T02/T03. Failed or unexecuted scenarios must
remain visible as such. Backup/restore and upgrade exercises belong to T04;
cross-feature user journeys and platform combinations belong to T05.

### Initial overview query

On 2026-11-23, the existing
`WorkspaceOverviewTests.Reader_SeesIndependentStatesAndAbnormalDevicesAcrossPages`
test passed with 500 fixture devices. It reported 439 ms for one overview HTTP
request on Windows 11 Enterprise x64 build 26300, .NET SDK 11.0.100 and LocalDB
17.0.4025.3 Express, using the isolated test database at compatibility level
150. The request used the in-process test host, a single reader and sparse
fixture data. This is one observation, not a latency percentile, sustained
load, a network measurement or evidence that the profile above is supported.
The SQL fixture was cleaned up by the test harness.

On 2026-11-23, `tools/capacity/overview.js` was also exercised against a real
Kestrel test host and isolated LocalDB, with 30 fixture devices and two readers
for 10 seconds. A Read only browser session authenticated the k6 requests; all
four measured checks passed. This was a short integration check of the script
and authorization, not a sustained load test: there was no Worker or simulated
Agent/application ingestion. The isolated database was cleaned by the fixture.

On 2026-11-23, the Agent capacity sender was checked against a real isolated
Kestrel HTTPS endpoint and LocalDB with 30 enrolled test identities. In a
10-second run, the staggered senders received 204 for 10 metric batches (200
points) and 60 log batches (60 logs); database counts matched. No failed HTTP
status was recorded. This checks the sender, authentication and persistence,
not sustained capacity, Worker processing, actual host collection or the
30-second complete metric cycle. The private test CA was trusted only by the
sender and the test database was cleaned by the fixture.

On 2026-11-23, a 60-second Agent-only check used the same isolated Kestrel
test host and the capacity sender with 30 enrolled test identities, backed by
SQL Server 2022 container `16.0.4325.4`, Developer Edition, compatibility 160.
The host was Windows 11 Enterprise build 26300, 8 logical CPUs and 32 GiB RAM;
Web, sender and SQL shared that machine. The sender reported 1,200 accepted
metric points (60 batches), 359 accepted logs, 554,422 attempted payload bytes,
and one cancelled request of each kind at the deadline. No HTTP rejection was
reported, and both accepted counts matched SQL rows. The fixture database and
container were removed after the run.

This is a short, Agent-only observation, not the full 30-device profile or a
capacity guarantee: there were no concurrent UI readers, application telemetry,
production Worker, retention, replay, storage-growth or long-running tests.
The sender measures closed-loop input and cancellation can leave an in-flight
result unknown. Fixture provisioning for that run was temporary. A subsequent
SQL Server 2022 integration check verified the repeatable
`tools/capacity/run-agent-load.ps1` provisioning, TLS, database count checks and
cleanup with 30 identities for six seconds; it is not a sustained load result.
Full workload and storage measurements remain necessary before T03 acceptance.

Further Agent-only 60-second runs used the same SQL Server 2022 container
version and host as above. Each run used a fresh isolated database. Accepted
counts matched the database; none of these runs included application telemetry,
Worker, concurrent UI, retention or replay:

| Agents | Accepted metric points | Accepted logs | Attempted payload bytes | Result |
| ---: | ---: | ---: | ---: | --- |
| 100 | 4,000 | 1,196 | 1,836,931 | One metric and one log request cancelled at deadline |
| 300 | 12,000 | 2,745 | 4,698,368 | All responses 204; 66.67 s including drain |
| 500 | 20,000 | 2,893 | 6,236,149 | All responses 204; 71.29 s including drain |

The first 300-Agent attempt cancelled in-flight requests at the deadline and
failed the database-count check; the sender was corrected to drain requests and
the successful rerun is shown above. Timeouts or connection failures can still
leave an individual request's commit status unknown.

The target profile calls for about 3,600 and 6,000 host logs respectively for
300 and 500 Agents over 60 seconds. The closed-loop sender achieved less than
those targets, so these results do **not** establish that the platform handles
the intended workload. Web, SQL and the sender shared one machine, and the run
did not isolate their resource usage or establish the cause of the reduced
rate. Investigate using measured request latency and SQL/host resource data
before deciding whether any change is needed.

## Initial recovery check

On 2026-11-23, Windows 11 Enterprise x64 build 26300 and SQL Server LocalDB
17.0.4025.3 Express were used for an isolated database check of code based on
`8effe37`, published with .NET SDK 11.0.100. The self-contained
Windows Migrator performed a fresh installation, a test group was inserted, the
database was backed up with checksum, restored under a different name, and the
Migrator was rerun against the restore. The group remained present. Both test
databases and the backup were removed afterward.

This verifies a database-only restore and rerunning the current migration on a
small fixture. It does not establish an RPO/RTO, a previous-version upgrade, or
recovery of Data Protection keys, certificates, package files or a deployed
service. T04 remains open until those aspects are exercised.

## Initial container check

On 2026-11-23, Linux x64 self-contained Web, Worker and Migrator outputs were
packaged into the container example. An isolated SQL Server 2022 container was
created; Migrator applied a fresh installation, Web served the setup page and
Blazor assets over HTTPS, and Worker registered a Hangfire server heartbeat.
The test used a temporary self-signed certificate and SQL administrator account
for the isolated database. Containers, network, temporary files and the test
image were removed afterward. This does not validate production TLS, restricted
SQL permissions, IIS, systemd installation, upgrade or backup recovery.

`tools/server-deployment-smoke.ps1` repeats this check on a Windows development
host with PowerShell 7, .NET SDK and Docker. It creates its own temporary SQL
Server, test certificate, network and container names, and removes them when
finished. Use `-SdkVersion` only when the repository-pinned SDK is unavailable;
the override is written only to the test's temporary directory.
