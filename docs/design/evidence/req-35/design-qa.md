# REQ-35 design QA

- Baseline results: the relevant portion of `docs/design/req-35-baseline-concept.png` and the rendered device panel are combined in `docs/design/evidence/req-35/comparison.png`. The source's example checks are illustrative; the actual page shows the implemented source and outcomes. Desktop and narrow panel captures are in the same directory.
- Custom checks: `req-35-custom-baseline-concept.png` and `custom-desktop.png` are combined at the same 1536 × 1024 viewport in `custom-comparison.png`. The existing application navigation and supported OS choices differ intentionally from the illustration. The saved draft and activation preview, blue/white cards, form labels, explanatory text and actions are visible. `custom-mobile.png` shows the responsive layout at 390 CSS pixels.
- The separate exception explanation form follows the same established visual direction; `exception-desktop.png` records the rendered form. An isolated browser test saves a note and checks administrator and read-only behavior.
- Checked actual rendering, not sample content in the design. Browser and HTTP tests cover editing, publication and exception access. These checks are not a full assistive-technology audit.

final result: passed
