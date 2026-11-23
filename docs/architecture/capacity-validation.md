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
