# Application metric rollups

Status: design in progress; no five-minute rollup is currently produced or retained by this document.

REQ-23 owns the application metric calculation and its results; REQ-39 owns retention policy, cleanup coordination and deletion. OpenTelemetry resource, scope, metric and point attributes identify a series. `MetricContext.RowId` identifies one received context, not a durable series. Source and service authorization, instance, schema URLs, metric name, unit, type, temporality and Sum monotonicity must remain distinct. Attribute identity must be deterministic and equality-checked; a fingerprint alone is not proof of equality. The original protobuf timestamps retain nanoseconds even though SQL timestamps have 100-nanosecond precision. A timestamp or value is not a general-purpose OTLP deduplication key.

Five-minute windows use UTC, with start `floor(unixNanoseconds / 300_000_000_000) * 300_000_000_000` and a half-open `[start, start + 5 minutes)` boundary. Results must preserve actual observation times, record type and quality; empty, missing, unsupported, overlapping, invalid and incomplete data must not become zero or normal. An absent optional field, including histogram sum, min and max, is distinct from a numeric zero. Resource and point attributes can contain sensitive information, so results retain the authorization of raw application telemetry and never enter anonymous projections or diagnostic logs.

Aggregation must follow the [OpenTelemetry metrics data model](https://opentelemetry.io/docs/specs/otel/metrics/data-model/):

- Gauge is a sample: a latest observation and explicitly sample-based statistics do not imply a time-weighted average.
- Delta sums and histograms describe intervals. Merge only compatible, non-overlapping intervals; retain uncertainty where intervals cross a window or have unknown boundaries. Do not proportionally invent a value.
- Cumulative sums and histograms must not be added as independent increments. Preserve reset epochs and boundary evidence, and do not infer a complete delta when the needed boundary is absent.
- Histogram bounds and exponential-histogram scale and bucket mapping must be compatible before merging; count, sum, min and max retain their optional and temporal semantics.
- Summary quantiles cannot be averaged or summed. Preserve the relevant snapshot and identify it as such.
- Unknown temporality, conflicting writers, malformed points, unsupported types, overflow, non-finite values and `NoRecordedValue` must not silently become valid values. Preserve enough evidence or an explicit quality state for consumers and cleanup to make a safe decision.

OTLP delta intervals are `(start, end]`: an interval ending exactly at a five-minute boundary belongs to the window ending at that boundary, rather than the following window. A point sample such as a gauge at the boundary belongs to the following half-open window. Retain the original nanosecond endpoints; an interval crossing the window cannot be split without source evidence.

OTLP retries have no universal event identifier: identical point contents may be legitimate observations. Rollup processing must not discard raw data simply because its payload matches another. Overlapping delta intervals must not be added twice; identical intervals or conflicting values make the derived result ambiguous unless a trustworthy source-specific identity resolves them. Preserve the inputs and report that quality state. Same-time conflicting values must not become an apparently complete result merely by selecting the largest database row ID. Reprocessing after a failure must atomically replace the computed result for the same source range, rather than adding the same input again.

## Late data and retention boundary

The current receiver accepts historical points after service approval and limits only future clock skew. A completed five-minute window is therefore not permanently closed. A late accepted point must make its affected result eligible for recomputation. The result commit must compare an input watermark or dirty generation with the one used for calculation, so an older calculation cannot overwrite a newer result; cleanup may use only a committed result that still covers the current generation. Concurrent ingestion, recomputation and cleanup need a transactional fence: REQ-39 must not delete raw inputs until the corresponding committed result includes them, and an ingestion racing that deletion must either be incorporated safely or receive an explicit policy outcome. The eventual treatment of samples older than already-deleted raw history must be defined and enforced jointly with REQ-39; silently accepting one into an apparently complete old rollup is not acceptable.

Computation, series cardinality, serialized payloads and query pages require explicit bounds. A capacity or compatibility limit must fail safely or produce a visible incomplete result; it must not silently discard raw inputs or permit their cleanup. Retention settings and the 365-day policy are not implemented until REQ-39 supplies and validates its scheduling, deletion and user-visible range.
