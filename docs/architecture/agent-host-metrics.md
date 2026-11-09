# Agent host metric semantics

The Agent reports missing values by omitting the point; it must not turn an unavailable or stale counter into zero. Each point records the Agent observation time and source. Counter rates use monotonic elapsed time and require consecutive valid samples of the same source. A reset, wrap, missing sample, zero interval, or stale interval invalidates the derived rate. Metric batches reject non-finite, negative, or greater-than-1e30 values; this generous upper bound also keeps database floating-point aggregation finite for any representable row count. A rejected batch is not partially stored.

## Units and platform meanings

| Measurement | Unit and meaning |
| --- | --- |
| Total and per-logical-CPU utilization | Percent over the observation interval; 100% is the full capacity of the reported CPU or aggregate. Linux logical CPUs use kernel `cpuN` labels; Windows uses processor-group and logical-processor indices. |
| Linux load averages | One-, five- and fifteen-minute kernel load averages; these are counts, not percentages and are not available on Windows. |
| Physical memory total and available | Bytes. Windows uses `GlobalMemoryStatusEx` physical totals and available physical memory. Linux uses `MemTotal` and `MemAvailable` from `/proc/meminfo`; `MemAvailable` is a kernel estimate and is not simply `MemFree`. The operating systems' available figures are not interchangeable definitions. |
| Linux swap and Windows pagefile | Bytes; Linux reports swap total and free; Windows reports pagefile total and used. They have different scopes and cannot be compared as the same counter. |
| Volume total and available | Bytes for a mount or Windows drive, not a physical disk. Available space is the amount available to the Agent's account and can differ from total free space because of quotas or reserved blocks. |
| Disk throughput and latency | Bytes per second and mean milliseconds per completed read or write operation; the reported dimensions and measurement layers are OS-specific. |
| Network traffic and errors | Received/sent bytes per second and received/sent errors per second, calculated from operating-system interface counters and keyed by interface identity. Counter availability and meaning depend on the interface and OS. |
| Process CPU and working set | Percent of one logical CPU and bytes of resident working set; the PID and start time together identify an observed process instance. |
| Collection unavailable | Value 1 identifies a named source that was unavailable or incomplete; absence of this marker does not prove source health. |

## Historical aggregates

The aggregate endpoint requires one device, one of its Agent identities, and an exact kind, source and optional dimension. It keeps re-registered Agent histories separate. Time ranges use `[from, until)`; bucket boundaries are UTC and anchored to the Unix epoch. Empty buckets are omitted, not represented as zero. Each populated bucket contains its last observation time, minimum, maximum, sample count and arithmetic sample average. The average is not time-weighted, and a rate cannot be summed as a byte total without accounting for time. Collection-unavailable markers remain available in raw history and are not numeric aggregates. Requests are limited to 31 days and at most 1,000 aligned buckets. Historical results can change when delayed samples arrive or retention removes data.

## Windows pagefile

`windows.pagefile` sums the actual installed pagefiles returned by the Windows `EnumPageFilesW` API. Total and in-use counts are converted from pages using the operating system page size. This is separate from `GlobalMemoryStatusEx`'s commit-limit fields, which include more than the pagefiles. Enumeration failure or invalid counters make the source unavailable; paths are not collected. Enumeration is limited to 256 pagefiles. [Microsoft API reference](https://learn.microsoft.com/windows/win32/api/psapi/nf-psapi-enumpagefilesw).

## Windows CPU

`windows.pdh.processor-information` reports processor-group and logical-processor instances as `group,index`; these are logical CPUs, not physical cores. It uses language-neutral PDH counter paths and requires two observations. An invalid, missing or stale counter produces no value. Changes in active processor counts reset the query and require warmup again. One query is owned by one collector and is closed when that collector is disposed. This has been tested on the current Windows host; other CPU topologies, processor-group configurations and x86 execution need separate validation.

## Linux block I/O

`linux.proc.diskstats` reads the Linux kernel block-device counters. Its dimension is `major:minor:name`, identifying the kernel device at observation time, not a permanent hardware identifier. A replaced device can reuse that identity; rates cannot reliably detect replacement if all counters continue increasing. Devices can be whole disks, partitions, loop devices, or virtual devices. The same I/O can appear at multiple layers; summing them does not give host throughput.

Read and write throughput use the difference in successfully transferred sectors multiplied by 512 bytes, divided by monotonic elapsed seconds. Read and write latency use the difference in total operation milliseconds divided by the difference in completed operations. These are averages for completed requests, not percentiles, and the kernel's millisecond resolution can produce zero. No completed operations means latency is unavailable. Source definitions: [Linux disk I/O statistics](https://docs.kernel.org/admin-guide/iostats.html) and [block layer statistics](https://docs.kernel.org/block/stat.html).

Collection is bounded to 8,192 input lines and 256 reported block devices. Exceeding the device limit marks the source incomplete; exceeding the input limit invalidates the sample. This limit is not an assertion that the first 256 devices represent every physical disk.

## Windows disk I/O

`windows.pdh.physicaldisk` reads per-instance `PhysicalDisk` counters for read/write throughput and average operation latency. These represent the measurement layer and interval described in the [Microsoft counter reference](https://learn.microsoft.com/windows-server/storage/storage-spaces/performance-history-for-drives), not a persistent hardware identity or an end-to-end application latency. No completed operations means that a latency average is unavailable, not a zero-latency observation. The first reading and readings after stale intervals or detected instance changes are warmup, not zero.

The reader discovers localized paths using the [documented PDH wildcard procedure](https://learn.microsoft.com/windows/win32/api/pdh/nf-pdh-pdhaddenglishcounterw), and refreshes the instance list every five minutes. It retains at most 256 instances and reports incomplete coverage when paths or counters fail. Counter access can depend on the service account's performance-monitor permissions. Current tests exercise x86 and x64 on one Windows host; other locales, service accounts and disk hotplug need separate validation.

## Windows network sampling observation

On 2026-11-10, a temporary test sampled `NetworkSampler`, downloaded a 32,149,718-byte archive from the Vigiloom GitHub repository over HTTPS, then sampled again. Four interfaces reported a positive receive rate, with no interfaces marked unavailable or truncated. This shows the sampler can observe live nonzero interface counters on that host; concurrent background traffic and multiple interfaces mean it does not establish byte-for-byte attribution or precision. The temporary test was removed afterward.

## Process resources

`system.process` uses the operating system process APIs. The dimension is PID plus UTC process start-time ticks; it identifies a process instance rather than a persistent application identity. Working set is resident physical memory as reported by the OS, not committed memory or exclusive memory. CPU is elapsed process CPU time divided by monotonic elapsed wall time, with 100% representing one logical CPU; a multithreaded process can exceed 100%. The first observation, a missing process, a reset or a stale interval has no CPU rate.

The operational starting defaults are 30 seconds for host metrics and 60 seconds for process snapshots. Administrators can change these through synchronized policy. The collector can skip process collection on a host-only pass; skipping it is not a source failure.

The sampler attempts to read the first 256 processes by PID. It marks the source incomplete when the list exceeds that limit or a process cannot be read, for example because it exited or the Agent lacks access. An administrator-configurable wall-clock budget, defaulting to two seconds, stops starting further process reads and reports `process_budget` separately when reached. It cannot preempt process enumeration or an individual synchronous OS call; already sampled entries are retained. Process enumeration itself is performed by the operating system API and can allocate a list larger than 256; the limit bounds detailed sampling and reported cardinality, not the initial enumeration cost. No claim is made that this selection includes the busiest processes. The time budget is not a byte-based I/O limit.

## Sample transport boundary

A sample is split into wire batches of at most 250 points and one MiB per batch. The publisher queues them in order, checking the Agent identity under the same lease as each queue transaction. Host and process observations are published separately: completed host observations are queued before process collection starts. If the queue is full, process collection remains due until the pending host batches drain. Full queues retain the unqueued batches for retry without changing their IDs or contents; a queue with capacity for only one batch can still progress as batches are delivered. Individual batches are durable after their SQLite transaction commits, but the entire sample is not atomic across batches or at the server.

Unqueued batches remain only in memory. Stopping or crashing the Agent can lose those points, and a producer blocked on a pending sample can miss later sampling intervals. Production scheduling must expose and account for these gaps rather than treating them as zero or claiming the entire sample is durable. The publisher must be called by one producer at a time and must never transfer pending points to a different Agent identity.

During orderly shutdown the sampler retries a full queue for up to five seconds, allowing a still-running sender to free space. The producer must be registered after the sender so the default sequential host shutdown stops it first. Any batches still pending are reported with their record count in the Agent log. Logs are not a durable loss ledger, and neither a hard crash nor an I/O failure is guaranteed to leave an exact loss count.

## Resource-policy boundary

The metric configuration controls host and process intervals, cooperative sampling CPU and I/O allowances, the Agent working-set threshold, and the sender's HTTP-body allowance. Queue capacity limits stored payload bytes and batch count; the sender limits concurrent requests and catch-up bursts. These settings are not hard operating-system resource quotas.

CPU and memory limits for a managed process must be described as cooperative thresholds unless an OS-level enforcement mechanism is actually configured. A synchronous operating-system API or SQLite call cannot be forcibly stopped by a cancellation token. Resource pressure should delay future sampling, and a missed sampling interval must remain distinguishable from a collection error or a discarded batch.

The metric worker uses a configurable CPU allowance, defaulting to three seconds of Agent process CPU time per 60-second window. It measures the process around each sampling cycle, so work by other Agent threads during that interval may also count; it pauses subsequent collection after the allowance is reached and still attempts to queue pending samples. A cycle crossing a window boundary is conservatively charged to the window in which it finishes. A single long-running cycle can exceed the allowance, and restarting the worker resets its in-memory window. This is a cooperative threshold, not a hard CPU cap; stored settings are not proof the worker applied them.

The same worker checks the Agent process working set against the administrator-adjustable threshold, defaulting to 512 MiB, before starting a new cycle. It still retries pending batches while sampling is delayed. Working set includes the whole process rather than only the sampler; the threshold cannot prevent a single collection or unrelated work from exceeding it. An allocator may retain memory, so the Agent can remain above the threshold until memory falls or an administrator raises it. The operational effect and complete memory budget still require live testing.

The worker also observes process I/O around sampling cycles and delays later collection after the administrator-adjustable allowance, defaulting to 64 MiB per minute. Linux `rchar`/`wchar` are logical read/write accounting and can include cached and non-disk I/O; Windows `GetProcessIoCounters` has its own process-I/O semantics. Both include I/O by other Agent threads while the cycle runs. They are not equivalent physical-disk byte counts. Counter reset is treated as an error rather than a negative delta; addition saturates on overflow. Pending batches are flushed before resource probes. If the post-cycle CPU or I/O observation fails, its budget is treated as unknown and new collection is delayed until the next window; successful observations are not reported as failed. As with CPU, this is a cooperative after-the-fact threshold, not a hard cap on an in-flight operation. Real workload effects require further validation.

The shared sender reserves HTTP body bytes in an identity-bound SQLite fixed-minute window before each request, including retries and compression fallback; the identity lease is released before network I/O. It reads the last-good Agent policy under the same lease for every reservation. The administrator-adjustable allowance defaults to 16 MiB per minute, with a minimum of 2 MiB per minute so a maximum-sized body and its compressed attempt fit in an empty window. A denied reservation preserves the queue and delays the next attempt. The reservation is retained after cancellation or failed transmission, so it can exceed the bytes actually sent. HTTP headers, TLS and TCP overhead are not counted. A fixed window can permit a short burst across its boundary; it is not a rolling-minute guarantee.

New senders using the same identity share the persisted allowance and a process restart does not reset it while the system monotonic clock remains continuous. A detected monotonic-clock reset or frequency change retains spent bytes for a new window. A machine reboot cannot always be recognized solely from timestamp values, and an older sender ignores this table, so neither reboot nor mixed-version enforcement is guaranteed. Lowering the allowance keeps already used bytes and queued data; an in-flight reservation is not revoked.

When a sampling source is unavailable or incomplete, the cycle emits a `CollectionUnavailable` point with the source name as its dimension and value `1`. This point follows the same bounded queue and delivery rules as other points; it becomes durable only after its batch is queued. An absent marker alone does not establish that a source was sampled or healthy. Markers do not count samples lost before durable enqueue or establish why a source was unavailable.

## Illustrative capacity measurement

On 2026-11-10, one Windows test host produced a warmed-up sample with 142 host points (28,385 serialized JSON bytes) and 286 process points (54,445 bytes). The process source reported incomplete coverage, and these figures exclude unavailable-source markers, HTTP headers, TLS, retransmission, compression, database indexes and storage overhead. The sample used a one-second warmup, not a sustained production run.

For illustration only, applying those sizes to the default 30-second host and 60-second process intervals yields about 160,149,600 uncompressed payload bytes and 820,800 points per day per similar host, or about 74.6 GiB/day of payload for 500 identical hosts.

In a separate disposable LocalDB database, 10,000 synthetic points for one Agent were written through EF with roughly equal process and CPU dimensions. SQL Server reported 1,832 KiB used for the clustered index and 632, 416 and 464 KiB for the three secondary indexes, about 342 bytes per point in this small sample. Applying that observed ratio to the preceding sample yields roughly 130.9 GiB/day of metric table and index pages for 500 similar hosts, before receipts, other tables, transaction logs, free or reserved space, backups and retention. This is an illustrative page-use estimate, not a capacity or throughput guarantee. Actual cardinality, permissions, agent count, index fragmentation, SQL version, growth patterns, retries and retention change the result; use sustained representative load and database measurements to size a deployment.
