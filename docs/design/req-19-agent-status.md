# REQ-19 Agent connectivity on the device page

Extend the existing device detail screen using the [selected visual system](visual-system.md), rather than adding a separate management screen. Show Agent state and its last server receipt time as two labelled values alongside, but distinct from, Ping. The explanatory text identifies the current conservative offline threshold and says the page must be refreshed for updated status. Do not infer the device is unreachable from an Agent timeout.

The display distinguishes disabled monitoring, no current registration, waiting for the first heartbeat, online, offline, and an uncertain server clock. Only deep-monitoring devices show the Agent fields. Read only can see the non-secret status but not the Administrator's Agent identity management link. Anonymous users and Agent machine credentials cannot read the monitoring API; authorized monitoring API credentials can. Status responses and the detail page are marked `no-store`.

The existing overview image is the agreed style reference, not a same-screen mockup. An isolated LocalDB and test-only accounts were used to inspect the actual rendered device page in the in-app browser at desktop and 390 × 844 CSS pixels. The test showed Ping without samples alongside an online Agent; after 90 seconds and a page reload the Agent changed to offline without changing Ping. At the narrow width the document did not overflow horizontally and no browser warning or error was observed. The test process completed and cleaned up its database.

This is preliminary visual verification, not full REQ-19 acceptance. The selected overview is a style reference, not a same-screen design target; exact page-fidelity comparison against it would be misleading. The device and settings screens still need validation of their own states, interaction and accessibility. Real-time updates and reliable data delivery remain incomplete.

## Agent delivery snapshot

Extend the same device details page with a separate Agent delivery panel, using the existing card, labelled values and alert styling. Read only and administrators see the same non-secret snapshot. Show pending batches separately from retained quarantined batches, logical bytes and batch capacities, and the server receipt time. A missing report and a failed local queue read have distinct messages; never show unknown as zero. A full queue is called out without claiming data was lost. Every snapshot is labelled as last reported; show an explicit stale or clock-uncertain warning when the server receipt time is in the future or at least the conservative offline threshold old, even if empty heartbeats keep the Agent online. This panel is observational; it does not add an action to discard or alter queued data.

## Confirmed metric loss

Use the same labelled card on the authenticated device detail page. Show the
confirmed count as a minimum, identify it as metric records rather than all
Agent data, and show both ledger start and last report time in UTC. Distinguish
an unavailable snapshot from zero, and warn when the report is stale or its
time is uncertain. Explain that incomplete accounting means other loss may be
unknown, and that queue backpressure and retained quarantine are not loss.
Both signed-in roles may read this non-secret information; public views must
not expose it. This design supplements the existing selected visual system and
does not assert that the screen has already passed visual or accessibility QA.

An isolated test site with a synthetic seven-record loss snapshot was viewed
in the in-app browser at desktop width and 390 px. The count, timestamps and
incomplete-accounting notice were visible on both layouts. This is a visual
smoke check; it does not establish screen-reader or complete keyboard coverage.
