# Agent platform and collector capability contract

REQ-17-T01 establishes the platform and capability rules consumed by the Agent requirements. This document is a design input, not evidence that an Agent, installer, collector, transport or updater is complete. ADR-0001, ADR-0002, ADR-0003 and the API and ingestion contract remain authoritative. REQ-17 stays open until the actual combinations and integrations are verified.

## Runtime eligibility and product support

The Agent targets .NET 10. The current product architecture scope is Windows x64, Windows x86 where the actual operating system and process combination meets Microsoft's support conditions, and Linux x64. Linux x86 and Windows Server 2003, 2008 and 2008 R2 are excluded from Agent distribution; agentless Ping is independent of this restriction. Other architectures are not implicitly included just because .NET supports them.

Microsoft's [current .NET 10 supported OS table](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md), including its notes and linked lifecycle requirements, is the source of truth for OS versions, architectures and dependencies. The [snapshot consulted on 2026-11-02](https://github.com/dotnet/core/blob/44927bc821d37f596e317c4316335632bd38c79b/release-notes/10.0/supported-os.md) records the basis of this decision; it is not a permanent allowlist. Requirements may change with operating-system lifecycle, servicing and .NET releases. In particular, distinguish a supported x86 process on a 64-bit Windows installation from a supported native 32-bit operating system; one does not establish the other. Server editions and extended security update conditions must be checked against Microsoft's table, not inferred from a client edition.

Runtime eligibility is necessary but insufficient for a Vigiloom support claim. Self-contained publishing, compilation, an installed runtime or a passing unit test does not prove installation or a collector works. Native APIs, kernel, libc, packages, filesystem policy, service identity and granted privileges can differ within an eligible OS. Agent installation, update and download experiences must explain incompatibilities instead of offering an incompatible package or suggesting it works.

No OS/architecture combination or collector is currently declared product-supported by this document. Before publishing a claim, record the exact OS edition, version and updates; OS and process architecture; Linux distribution, kernel and libc where applicable; runtime and Agent/package versions; native dependencies; service account and effective permissions. Verify installation, startup, each claimed collector, transmission, disconnect/recovery, update, rollback and removal on the actual target or a representative VM. Record dates, test procedure and evidence, including any limitations. Revalidate on material changes and when the underlying support conditions expire. REQ-20 and REQ-40 own their installation/update and release evidence; collector requirements own their native functionality. A single successful combination does not establish support for an entire OS family.

## Collector capability observations

Collector modules report their own capability independently of host Ping, Agent connectivity and other collectors. REQ-19 owns transmission and reception, and each collector owns its signal, prerequisites, measurement time and required validation. This section defines semantics for those implementations; it does not add an unused wire DTO or claim a receiver exists. Protocol fields and compatible evolution must follow the versioned first-party batch contract in `api-and-ingestion-contract.md`.

Each observation identifies the collector/capability and reports one of:

| State | Meaning |
| --- | --- |
| `Supported` | The configured capability is available with the current environment and privileges. It is not a claim that a measurement exists or is healthy. |
| `Unsupported` | The platform or required native capability cannot provide this collector under the applicable product implementation. |
| `Disabled` | Configuration disables the collector. |
| `PermissionDenied` | The collector is applicable but the current identity or access policy denies a required operation. |
| `Error` | The capability could not be established or collection failed for another reason; do not convert uncertainty into `Unsupported`. |

An observation includes its UTC observation time, the last successful collection time if one exists, and a bounded, stable reason code when applicable. A sanitized diagnostic can help an administrator remediate failures; it must not expose credentials, raw sensitive telemetry or arbitrary exception details. No successful collection is represented by an absent last-success time, never a manufactured zero or healthy value. A collector may supply additional per-signal sample timestamps and errors under its owning contract.

The receiver derives Agent identity from its authenticated principal, not the observation payload; the existing UUID, timestamp, batch identity, durable retry and authorization policies continue to apply. The central platform distinguishes observation time, receipt time and staleness. A previously `Supported` observation does not imply present availability after the Agent goes offline, and absence of a report does not itself mean `Unsupported`. Treat future timestamps and clock skew according to the ingestion contract. Configuration changes, permission changes and recovery produce a new observation rather than silently rewriting historical evidence.

`Supported` in an individual runtime observation is different from a published product support claim: the former describes a reported capability in the current environment; the latter requires the documented end-to-end evidence above. Neither is a substitute for successfully collecting a particular sample.

## Installation, identity and privilege boundaries

Installation, service registration and replacing protected program files may need elevated privileges. That does not make elevation a requirement for every collector or justify running the Agent as administrator/root by default. REQ-20 owns installation and the separate updater; it must preserve the distinction between setup/update privileges and the Agent's normal runtime identity. Each collector requirement documents and verifies the least effective access necessary on each target OS, including restricted accounts and denial behavior. OS configuration may change access, so an untested permission must not be presented as universally sufficient.

Do not automatically elevate the process, broaden permissions for all collectors when one is denied, or treat partial access as a complete snapshot. Surface denied or partial collection with timestamps and reason codes. Sensitive detail stays in authenticated, authorized monitoring paths; public projections remain independently controlled. Updates, collection and reporting must observe cancellation and use bounded concurrency and buffers.

## Delivery ownership

REQ-17 owns this shared platform/capability policy and the final verified compatibility matrix. REQ-18 owns registration and machine identity, REQ-19 owns authenticated transport and the concrete versioned signal contract, REQ-20/21 own installation/update and packages, and individual collection requirements own platform-specific implementations. REQ-40 aggregates release and deployment validation. These boundaries avoid duplicating identity, transmission or native collectors in REQ-17.
