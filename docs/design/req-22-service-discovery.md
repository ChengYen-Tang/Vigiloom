# REQ-22 service discovery design work in progress

The Administrator uses this page to identify recent candidates, approve them as Private, and manage independently authenticated sources. It uses the [selected Vigiloom visual direction](Vigiloom-UI-Concept-2-Revision-2.png); the [exploration image](req-22-service-discovery-concept.png) contains fictional examples. Its source trust badges and implication that Private is administrator-only are not product behavior. The page explicitly states that the formal telemetry pipeline is not yet available.

The implementation presents source identifiers and requires a confirmation for approval and revocation, including when sources share a name. It shows a newly issued secret only in the issuance response; later reads do not include it. Source and candidate lists are paged independently. Other roles cannot access the management page.

Isolated SQL/HTTP tests cover two candidates and sources, one-time secret display, approval, revocation and authorization. Browser testing captured [desktop](evidence/req-22/desktop.png) and [390px](evidence/req-22/mobile.png), and exercised approval and keyboard focus in the mobile viewport. Both contain synthetic data only.

Visual design review remains open: the exploration image uses a multi-row table while the current implementation uses individual confirmation cards, and the captures do not have matching content/state. A like-for-like comparison and any resulting revisions are still required. Full keyboard, screen-reader, error-state and concurrent expiry interaction checks also remain outstanding.
