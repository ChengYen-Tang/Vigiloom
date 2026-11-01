# REQ-11 Speed test execution

> **Superseded work in progress, 2026-11-02:** Pingvalue used Speedtest.net. Selecting M-Lab as a replacement was an incorrect, unauthorized change. The M-Lab-specific design and code below are retained temporarily for traceability and must not be shipped or treated as the REQ-11 solution. The current source of truth is `monitoring-execution.md` and the corrected REQ-11 card. A Speedtest.net integration is subject to verification of current authorization, distribution and automated/commercial use conditions.

This requirement uses the already delivered API, Blazor and scheduler foundations (PRs #11–#13, verified Server/WebAssembly and SQL tests). It is not waiting for those cards' unrelated downstream acceptance. T01 owns execution/settings/results; T02 owns history/detail/trend presentation. Default visibility is private.

## Provider and build

- M-Lab NDT7 reference client v0.10.1, source commit `1f6adcf81a3f29cae933a2e317e86e24826e2900`, Apache-2.0; retained in `tools/ndt7/reference` with license/authors and one explicit completion-error hook. The measurement algorithms remain the reference implementation.
- NuGet was checked first: SpeedTest.Net is deprecated; SpeedTest/SpeedTest.NetCore target different providers; LibreSpeed.NET is for LibreSpeed. None directly implements the selected NDT7 provider plus the required transport limit. Reuse the official client rather than replace its protocol in C#.
- Go 1.27.3 is build-only. `build/build-ndt7.ps1` pins the toolchain, builds Windows/Linux without CGO and emits SHA-256; Worker output/publish includes the executable. CI builds and tests on both OSs. Windows x86 can be cross-built explicitly; central Worker deployment support still requires the .NET 10 platform conditions. Users install no Go runtime.
- The process uses fixed application-owned paths, stdin JSON and bounded output. No caller-supplied executable, shell, URL or flag. M-Lab access tokens remain in memory and are not persisted/logged. Local full URLs and raw network errors are not returned as diagnostics.

## Source/policy review (2026-11-02)

- [M-Lab AUP](https://www.measurementlab.net/aup/) dated 2025-07-15: user consent; automated ≤4/day with randomized timing; interactive ≤40/day; commercial integrations require sponsorship/in-kind contribution and may need registration or additional terms.
- [Privacy](https://www.measurementlab.net/privacy/) dated 2025-02-17: public-facing IP and measurement data are published and retained indefinitely. Application privacy controls do not retract third-party data. Administrator sees both policy links, disclosure and a specific consent checkbox; default is no consent and automatic off.
- [Locate v2](https://www.measurementlab.net/develop/locate-v2/) gives complete URLs with required tokens. Only WSS, normal certificate verification, allowed M-Lab suffix/path/port, non-private resolved IPs pinned to the actual connection, no HTTP redirects, and one matching download/upload endpoint are accepted. No hidden endpoint retry.
- Running external tests is separate from local implementation validation; no test fixture or CI contacts M-Lab or publishes a user's IP. A real external test requires the administrator's informed consent and consumes the same persisted quota.

## Ownership and persistence

- `SpeedSetting` is a UUID v7 managed resource, nonclustered Guid PK plus unique clustered bigint identity RowId and rowversion. Consent actor/time/policy version and update actor/time are recorded. Changing settings invalidates stale form submissions; return the exact saved snapshot.
- `SpeedRun` is an independently manageable/cancellable job with a permanent detail URL, so it follows the resource classification (UUID v7 Id, nonclustered PK, clustered identity RowId). It is not a high-volume raw sample. RequestedAt/StartedAt/CompletedAt are UTC and queries order by time plus the internal tie-breaker, never Guid time.
- `(RequestedBy,RequestId)` is an idempotency key, including automatic null actors; repeated manual requests return the same job. A filtered unique index admits one Queued/Running job for the central location. All settings/queue/claim/complete mutations take the same SQL settings gate, then run rows.
- Quotas count each reservation within a rolling 24 hours based on actual start time, or request time if it never started, including failed/cancelled/interrupted jobs, across restarts/Workers: ≤4 automatic and ≤40 total. Counts are conservative rather than refunded after unknown external outcomes. Auto intervals 6–168h use ±20% jitter, with the hard rolling limit as the final gate. The minute Hangfire job checks desired due time; no catch-up samples or automatic retries.
- A queued job expires after five minutes if no Worker starts it. A running job has a fresh UUID lease, deadline `timeout+30s`, no renewal beyond bounded native execution, and can finish only while lease/state remain current. Abandoned jobs become Interrupted, never retried automatically or fabricated as success.
- Consent revocation atomically cancels queued jobs and flags running jobs, blocking new claims immediately; live execution checks at 500ms and cancels the child. Disabling automatic similarly stops automatic jobs without cancelling a manual job. Cancellation closes stdin so the child stops, gives a short shutdown grace, then kills a remaining process tree. Source measurements may be partial and in-flight packets cannot be recalled.

## Measurements and limits

- Max 1–200 MiB combined transport budget, max 5–60s whole-operation timeout. Shared counters wrap the Locate lookup and TLS measurement connections: real reads/writes reserve against the budget before I/O. TLS/HTTP/WebSocket overhead on those connections counts. Kernel/in-flight buffering can make actual wire traffic differ; never claim a line-byte hard cap.
- NDT7 application bytes/elapsed microseconds convert to decimal Mbps (`8 * bytes / microseconds`). Upload uses server-received application data; download uses client-received data. TCP MinRTT microseconds convert to milliseconds. No positive sample means absent, not 0 Mbps; unavailable/NoServer/interrupted byte totals are unknown rather than fabricated zero.
- Budget/timeout/cancel/failure preserves known partial values but remains visibly non-success. Trends must break at non-success entries rather than connect a partial run as normal throughput.
- Server identity is the verified endpoint host without query/token. City/country/coordinates only come from Locate; ISP is absent when the provider supplies none. No reverse lookup, fabricated coordinates or extra IP-disclosure service.
- History is private, bounded to UTC half-open ranges of ≤31 days, 50 rows/page, 1,000 chronological trend points with explicit truncation. Earliest available record is reported; lifecycle deletion remains REQ-39. Exact result detail remains separately queryable by authorized Id.
