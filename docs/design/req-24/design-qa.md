# REQ-24 health settings design QA

final result: passed

- Source: `health-settings-reference.png`, 1487 × 1058 pixels. This generated reference illustrates the selected style, not the product contract: the sample identifiers are not UUIDs, its latest-observation column omits the result, and its top explanation incorrectly implies latest observations alone determine current health. The implementation follows the documented health contract instead.
- Implementation: `desktop-final.png` and `narrow-final.png`, signed-in Administrator using an isolated LocalDB database, a service, and a disabled test endpoint. Desktop viewport was 1487 × 1058 CSS px at DPR 1; captured content was 1472 × 1047 pixels. Mobile viewport was 390 × 844 CSS px at DPR 1, with a 374 × 2062 pixel full-page capture. Scrollbars account for the smaller captured content area.
- Full-view comparison: `desktop-comparison.png` displays source and implementation together; the implementation is normalized to the reference image dimensions. Focused table comparison: `table-comparison.png`. The illustration has example data, a service dropdown and an invented sidebar; the implementation uses actual service/source identity, real paged selection and the existing application shell.

## Findings and iteration

1. Initial narrow capture `implementation-top.png`: P2 — a wide table caused document-level horizontal overflow. Added a bounded, keyboard-focusable table scroller; `implementation-after.png` shows the local scrollbar. A narrow-screen instruction was added and appears in `narrow-final.png`.
2. Desktop grid iteration `desktop-top.png`: P2 — excess vertical whitespace and visual order diverging from keyboard order. Reduced grid row expansion and moved the network policy section after the form in the document. `desktop-final.png` shows the revised grid; the accessibility tree confirms the matching section order.
3. Independent review: P2 — muted copy `#667a99` was approximately 4.37:1 on white. The page now uses `#536987`, measured from the browser and calculated as 5.62:1 on white. `desktop-final.png` and `narrow-final.png` show the revision.
4. Focused comparison: P2 — narrow desktop columns split the edit label. The first and last table columns now do not wrap and endpoint text can wrap. The final desktop table has a 572 px content and scroll width; the focused comparison shows the revised labels.

No actionable P0/P1/P2 findings remain in the reviewed states. The existing global navigation and unimplemented example-only controls intentionally differ from the illustration.

## Reviewed surfaces and interactions

- Typography: existing system and Traditional Chinese fallback fonts, readable heading/label hierarchy, no split edit action.
- Layout: white cards on the existing pale background, two-column desktop layout and single-column narrow layout; scrolling is confined to the table.
- Color: existing blue actions and pale-blue table header; page-local muted text meets 4.5:1 against white and the page background.
- Images and icons: no new decorative assets; existing application navigation is retained.
- Copy: the latest observation is not presented as current health; network policy applies to all services; empty network policy stops checks.
- In-app browser: authenticated, selected a service, created a disabled test target and opened it for editing, captured empty and populated states, and checked console errors (none). The focusable table accepted Right arrow and moved scrollLeft from 0 to 40 pixels. Full screen-reader testing was not performed.
