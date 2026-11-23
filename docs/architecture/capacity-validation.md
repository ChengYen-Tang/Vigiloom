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

The same script accepts `-IncludeServices` to run one pre-approved application
service per ten synthetic Agents using the real OTLP HTTP endpoints. A single
test source credential represents a shared Collector. Each service schedules a
512-character log per second, ten metric points per minute and one span per
ten seconds. HTTP errors and OTLP partial rejection fail the run; accepted
counts are checked against SQL rows. Services send in a closed loop and catch
up when delayed: compare elapsed time with the requested duration rather than
assuming a sustained rate. This synthetic producer is not the sample SDK or
the real Agent's offline queue, and it does not exercise Worker or UI traffic.

On 2026-11-23, the isolated SQL Server 2022 / Kestrel fixture completed a
10-second smoke run with 30 Agents and three services. The Agent producer
reported 200 accepted metric points and 59 accepted logs in 10.02 seconds;
OTLP reported 30 logs, 30 metric points and three spans in 9.73 seconds. All
accepted totals matched SQL; no HTTP error, rejection or remaining Agent batch
was reported. This validates the fixture, not sustained release capacity.

A 60-second attempt with 500 Agents and 50 services sharing one source on the
same host did not pass: at least one OTLP request received HTTP 503. The Agent
sender reported 19,980 accepted metric points and 5,993 accepted logs with no
remaining batches after 110.99 seconds. OTLP reported 240 accepted logs, 40
points and 24 spans after 112.15 seconds. Since the test stopped on the OTLP
error, neither set of counts was subsequently checked against SQL in this run.
The OTLP sender stops an individual service's loop on its first failure and
does not represent the intended full offered load after that point. A 503 alone
does not establish whether the receiver gate, a downstream timeout or another
condition caused it. This result is not a passing 500-device capacity test.

Two subsequent 10-second diagnostic attempts used the same 500-Agent,
50-service fixture. With the existing four OTLP request slots, 46 log requests
returned 503 `receiver-busy`; the service sender reported 40 accepted logs,
40 points and four spans in 22.43 seconds. A temporary local experiment with
16 slots reported 34 `receiver-busy` responses and nine `export-retry`
responses, with 70 logs, 70 points and seven spans in 22.91 seconds. Each
service stopped at its first failure, the results were not SQL-verified, and
these short attempts do not demonstrate sustainable throughput. The original
four-slot setting was restored. Raising the limit alone did not remove the
failures; exporter batching and the actual retry configuration need separate
evaluation. The failure breakdown covers HTTP failures received by the
synthetic sender, not transport errors or OTLP partial rejections.

The 50-service profile also exceeds the default application-source Log budget
when all services share one source: 50 logs/s offered versus a 20 logs/s
refill rate and 100-record burst. Starting with a full bucket, no more than
approximately 1,300 records can be admitted over 60 seconds at that default,
even if batching eliminates HTTP concurrency failures. This is a calculation
from the configured token bucket, not a measured throughput result or an
explanation of the earlier `receiver-busy` responses. A sustained test needs
to record and deliberately select a suitable source quota or representative
source grouping, along with actual Collector batch, retry and loss behavior.
These settings can be changed by an administrator; no default was changed by
this diagnostic.

On 2026-11-23 the existing `Collector_SeparatesAuthenticatedSourcesAndForwardsToRealPlatform`
integration test passed against LocalDB and Collector Contrib 0.164.0 using the
repository's Collector example. It verifies actual forwarding and recovery
after an injected downstream write failure and an exhausted source quota,
along with other source and signal checks. This is a functional retry check,
not a mixed-capacity benchmark, durable-delivery guarantee or measurement of
Collector loss under a sustained overload.

The manual script also supports `-IncludeServices -Collector`, which uses the
repository's Collector Contrib 0.164.0 configuration and a test-only shared
Log policy with a per-source rate of 100 records/s, burst 100 (global 1,000/s,
burst 2,000). Application requests go through Collector; the fixture waits for
central SQL row counts before declaring success. Collector acknowledgements
alone do not prove persistence. This is still a synthetic, closed-loop
producer; the reported elapsed time must be compared with the requested period.

On 2026-11-23, with the same SQL Server 2022 / Kestrel shared host as above,
10-second tests passed through the real Collector: 30 Agents / 3 services
stored 30 application logs, 30 metric points and 3 spans; 500 Agents / 50
services stored 500 application logs, 500 points and 50 spans. The latter
service producer took 29.13 seconds to complete its planned 10-second work;
the Agent sender took 22.38 seconds to generate and drain 3,320 metric points
and 992 logs. Both were checked against SQL. These are short completion checks,
not evidence that the requested rates can be sustained.

A subsequent 60-second run with the same 500 Agents, 50 services, Collector
configuration and test-only quota completed with all accepted counts matching
SQL: 19,980 Agent metric points and 5,992 Agent logs, plus 3,000 application
logs, 500 metric points and 300 spans. The Agent producer and drain took
109.16 seconds; the closed-loop application producer took 154.82 seconds to
complete the planned 60-second work, followed by 2.04 seconds waiting for
central counts. No generator queue overflow, remaining Agent batch or HTTP
failure was reported. Because the application sender falls behind and catches
up rather than continuing at a fixed offered rate, this result does not prove
that the host can sustain the stated workload, nor does it cover UI, Worker,
replay, retention or storage growth.

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

### Investigation of the 300-Agent run

An additional 300-Agent, 60-second run on the same host and SQL Server build
recorded 12,000 accepted metric points and 2,732 accepted logs, with no HTTP
rejections; drain made the elapsed time 66.69 seconds. Mean/max HTTP attempt
times were 4.85/6.86 seconds for metrics and 6.63/7.05 seconds for logs. These
include client connection waiting and do not measure server time alone; the
sender was limited to 100 simultaneous HTTP connections.

During that run, four snapshots of SQL Server's active requests each showed 98
requests waiting on `LCK_M_X`, with maximum observed waits of 1.95–2.30 seconds.
Another snapshot identified 97 waits on the application lock
`Vigiloom.LogAdmission`. This demonstrates contention on the shared log
admission lock in this test, not that it is the only bottleneck. Any change to
its transaction boundary must preserve the intended quota, duplicate and
rollback behavior and be checked against a comparable workload.

A trial moved quota admission to immediately before transaction commit, while
retaining the same transaction. In a comparable 300-Agent run it reported
3,502 accepted logs and 11,840 accepted metric points, but also 19 HTTP 500
responses and 8 timeouts. The trial was reverted. The load fixture was updated
to fail when any HTTP attempt fails or has an unknown result, even if the
accepted counts match SQL. The cause of those errors requires investigation;
the earlier shared-lock contention remains an observed issue, not a license to
weaken the existing transaction guarantees.

On investigation the 500 responses in a repeat of this trial included
`Agent device lock could not be acquired`. The capacity sender had been
sending metric and log batches concurrently for the same Agent, whereas the
actual Agent batch worker sends its queue without overlapping requests. The
previous measurements therefore do not faithfully model Agent transport. The
sender was changed to serialize per-Agent requests; repeat the measurements
before using the earlier results to judge production capacity. This correction
does not model the actual Agent queue, batching or retry behavior.

After that correction, new 60-second Agent-only runs used SQL Server 2022
`16.0.4325.4` Developer, compatibility 160, on the same host:

| Agents | Metric points | Logs | Total elapsed | Mean metric/log HTTP |
| ---: | ---: | ---: | ---: | ---: |
| 300 | 11,500 | 2,684 | 65.39 s | 3.76 / 5.85 s |
| 500 | 17,480 | 2,681 | 69.40 s | 7.24 / 9.20 s |

All completed requests in both runs returned 204 and their counts matched the
database. The sender's nominal 60-second profile calls for 12,000 / 20,000
metric points and 3,600 / 6,000 logs respectively. Because this closed-loop
generator waits for a response and does not queue missed samples, the shortfall
is ungenerated load, not a measured count of lost Agent records. The latency
includes client connection waiting. Web, SQL and generator shared one machine;
these short runs do not isolate the bottleneck or establish capacity with
production Agent batching, durable queues, application telemetry or UI load.

### Synthetic in-memory queue runs

The generator was later changed to produce samples independently of HTTP and
queue up to 128 batches per Agent, with one sender per Agent. On 2026-11-23,
the same SQL Server 2022 / shared-host setup produced these results:

| Agents | Generated points / logs | Accepted points / logs | Result |
| ---: | ---: | ---: | --- |
| 300 | 12,000 / 3,595 | 12,000 / 3,595 | No rejection or remaining batches; 85.99 s total |
| 500 | 19,980 / 5,990 | 17,220 / 5,068 | 561 batches unsent; 499 in-flight requests cancelled at drain deadline; 120.03 s total |

The 300-Agent SQL counts matched all accepted records; generating ended at 60
seconds and draining took about another 26 seconds. The 500-Agent run failed
the fixture's drain requirement. Its 499 cancelled requests have unknown commit
status and the fixture did not reach its final SQL count comparison; neither
the cancellations nor unsent batches establish lost production Agent data.
No queue-full rejection was reported. These are one-minute synthetic runs,
not a production capacity or durability guarantee. The test queue is in
memory and does not reproduce the Agent's disk queue, batching, retries or
continuous operation, and the Web host, sender and SQL shared one machine.

With the corrected synthetic queue model, the late-admission transaction trial
was repeated at 500 Agents. It generated 19,960 metric points and 5,984 logs;
all metric requests and 5,982 log requests returned 204, with two log requests
timing out. The in-memory queues drained in 105.18 seconds total, but the
fixture correctly failed because the two requests have unknown commit status;
it therefore did not perform the final SQL count comparison. This was a shorter
drain than the original 500-Agent run, which still had a backlog at 120 seconds,
but it does not demonstrate error-free operation. The trial change was again
reverted, pending a reliable solution and repeatable validation.

A different change retained the original admission lock and transaction, but
combined the SQL reads and writes of the two quota buckets to reduce round
trips. In a comparable 500-Agent synthetic-queue run, 19,980 metric points
and 5,986 logs were generated and accepted; SQL counts matched, with no queue
rejections, remaining batches or HTTP errors. The total time was 116.15 seconds,
including about 56 seconds after production stopped. This run passed its
bounded drain check, but the accumulated backlog and short duration do not
establish sustained capacity. The earlier baseline still had 561 unsent batches
and 499 unknown in-flight results at its 120-second deadline, so no exact
completion-time comparison is available. More representative mixed traffic and
longer measurements remain necessary.

The synthetic sender previously limited all Agents to 100 shared HTTP
connections. It was changed to permit one connection per simulated Agent; this
still uses one process and is not equivalent to independent machines. With the
combined bucket SQL and 500 connections, a 500-Agent run generated and accepted
19,980 metric points and 5,984 logs without HTTP errors; total time was 117.46
seconds, including drain. One snapshot during the run showed 98 SQL requests
waiting for an exclusive lock. The run remains too short and too concentrated
on one host to establish production capacity.

Moving Agent log admission to the end of its transaction was also tried with
the combined bucket SQL and 500 allowed connections. One run drained in
81.86 seconds without HTTP errors; a second took 82.93 seconds but reported 44
timeouts, whose commit status is unknown. The second run failed and the trial
change was reverted. These runs show possible throughput improvement but do not
establish a reliable change or identify the timeout cause.

The admission lock acquisition and bucket reads were subsequently combined
into one SQL command without changing the lock or transaction boundary. A
500-Agent run with 500 allowed HTTP connections generated and accepted 19,960
metric points and 5,984 logs, with no HTTP errors or remaining batches; SQL
counts matched. Total time was 107.91 seconds including drain, compared with
117.46 seconds for the earlier single run with the same connection limit. This
is a limited observation, not a statistically established improvement or a
sustained-capacity result; both runs accumulated backlog on a shared host.

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
