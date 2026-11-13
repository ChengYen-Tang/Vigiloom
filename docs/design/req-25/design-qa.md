# REQ-25 design QA

Final result: in progress — service details have been reviewed; the onboarding guide and its final visual check are still being implemented.

The selected Vigiloom visual language is the blue/white reference in `../Vigiloom-UI-Concept-2-Revision-2.png`. The service-detail concepts use illustrative data only. The empty-state comparison uses `../req-25-service-details-empty-concept.png` and the rendered, signed-in Read only page in `desktop.png`; both depict an approved service with no health observations, instances or HTTP metrics. The concept depicts an Administrator and illustrative navigation, while the tested page uses the application's existing navigation and does not show administrator actions to Read only.

The source is 1487×1058 pixels; the rendered capture uses a 1486×1058 CSS viewport at device scale 1, with a 1486×1312 full-page image. `desktop-comparison.png` normalizes the one-pixel source difference and places the same top viewport beside the implementation. `detail-comparison.png` compares the health region at native pixel scale. `mobile.png` captures the 390×844 CSS viewport at device scale 1, with its full scrollable page.

The first visual pass found an actionable contrast defect: the unselected range buttons inherited white text from the global button style on a white background. The component now explicitly applies dark text, and the browser test checks its computed colour. The revised desktop and mobile captures show readable controls, grouped UTC labels, a textual status and striped unknown interval, with a keyboard-accessible history table. The table is collapsed by default to keep the timeline and subsequent sections discoverable.

Intentional differences from the concept: the real application does not invent a third dependency metric, a P95, last-update values, placeholder instances or an administrator-only guide for the Read only role. It reports the supported average only when the received histogram can be evaluated, with explicit missing-data wording. Existing application navigation and full-width page layout are reused rather than recreating conceptual navigation.

Validation: `ServiceDetailsBrowserTests` exercises the real SQL-backed page, Server and WebAssembly interactivity, 390px layout, missing health observations, time-range validation, Trace-to-Log navigation and cancellation of a superseded request. The browser screenshots use synthetic test records and contain no credentials.
