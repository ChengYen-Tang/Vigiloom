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

## REQ-23 pipeline reception

- Source: `docs/design/req-23-pipeline-concept.png` (1487 × 1058 pixels); implementation: `docs/design/evidence/req-23/template/desktop-contrast.png` (1440 × 1339 pixels, full page, 1440 × 1024 CSS viewport, device scale 1). Both show the Administrator reception overview with synthetic sources. Source names/order, example URL and navigation differ; those placeholders are not product requirements.
- Compared source and implementation in one normalized input at `docs/design/evidence/req-23/template/comparison-contrast.png`, with a focused table and guidance crop in `docs/design/evidence/req-23/template/focused-contrast.png`. The 390 × 844 CSS viewport capture is `docs/design/evidence/req-23/template/mobile-contrast.png`.
- Iteration: the initial comparison found overly tall rows and weak endpoint/security hierarchy. Compact identifiers, credential labels and separate guidance panels addressed these. A later review found normal explanatory text insufficiently contrasted; the local muted color was darkened to #536987 (5.62:1 on white, 5.13:1 on #f0f5fb). The captures above are after that fix.
- Typography hierarchy, card spacing, colors, status labels, icons and explanatory copy were inspected in the combined views. Source placeholder data and shell differences were not treated as missing functionality. The mobile browser test covers overflow, horizontal table scrolling and keyboard focus. A screen-reader pass was not performed.
- The Collector reports and per-source counters are separate extension screens, not implementations of the reception mockup. Their `reports/comparison.png` and `counters/style-comparison.png` compare shared style across different screens, not same-screen fidelity. Browser checks cover responsive use, source identification and table keyboard scrolling. The UI explicitly reports unknown overall Collector health and explains that deployment requires configuration; counters are not represented as application health.

final result: passed

# REQ-38 preliminary design QA

- Source: `docs/design/req-38-audit-concept.png`, generated from the selected Vigiloom style. Its sample content is illustrative; the current slice only records speed-settings changes and retains the existing navigation.
- Implementation: the actual Administrator audit page on an isolated LocalDB/Kestrel test site, viewed in the in-app browser at its desktop size and at 390 × 844 CSS px. The date-range error and a real saved test setting change were observed. The narrow table can scroll horizontally; a labelled keyboard-focusable region and explanatory text were subsequently added and still require visual and keyboard verification.
- At this preliminary stage, the images had not yet been saved and normalized into a joint comparison input. The earlier claim of a browser security rejection is unsupported by verified tool output. Focused visual comparison, keyboard/accessibility testing and console review remained outstanding at that stage.
- The user independently compared the initial rendered page with the reference and accepted the overall style and page structure. They checked the normal, invalid-date, empty and 390 px states and requested more specific date errors, clearer table hierarchy, a stronger horizontal-scroll hint and no redundant pagination on the first empty page. Those changes were implemented; the revised page has not yet had a post-fix comparison with the reference.
- In the revised page, keyboard testing confirmed Enter submits the query, Tab moves from the query button to the scroll region and then to the version disclosure, and Enter expands the disclosure. At a 390 px viewport, the right arrow moved the focused table's horizontal scroll position from 0 to 40 px. A screen-reader pass is still outstanding.

final result: blocked

## REQ-24 health settings — latest review

See `docs/design/req-24/design-qa.md` for the feature reference, normalized full-view and focused comparisons, desktop and narrow captures, iteration history, fidelity surfaces, and tested interactions. Its P2 findings were corrected and independently re-reviewed.

final result: passed

## REQ-39 speed retention — current review

See `docs/design/req-39/design-qa.md` for source limitations, combined desktop and narrow comparisons, interaction checks and iteration history.

final result: passed

## REQ-13 template history — current review

See `docs/design/req-13-history/design-qa.md` for the reference, same-state comparisons, narrow capture, and iteration.

final result: passed

# REQ-14 public status design QA

**Findings**

No actionable P0/P1/P2 visual issues remain in the compared states. The device history is an intentional interaction difference: the source depicts status bars while the authorized public data provides five-minute average latency, so a named control expands the real latency chart in its device row. This also avoids automatically issuing a history query for every device on the page.

**Comparison target and state**

- Source: `docs/design/req-13-public-status-concept.png`, 1487 × 1058 pixels.
- Browser-rendered implementation: `docs/design/req-14/desktop-latest.png`, 1487 × 1058 CSS pixels at device scale factor 1. Both are placed at their original dimensions in `docs/design/req-14/comparison-latest.png`.
- Responsive capture: `docs/design/req-14/mobile-latest.png`, 390 × 844 CSS viewport, full-page image. Expanded device: `docs/design/req-14/device-expanded.png` at 1280 × 900 CSS viewport.
- Both show the anonymous public status overview. Source uses illustrative healthy states, two groups and three devices/services; the isolated actual dataset uses one visible group, one device and one service with unknown states. The time bars, content density and colors therefore differ for legitimate data reasons.
- The full-view combined image and the device and service table regions were inspected. The expanded capture demonstrates the row disclosure and the explicitly unavailable history state; it does not verify a populated chart on a narrow viewport.

**Comparison history**

1. `desktop-before.png` showed overly tall service rows. They were compacted in `desktop.png`.
2. `comparison.png` showed vertically stacked desktop group and section headings. These were aligned and captured in `comparison-final.png`.
3. The device row had no obvious history action and the service strip lacked UTC range labels. A single row can now expand its real permitted latency history; the strip now shows its actual UTC query window. `comparison-latest.png` and `device-expanded.png` show the revised implementation.
4. An initial narrow capture showed the device name and history prompt running together. The name control was aligned vertically and re-captured in `mobile-latest.png`.
5. A 256-character public name initially squeezed the adjacent service status into individual vertical characters. Flexible widths and an unwrapped status fixed this; the actual 390 px result is `mobile-long-names.png`, and the browser test checks both page overflow and status height.

**Fidelity surfaces**

- Typography: system sans-serif, dark blue headings, hierarchy and muted copy follow the existing visual language; the mock has heavier icon-led headings.
- Spacing and layout: desktop group and section headings align horizontally; narrow controls stack and the history row remains readable.
- Colors: pale blue background, white cards, dark blue text and state colors follow the source. Unknown has an explicit text label.
- Images and assets: no new photographs or illustrations. The reference section icons are omitted; no substitute drawing is used.
- Copy and content: example data differ as described; copy distinguishes public devices from independent services and unknown from healthy.

**Interactions and limitations**

- Browser tests cover group filtering, device disclosure, actual public data access, service date filtering and revocation during an in-flight list request, including both Server and WebAssembly rendering.
- The new UTC range and recent-publication alignment have component tests. A screen-reader pass and a narrow-viewport capture containing a populated device latency chart were not performed.

**Follow-up polish**

- Consider approved section icons when assets are available.

final result: passed
