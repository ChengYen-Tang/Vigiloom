# Security intelligence sources

Status: REQ-32-T01 source and consumer contract under investigation, 2026-11-14.
This document does not mean a provider has been implemented or enabled. A
public endpoint or an open-source client is not, by itself, permission to
redistribute its data. Record the applicable terms for each source and data
subset before publication; leave unresolved sources unavailable for publication.

## Source inventory

| Source and evidence | Purpose and representation | Update and provenance | Publication decision |
| --- | --- | --- | --- |
| [NVD CVE API](https://nvd.nist.gov/developers/vulnerabilities) and [CPE API](https://nvd.nist.gov/developers/products) | CVE records and product matching metadata. Neither an NVD record nor a CPE string proves that a particular installed package is affected. | API 2.0 results are paginated; preserve the source, query window, upstream modification time and schema version. API key handling, limits and updated records need an explicit synchronization contract. | Candidate. The official developer page was not readable in this environment; terms, current rate limits and redistribution conditions are not yet verified. Do not publish. |
| [OSV data sources](https://google.github.io/osv.dev/data/), [OSV schema](https://ossf.github.io/osv-schema/) and [API](https://google.github.io/osv.dev/api/) | Affected open-source packages, ecosystems and version or commit ranges. Records originate in multiple databases and cannot be treated as one uniform license. | API lookup and bulk ecosystem exports; preserve the original record ID, `modified`, `withdrawn`, upstream source and schema. Do not infer that a missing record means a package is safe. | Candidate by individual upstream and subset. The official source list includes different licenses, including CC-BY-4.0 and CC-BY-SA-4.0. Attribution, transformations and redistribution require validation per subset before publication. |
| [CISA KEV data and license](https://github.com/cisagov/kev-data/blob/a795af4c4afc47df12906b4c04e656156cf06493/LICENSE), [JSON schema](https://github.com/cisagov/kev-data/blob/a795af4c4afc47df12906b4c04e656156cf06493/known_exploited_vulnerabilities_schema.json) | A catalog of known exploited vulnerabilities, not a full vulnerability catalog or proof that a local asset is affected. | JSON snapshot includes `catalogVersion`, `dateReleased`, count and CVE records. Validate the complete snapshot and preserve the source version, date, retrieval time and content digest. | CISA's repository identifies the KEV database as CC0-1.0. Third-party links retain their own terms and CISA branding or endorsement must not be implied. Actual publication also requires source authenticity, format validation and the release controls below. |
| [FIRST EPSS data](https://www.first.org/epss/data) and [FAQ](https://www.first.org/epss/faq) | Daily probability estimate and percentile by CVE. EPSS does not prove local applicability or exploitation and is not a complete risk score. | Prefer the official daily compressed CSV for bulk synchronization; the API is intended for lookups. Preserve score date and model information where supplied. | Candidate. FIRST says scores are freely available and requests attribution for use in products; the complete redistribution and attribution conditions for the intended artifact still need confirmation. Do not publish pending that confirmation. |
| [Microsoft Security Updates API](https://github.com/microsoft/MSRC-Microsoft-Security-Updates-API) and [OpenAPI](https://github.com/microsoft/MSRC-Microsoft-Security-Updates-API/blob/main/docs/swagger.yaml) | Microsoft vendor advisories, product applicability and security updates in CVRF. Platform version, product identifiers and installed update evidence are still required for a finding. | List update summaries and retain source document ID, dates and the received document/version. | Candidate. The sample repository's software license must not be assumed to license the advisory data. Verify data terms, rate limits and redistribution before publication. |
| [Red Hat Security Data API](https://docs.redhat.com/en/documentation/red_hat_security_data_api/1.0/epub/red_hat_security_data_api/csaf) | Vendor CSAF, CVE and OVAL data; relevant for supported Red Hat products, not every Linux distribution. The provider documents CVRF and OVAL v1 as deprecated. | Preserve advisory IDs, version, source time and format; use vendor-supported formats and handle revisions. | Candidate. Verify the particular data and redistribution terms and applicable platform/version before publication. |
| [SigmaHQ rules at reviewed commit](https://github.com/SigmaHQ/sigma/tree/54967d64e6dda2b4daa81f47f7a8bf1f591b68a0), [DRL-1.1](https://github.com/SigmaHQ/Detection-Rule-License/blob/bcbdc605172d00390572cd774ba395ec9e975dfc/LICENSE.Detection.Rules.md) | Behavioral detection rules in Sigma format; the Sigma specification and conversion engine are not the rule data. Rules require compatible event sources and field mappings. | Pin the upstream commit or release and individual rule identity, content hash, source and metadata. A revised or removed rule is a version change, not evidence of a clean host. | Candidate per rule and subset. Sharing rules requires the specified attribution, rule link and license text or link where reasonably practicable; messages based on matches require the specified author attribution. Verify any rule-specific license and support the attribution in the downstream detection and evidence UI before publishing. |
| [Signature-Base at reviewed commit](https://github.com/Neo23x0/signature-base/tree/eb8c53cb08ed103d031fe9c98d1d23e94904839d) | Candidate YARA rule files and separate IOC CSV files. YARA is a rule format and engine; it does not itself supply this dataset. Source metadata warns that some rules use scanner-specific external variables. | A commit identifies the source snapshot; retain individual file/rule IDs, author, license metadata and content digest. A changed commit requires revalidation rather than automatic trust. | Candidate only. Upstream states DRL-1.1 applies to its signatures and IOC files except YARA rules with a different explicit license. Resolve each rule/subset, attribution and external-variable compatibility before selecting or publishing it. |

IOC feeds, YARA rules and security configuration baselines each need their own
provider, license and capability record. Their names do not designate a single
authoritative feed; no source is enabled merely because the corresponding data
type exists. Wazuh CTI and historical independent Wazuh rulesets are candidates,
not an assumed unrestricted vulnerability database.

## Provenance and release contract

Each independently managed source/subset has a stable identity, data category,
owner, official source URL, applicable terms and license evidence, retrieval
and redistribution permissions, attribution obligations, format and supported
schema, platform/ecosystem coverage, and a policy for updates, expiry and
withdrawal. Unknown permission or unsupported schema is an explicit unavailable
state. Per-record origin and license must survive normalization and any merge
of multiple providers. Credentials must not be part of ordinary reads, logs,
release artifacts or error messages.

Central Worker updates must use a bounded schedule, request limits, cancellation,
size and decompression limits, and authenticated TLS. Store the upstream
revision, retrieval time, source timestamps, digest and transformation version;
distinguish upstream-provided signatures from a signature made by Vigiloom.
Fetch and validation happen before atomic activation. Incomplete, malformed,
stale or unlicensed input cannot replace the last valid version. Expose failure,
last successful update, expiry and unsupported coverage in the administrator UI.
Retain version references used by security evidence and allow an authorized
rollback without erasing the audit history. A retained revision is not
necessarily eligible to be reactivated: revoked authorization, an upstream
withdrawal or a security revocation must prevent its republication until the
specific source policy permits it. Evidence references and audit metadata may
need to remain even when a revision can no longer be distributed. A removed or withdrawn upstream
record must be represented explicitly; it must not silently become a claim that
the affected asset is safe.

REQ-33 through REQ-36 own interpretation and applicability. REQ-32 supplies
versioned data and provenance; it does not claim a detection, vulnerability,
baseline or IOC match merely because a source was imported. End-to-end
publication, rollback and attribution remain unverified until each consumer is
implemented and exercises those contracts.

## First source contract: CISA KEV

KEV is the first source to implement, not the full scope of REQ-32. The reviewed
upstream schema and license are pinned above; the running service must fetch
the current catalog from the [official CISA JSON feed](https://www.cisa.gov/sites/default/files/feeds/known_exploited_vulnerabilities.json)
and record the actual retrieved catalog version, digest and validator schema
revision. The catalog does not itself declare its schema revision. A release using a different schema revision must be
validated explicitly before it can be activated. The catalog is a complete
snapshot, not an incremental stream.

The normalizer retains the original CVE ID, vendor/project, product, name,
date added, description, required action, due date, and, when present, ransomware-use status,
notes and any recognized current optional fields alongside the immutable source
revision. Preserve unknown fields in the bounded original artifact; a consumer
must not mistake an absent optional field or `Unknown` ransomware status for a
negative result. Consumers identify records by source and CVE, and query an
explicit activated version. Vendor/product strings are source descriptions,
not a verified mapping to a Vigiloom inventory asset.

The importer validates UTF-8, JSON and the pinned schema, declared `count`,
unique CVE IDs, required fields and timestamps before a version is eligible for
activation. It rejects content above the configured compressed and decompressed
limits, excessive record counts, duplicate IDs and unsupported schema changes;
it must never download third-party URLs embedded in an advisory. The initial
engineering limits are 16 MiB each for compressed and decompressed input and
50,000 records, monitored and
adjustable by an administrator without bypassing schema or authorization checks.
The limits are safeguards, not a claim about future upstream catalog size.

Poll centrally once per day with a 60-second overall timeout and conditional requests
when the upstream supports them. An upstream `304` is not a new version; record
the successful check separately from the data's release date. Preserve the
previous active snapshot when retrieval, decompression, validation or storage
fails. Detect duplicate bytes by digest, but record the upstream version and
dates rather than using the digest as a substitute for provenance. A changed
snapshot produces a separate immutable staged revision; activation atomically
changes the selected revision and records actor or scheduled operation, time,
source/license evidence and validation outcome. Rollback selects a previously
validated retained revision; it must not rewrite source history or discard a
revision referenced by investigation evidence. The user-facing health state
must distinguish last check, last successful update, upstream release time,
validation failure and age; the initial staleness warning is 72 hours since a
successful source check. Administrator settings may override the interval and
warning without treating old data as current.

The KEV catalog includes external links and references. Their existence in a
CC0 catalog does not grant permission to retrieve or redistribute the linked
content. No CISA/DHS branding or endorsement is implied. Automated validation
must include a real bounded sample and failure cases for truncated, malformed,
duplicate, unsupported and over-limit data, together with atomic activation,
rollback and concurrent update behavior. REQ-34 is responsible for applying
KEV to an independently established asset/CVE relationship; its acceptance and
the signed publication path are not established by this source contract alone.

## OSV and rule-consumer interoperability

The [OSV 1.9.2 format](https://ossf.github.io/osv-schema/) and its
[reviewed schema revision](https://github.com/ossf/osv-schema/blob/972b058557d91ee36d92306fd5f986947b1b3c30/validation/schema.json) include a record ID,
`modified`, optional `withdrawn`, aliases, affected package identity and
version/commit ranges. Preserve these independently; aliases are relationships,
not permission to discard the original record or merge conflicting provider
claims. A package range requires its ecosystem's comparison semantics and an
identified package/version. An unsupported range, missing inventory or an
unrecognized ecosystem is unknown, not an unaffected verdict.
`SEMVER`, `ECOSYSTEM` and `GIT` ranges are distinct: a `GIT` range requires
the specified repository and commit ancestry, not just package-version order.

[OSV exports](https://google.github.io/osv.dev/data/) include withdrawn records
and provide per-ecosystem snapshots and `modified_id.csv` indexes. Incremental
sync must handle records with the same modified timestamp without skipping any,
checkpoint only after all referenced records and their provenance are durable,
and reconcile deletions or inaccessible records explicitly. OSV's documented
handling of deleted upstream records varies by source; an orphaned or missing
record cannot automatically be treated as withdrawn or safe. Restrict imports
and publication by the actual upstream subset and license; ecosystem membership
alone does not prove a record's origin or redistribution permission. The
GitHub Advisory Database [documents CC-BY-4.0](https://github.com/github/advisory-database/blob/09ff71573b6748a21637a08ffd9047b8a7da00cc/README.md#license)
for its repository; retain the required attribution, source and license links,
and upstream-provided notices for any selected GitHub subset, and indicate
changes when transforming it.
Pin the upstream snapshot and schema revision used by an implemented importer.
The first authorized subset, index consistency strategy and resource limits
still need to be finalized before that importer can be enabled.

For Sigma, pin a concrete release or commit and preserve rule ID, author,
license, source, status, log source, detection expression and the conversion
version in each staged revision. Validate each rule and its dependency closure
before it can be published. A rule using unsupported syntax, required fields,
correlation, log source or backend semantics must be marked unsupported; do
not silently weaken it or declare that it found no threat. Upstream separates
core, emerging-threat and threat-hunting rules, and explicitly states that
[threat-hunting rules are not for alerting](https://github.com/SigmaHQ/sigma/blob/master/Releases.md).
REQ-33 must decide supported semantics and preserve author attribution in
match messages; REQ-32 must carry the source metadata and avoid activating a
candidate until that contract has been verified end to end. Previous revisions
remain subject to the authorization and withdrawal checks above.
