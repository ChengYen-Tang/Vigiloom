# REQ-31 Server Details

The device detail extends the approved Vigiloom blue-and-white visual system. The Product Design exploration in `req-31-server-details-concept.png` is a visual study, not a source of device facts or a commitment to infer an overall health state or an application-to-host relationship.

Deep-monitoring data is grouped into inventory, performance, processes and OS services, host logs, and file integrity. Each view is loaded independently and uses bounded pages or snapshot chunks. Snapshot replacement asks the user to reload instead of combining generations. Graph series remain separate by Agent registration, source and dimension; missing samples do not become zero.

Ping, Agent status and application-service health remain independent. Source outcome, central acceptance, freshness and missing information are shown separately. Administrators manage integrity scopes from the authenticated device page; Read only users can inspect monitoring data without access to configuration.

The reference establishes visual language rather than fixed example content. Actual UI content is driven by available evidence and existing product navigation; future public projection, application health, vulnerability and security-baseline integrations are verified with their respective requirements.

The approved dashboard reference and an authenticated device screenshot were inspected together for the shared palette, typography, cards and navigation. They are different pages and data states, so this is a visual-language review, not a pixel-fidelity comparison. The device page was also inspected in the in-app browser at desktop and 390 px; the host tabs and horizontal overflow were checked, and the earlier white-on-white tab text was corrected. Browser tests cover interaction and role boundaries separately.

The performance tab keeps each Agent and source separate and presents a timestamped threshold evaluation independently from the historical chart. Administrators can configure the selected series; read-only users can inspect the result without a management link. Missing or expired samples remain unknown. The flow was checked in the in-app browser, and a 390 px screenshot of the revised controls confirms labels and selects remain grouped when wrapping. Automated browser tests cover Server and WebAssembly reads, threshold configuration, disabled and expired states, empty data, historical gaps, and the management boundary.
