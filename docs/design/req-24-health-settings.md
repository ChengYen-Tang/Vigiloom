# REQ-24 health-check settings

Design direction: extend the selected Vigiloom blue-and-white system using the
existing [service discovery concept](req-22-service-discovery-concept.png) and
the established service-instance management layout. This is a service setting,
not a device Ping or Collector-health screen.

An Administrator chooses an approved service by its displayed name and
environment. The screen shows its configured HTTP checks, their enabled state,
and the latest actual observation with its timestamp; absence, expired data,
blocked access and monitor errors must not be labelled healthy. A separate
compact form creates or edits a check, with the service-level/instance choice,
endpoint, interval, timeout and failure/recovery thresholds. Defaults are
30 seconds, 5 seconds, 3 failures and 2 successes. Configuration is versioned;
conflict and validation messages explain how to recover. The URL is visible only
to administrators and must not contain credentials; it is excluded from audit.

An administrator can manage allowed destination networks on the same screen.
An empty list is a clear, actionable blocked state, not an apparently running
check. CIDR examples, input errors, the current revision and the effect of
changing the list are visible. Saving a target or network policy is distinct
from a successful probe. Show timestamps and textual outcomes, not colour alone.

Use existing service-management table, labels, spacing and responsive form
patterns. Group each label with its input at narrow widths; avoid requiring
horizontal scrolling for forms. Existing shared navigation and access controls
remain authoritative. Public history and authenticated evidence views are
owned by REQ-14 and REQ-25, respectively, and are not represented as complete
by this settings screen. Visual and accessibility validation is still required.
