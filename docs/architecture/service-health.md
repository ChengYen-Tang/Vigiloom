# Application service health and history

Status: design in progress for REQ-24. This document defines the proposed contract;
implementation and cross-feature acceptance are not implied by its presence.

## Identity and collection

Application health belongs to an approved logical service, independently of a
device, Agent heartbeat, Collector liveness or Ping. A configured HTTP health
endpoint is explicitly associated with a service or a known service instance;
neither its address nor the Collector host infers that association. Initial
collection uses a central Worker pull. ASP.NET Core's standard Health Checks
endpoint is one supported producer; OTLP does not automatically carry its
result. Push is not represented as available by this implementation.

Administrators explicitly configure and enable each target. The default is a
30-second interval and 5-second timeout; both are configurable, subject to
validated bounds. A worker uses bounded concurrency and a durable lease; it
does not hold a database transaction over DNS or HTTP. Before committing a
result, it rechecks source authorization, approved service, instance, target
configuration and lease owner and generation in the same SQL transaction as
the result, including that the lease has not expired according to the SQL
clock. Stopping a service, retiring
an instance or changing a target must prevent an in-flight obsolete result
from being accepted. Do not backfill missed checks as successful after restart.

Targets are administrator-controlled network access, including legitimate
private networks. Validate schemes, hosts and ports; reject userinfo, fragments
and redirects, restrict destinations to an explicit administrator-managed
allowlist and enforce the same decision on the actual resolved connection to
prevent DNS rebinding. Keep TLS certificate and host verification enabled.
Use bounded request duration and response size and never log credentials, full
target URLs or exception content. The detailed allowlist and credential model
must be reviewed against existing network-probe protections before implementation.

The first response contract is the ASP.NET Core default: UTF-8 plain text
`Healthy`, `Degraded` or `Unhealthy`, case-insensitive after trimming whitespace,
with a maximum body size of 1 KiB. No custom JSON schema is assumed. A 2xx
response can carry any of these states; a 503 with `Unhealthy` is also valid.
Other status/body combinations are invalid, not Healthy. Transport failure,
timeout, invalid response and monitor failure are distinct
from an explicit application Unhealthy result. Store observation and receipt
times separately; the Worker supplies the observation time for pull checks.

## State and freshness

Each enabled target begins Unknown; pre-approval time is never inferred as
healthy. Initial failure and recovery thresholds are three consecutive failures
and two consecutive successes, respectively, configurable by an administrator.
Healthy counts as success; explicit Unhealthy counts as application failure;
transport failure, timeout, invalid response and monitor failure count as
observation failures with distinct reason categories.
Changing category resets the previous streak. Degraded takes effect immediately
and resets both streaks. Three consecutive application failures yield Unhealthy;
three observation failures yield Unknown with the relevant reason, not an
asserted application outage. Two consecutive Healthy observations yield Healthy
from any other state. Before a threshold is reached, retain the previous state
only while its evidence remains fresh. Every non-Healthy observation resets the
success streak; a failure category change restarts the failure count at one.

Freshness expires at the last observation that supports the current state plus
twice the configured interval and the configured timeout. Contradictory results,
timeouts and invalid responses cannot extend that state's evidence; in
particular alternating Unhealthy and timeout cannot keep Healthy indefinitely.
Independently, a pending streak expires after the same duration from its latest
observation, including when the current state is already Unknown. Before
processing a new observation, apply any crossed expiry boundary. Expiring the
state retains a still-fresh success or failure streak so the configured
threshold is honoured even when previous state evidence expires between checks.
An expired streak of either kind is reset. This also applies after process downtime, even if no expiry scanner
ran. At expiry the effective state becomes Unknown. Stopping or revoking
a source, retiring an instance, disabling a target or changing configuration
also invalidates old evidence and resets streaks. A later recovery must satisfy
the success threshold again. Persist the reason and interval boundary; do not
backdate missed checks as healthy. Validate bounds to prevent overflow.

Each state transition records its effective time, state, reason category and
source reference. Raw endpoint details and exception messages are not public.
Target configuration history includes explicit inactive intervals while a check
is disabled. An unexplained gap during a target's recorded lifetime is Unknown,
not evidence that the target was intentionally disabled.
Intervals are half-open `[start, end)` in UTC, non-overlapping for the same
subject across all policy versions, with at most one open interval. A policy
change atomically closes the old interval before opening the new one. Time is ordered by
observation, not UUID or database identity. Duplicate, concurrent, delayed and
out-of-order results must not create overlapping history or move an established
transition backwards. The processing policy and its SQL concurrency tests are
part of REQ-24-T03; a late observation cannot silently rewrite published history.

Service-level targets and instance-level targets are distinct. An instance is
identified by the existing approved service and source-scoped instance identity,
not by host IP. Aggregate only explicitly configured, active participants; do
not assume an unreported instance disappeared. No participants means Unknown.
All Healthy means Healthy; all Unhealthy means Unhealthy. A known Degraded
participant or a mixture of Healthy and Unhealthy means Degraded, with an
explicit unknown-participant count. Otherwise any Unknown participant makes
the aggregate Unknown. Healthy + Unhealthy is Degraded; Healthy + Unknown is
Unknown; Unhealthy + Unknown is Unknown; Healthy + Unhealthy + Unknown is
Degraded with one unknown. Maintenance is not produced until a real maintenance
policy and its aggregation rules are implemented; a missing sample is never
maintenance. Availability reserves the category for such a policy without
claiming it currently exists.

## History, availability and visibility

Persist state intervals independently of raw logs and traces. The initial
retention is 365 days; REQ-39 owns scheduled cleanup. Hourly and daily buckets
are derived from intervals, intersected with the requested period and expressed
with an explicit display timezone; DST days need not contain 24 hours. A bucket
may contain several states, so its display colour is not the uptime calculation.
Queries use bounded UTC windows and expose coverage and unknown duration.

Availability measures fully Healthy duration divided by Healthy + Degraded +
Unhealthy duration. Degraded reduces availability; Maintenance and Unknown are
excluded from that denominator but shown separately. Coverage is the denominator
divided by the whole requested duration. For 60 minutes containing 45 Healthy,
5 Degraded, 5 Unhealthy and 5 Unknown, availability is 45/55 = 81.82%, coverage
is 55/60 = 91.67%. A zero denominator yields Unknown; show coverage beside any
percentage. Service availability uses its own aggregate state intervals; do
not average instance percentages.

Group history uses only the distinct services visible to the requesting user and
belonging to that group at each instant. A service in multiple groups appears
once in each applicable group, never twice within one group. Combine its service
states by the same participant rules above, then apply the duration-based
availability and coverage formula to the resulting group state intervals; do not
average service percentages. For example, one Healthy and one Unhealthy service
make the group Degraded; Healthy and Unknown make it Unknown. A group with no
visible members is Unknown. A member joining or leaving changes the aggregate at
that event time. Public group calculation must first filter for actual public
visibility, so hidden members and their counts cannot affect public results.
This contract does not claim group history is implemented: persistent membership
and publication-effective history are required before this calculation can be
exposed and must be verified with the group and publication owners.

The authenticated history API accepts UTC instants (other offsets are converted
to UTC), with a maximum 366-day range and at most 20,000 stored target intervals
per request. An oversized request is rejected and must be retried with a shorter
window; results are not silently truncated. An open interval is clipped at its
evidence deadline or the earliest network policy revision even if the expiry
worker has not yet persisted that boundary. Configured target gaps are Unknown,
while explicitly disabled periods are excluded from participation. The response
returns UTC half-open intervals and fractional availability and coverage; it does
not expose target URLs, credentials or network allowlists.
History readers verify target, schedule and network-policy versions before and
after assembling a result and retry when they change. Future cleanup or
correction that changes a target's intervals must atomically advance that
target's schedule version in the same transaction, including REQ-39 retention
cleanup, so readers can detect a concurrent history change.
Cleanup must also retain each schedule's last evaluated observation and all
subsequent policy revisions needed to locate an unresolved policy-change
boundary, until the schedule has consumed that change. This protection and the
version update require integration testing with the real REQ-39 cleanup.

Publishing is independently authorised by REQ-12 and REQ-13. A private or
revoked resource is immediately excluded from anonymous output, including
cached output. Making a resource public does not automatically expose its
earlier private history. Anonymous responses use an allowlist of simple state,
time and approved public summary; they exclude IPs, endpoints, source identity,
credentials and diagnostic evidence. The publication event and history start
must be persisted by the publishing owner and verified end-to-end with REQ-14.
Authenticated history and evidence navigation belongs to REQ-25. Public and
authenticated history are not marked accepted until those real integrations
and accessible, non-colour-only interaction have been verified.

## References

- [ASP.NET Core Health Checks and publishers](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/health-checks?view=aspnetcore-10.0)
- [Microsoft.Extensions.Diagnostics.HealthChecks](https://www.nuget.org/packages/Microsoft.Extensions.Diagnostics.HealthChecks/)
- [Identifier and index policy](ADR-0001-identifiers-and-keys.md)
