# Agent collection loss and gaps

Status: REQ-19 design in progress. This document does not claim the reporting
path or its acceptance tests have been implemented.

## Meaning

An observation becomes a durable transport batch only when its local SQLite
transaction commits. A full queue, a retryable rejection, or a quarantined
batch does not by itself mean that data has been lost. A missed sampling
interval is a coverage gap, not a known count of lost observations. A source
that could not be read is unavailable or incomplete, not a zero measurement.

Count a confirmed lost record only when its producer can establish that the
record existed, is no longer recoverable, and has not already been counted.
If a process terminates while data is solely in memory, or a storage operation
has an ambiguous outcome, exact loss may be unknowable. Report a possible gap
or unverified completeness rather than an established loss or inferred count. Failure to write
local diagnostics must also produce an unknown result rather than silently
claiming completeness.

## Required implementation properties

- Bind local state and any reporting to the registered Agent identity and its
  protected state directory. An identity change must not attribute old data to
  the replacement identity.
- Account for the fact that one metric sample can be split into multiple
  batches, some committed and some pending. A retry or lost response must not
  count an already durable batch as lost.
- A durably identified producer loss event and its local accounting must be idempotent across
  retries, restarts and competing processes. A single remembered last event ID
  alone is insufficient when different events can interleave.
- Send an authenticated durable cumulative snapshot, not a delta that the
  central platform adds again on every heartbeat. Preserve the existing report
  session fencing and ordering; an absent, unsupported, stale or unreadable
  loss snapshot is unknown rather than a newly reported zero.
- Keep lifecycle boundaries explicit: pending data is retained until the
  producer actually abandons it; shutdown, identity change and failures must
  not reassign or erase queued batches. A hard crash cannot guarantee a count
  for observations that were never durably identified.
- Show authenticated users the count and its scope, freshness and any unknown
  portions. The statistics begin when the ledger is enabled and do not prove
  completeness before that point. Public Ping visibility does not publish this information.

## Local accounting approach

Only one metric producer may own a state directory at a time. Use an exclusive
process lease and a durable writer generation to fence stale work. Store a
fixed-size Agent-bound cumulative ledger in SQLite with an identifier, its
start time, confirmed lost batch and record counts, an unverified-completeness
flag, the active writer generation and the latest settled event sequence and
its batch/record counts.
Each deliberate abandonment uses a stable sequence; check the generation and
sequence and increment both counts in one transaction. A retry of the same
settled sequence is idempotent; a gap or a conflicting replay is an error.
Do not retain an unbounded event history or additional telemetry payloads.

Mark the writer active before sampling. Once the lease is obtained, finding a
previous active writer marks completeness unverified; it does not establish
that an observation was lost. Clear the active marker only when known pending
work is settled. An ambiguous queue commit must be reconciled against its
batch ID before an exact loss count is asserted. If the local database cannot
be read or updated, the next report must not imply complete accounting.
An absent batch ID after an ambiguous enqueue is not proof of loss: the sender
may already have received an acknowledgement and removed it. If the outcome
cannot be proven, retain the unverified-completeness state.

The worker retains exclusive producer ownership and continues metric collection
when the identity-bound ledger cannot be opened or validated. Accounting is then
unavailable, not zero; the worker retries periodically, and a recovered ledger
marks completeness unverified before it can be reported again. It does not
overwrite damaged or differently owned state. The current
shutdown path allows up to five seconds for pending queue flush and a separate
five seconds for accounting; neither timeout can preempt synchronous OS or
SQLite calls. An old identity's ledger cannot be transferred to a replacement
Agent, and a revoked identity may be unable to report its final snapshot.

## Acceptance

Verify with the real metric collector and queue that backpressure and retained
quarantine do not increment loss, a partially committed sample counts only
actually abandoned records, retries and reordered heartbeats do not inflate a
count, and identity changes cannot misattribute it. Exercise cancellation,
process restart, ambiguous storage outcomes and offline recovery; demonstrate
that an unknown count is shown as unknown. The separate host-log source and
update/restore integrations still require their owning requirements.
