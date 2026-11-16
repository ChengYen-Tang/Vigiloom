# REQ-15 design QA

- Source: `docs/design/req-15-notifications-concept.png`, 1487 × 1058 pixels.
- Implementation: `docs/design/evidence/req-15/desktop.png`, 1487 × 1058 CSS pixels, device scale factor 1, Administrator, populated notification history. The side-by-side and focused comparisons are `comparison.png` and `focused.png` in the same evidence directory.
- Responsive capture: `docs/design/evidence/req-15/mobile.png`, 390 CSS pixels wide. Date inputs remain grouped, the filter is visible, and a horizontal-scroll hint precedes the table.
- The concept uses illustrative records, a settings sidebar and example severity labels. The implementation uses the existing application navigation, two isolated test records and only currently supported data; the delivery-settings button is omitted until REQ-16. These are intentional content and navigation differences rather than missing sample records.
- The actual page retains the blue and white palette, hierarchy, delivery notice, filter panel, readable table header, source badges and related-resource links. The focused table and filter comparison found no actionable P0/P1/P2 visual issue. No visual changes were made during this comparison.
- Source filtering, invalid date order, empty results and read-only access were separately covered by the notification HTTP test. The mobile screenshot was visually inspected; a complete keyboard and assistive-technology audit was not performed.
- The in-app browser opened the page but produced tiled images after resizing; the final captures used the repository's existing Playwright browser-test setup against the isolated local HTTP site. Test site, browser and database were closed after capture.

final result: passed
