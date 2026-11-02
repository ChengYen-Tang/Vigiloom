# REQ-09 design QA

- Source visual truth: `docs/design/Vigiloom-UI-Concept-2-Revision-2.png` (1484 × 1060).
- Implementation: `docs/design/evidence/req-09/{settings,device}-desktop.png` (1440 × 1000), `{settings,device}-mobile.png` (390 CSS px wide, full-page capture, DPR 1).
- Full-view comparison: `docs/design/evidence/req-09/comparison.png`. Source and desktop captures are normalized to 700 px width in the same input. Full-resolution captures were inspected for readable labels, controls and table cells; a further crop was unnecessary.
- State: signed-in Administrator on an isolated test database; actual loopback Ping samples, not fabricated chart data. Source is an overview concept, while these are settings/detail screens: compare the shared design language rather than unrelated overview content or identical region proportions.

## Comparison history

1. Initial capture: P2 — the inherited device pages used a white page background, weakening separation between white cards and the shell; Ping states also lacked the existing semantic badge treatment. Fixed the device-page background to the existing light-blue token and reused the shared MudBlazor status badge. No new component package or visual language.
2. Revised capture: repeated in the in-app browser at the same desktop size and at 390 × 844. Cards, settings fields, latest-result table, status badges and mobile scrolling were checked against the source and the first capture. No actionable P0/P1/P2 findings remain.

## Required surfaces

- Typography: system UI/Traditional Chinese fallbacks, dark navy headings and muted supporting copy follow the shared application styles. Settings labels are explicitly associated with inputs; comfortable input sizes intentionally differ from dense overview rows.
- Spacing/layout: existing 8 px-radius cards, 24 px padding and consistent spacing. Settings use the existing narrower form width; details retain the existing device-page width. Mobile forms fit; the wide result table scrolls within its own container. Existing primary navigation scrolls within its own strip without hiding the settings control.
- Colors/tokens: existing blue actions, pale blue background, white cards and borders; readable green/red/neutral/warning badges supplement explicit status text. Color is not the sole state indicator.
- Images/assets: no new decorative imagery or substitute icons; the feature is composed of existing application controls, tables and shared badges. No fake historical graph.
- Product copy: distinguishes Ping from Agent/application health, current reachability from the last attempt, thresholds from one-off failure, unknown/stale from success, and missing latency from a real 0 ms loopback result. UTC labels match persisted data.

## Behavior and evidence

- In-app browser: signed in, saved settings with visible confirmation, opened actual per-target results, checked desktop/mobile captures and console (no warning/error entries).
- MSTest/Chromium: invalid timeout versus interval, successful save, mobile controls/no document overflow, and actual loopback result rendering.
- HTTP/SQL tests: anonymous/Read only write denial, CSRF, optimistic concurrency, persisted settings, current policy display, and readable latest results without management controls for Read only.
- Disposable preview and test databases use the ownership fixture and verified cleanup; no development database is used.

final result: passed

# REQ-19 preliminary design QA

- Source: `docs/design/Vigiloom-UI-Concept-2-Revision-2.png` (1484 × 1060), an overview concept used as a shared style reference rather than a device-detail mockup.
- Implementation: the actual device detail page, inspected in the in-app browser on an isolated LocalDB/Kestrel test site at a 1440 × 1000 desktop viewport and at 390 × 844 CSS px. Online and, after elapsed time and refresh, offline Agent states were observed separately from Ping. At 390 px the document width did not exceed the viewport; browser error and warning logs were empty.
- Source and implementation were opened separately. A saved, normalized joint comparison input, exact screenshot pixel/density metadata and focused comparison have not yet been produced. Keyboard/accessibility testing also remains outstanding. The overview reference and the device-detail implementation show different screens, so any eventual comparison must evaluate their shared design language, not pretend they have identical content or layout.
- No P0/P1/P2 visual finding was established from this preliminary inspection. This does not constitute the required comparative QA; typography, spacing, colors, assets and copy still require a recorded comparison.

final result: blocked

# REQ-38 preliminary design QA

- Source: `docs/design/req-38-audit-concept.png`, generated from the selected Vigiloom style. Its sample content is illustrative; the current slice only records speed-settings changes and retains the existing navigation.
- Implementation: the actual Administrator audit page on an isolated LocalDB/Kestrel test site, viewed in the in-app browser at its desktop size and at 390 × 844 CSS px. The date-range error and a real saved test setting change were observed. The narrow table can scroll horizontally; a labelled keyboard-focusable region and explanatory text were subsequently added and still require visual and keyboard verification.
- The images have not yet been saved and normalized into a joint comparison input. The in-app browser rejected the local data-URL comparison under its URL security policy; no alternative route around that restriction was attempted. Focused visual comparison, keyboard/accessibility testing and console review remain outstanding; this preliminary inspection is not completed design QA.

final result: blocked
