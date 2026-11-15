# REQ-34 design QA

- Source: `../req-34-vulnerabilities-concept.png` (1487 × 1058).
- Implementation: `desktop.png`, captured at a 1440 × 1024 viewport with a controlled stale result and expanded history; full page is 1440 × 2268.
- Focused comparison: `comparison.png`. Both crops show the vulnerability panel and expanded history; the source contains many illustrative rows while the implementation fixture has one. The different device shell follows the existing implementation and is not evidence of missing sample data.
- Browser interaction: the controlled fixture also exercised a 390 × 844 viewport, stale notice, assumed origin and history expansion. This does not verify actual API data or the WebAssembly render path.

## Findings

- P2 (fixed): The source includes package/CVE search and status filters. The implementation initially lacked them; both now filter the server query before pagination. `comparison-after.png` and `comparison-final.png` show the controls and expanded history after the change.
- P2 (fixed): The initial result table omitted the observed binary version and wrapped status labels on desktop. The updated table includes the actual recorded version and keeps status labels together; see `comparison-final.png`.
- The source depicts fabricated severity, confidence scores and a coverage percentage. These are intentionally omitted until backed by actual evidence; they must not be copied as if they were real data.
- The source depicts an older device shell and navigation arrangement. The implementation uses the currently shipped device details layout; changing the entire shell is outside this visual comparison.

The final focused comparison uses the same expanded-history interaction with one controlled fixture row. No remaining actionable P0/P1/P2 visual difference was identified within the implemented vulnerability panel. The 390px browser test covers stale notice, filtering and expanded history; the screenshot comparison does not establish live API or WebAssembly behavior.

final result: pass for the compared panel; overall REQ-34 acceptance remains incomplete.
