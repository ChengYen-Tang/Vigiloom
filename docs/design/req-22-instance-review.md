# Service instance management: visual review

The existing Vigiloom blue and white direction was applied to the administrator's service instance and capacity page. This is a focused UX review against that selected product style, not a pixel comparison against a dedicated instance-page mockup.

- [Page and capacity form](req-22-instance-review/desktop.jpg): labels, current limits and service management are visible; telemetry and health are explicitly not represented as live data.
- [Service confirmation](req-22-instance-review/service-confirm.jpg) explains the effect before stopping collection. In an isolated test database, browser operations stopped and resumed the service and the page reported both outcomes.
- [390px layout](req-22-instance-review/mobile.jpg): service management and the instance table remain visible; the table offers horizontal scrolling. A previous instance-action check exposed an expanding column, which was fixed with fixed table layout and wrapping.
- A separate browser check retired and restored the same test instance; an HTTP test covers role restrictions and changing capacity after those operations.
- Screenshots use test identities and data. Screen-reader behavior and a complete accessibility audit were not verified by this review. No application telemetry was generated or validated.
