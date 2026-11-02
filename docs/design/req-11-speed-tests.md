# REQ-11 UI plan

Extend the selected `Vigiloom-UI-Concept-2-Revision-2.png` and the existing device/settings controls. Use the existing MudBlazor and Vizor.ECharts components.

- The independent Network speed navigation provides private history, date and server filtering, stable sort/page, two throughput trends and single-result detail. Non-success entries are gaps in the trends.
- Speedtest.net measurements run automatically on a schedule. The Administrator can adjust or disable the schedule; optional advanced limits are available without requiring data-volume choices for ordinary use. Location, traffic impact, cancellation and failures remain visible. Read only users can inspect results without management actions.
- History/chart interactivity uses per-page Auto with server and browser facades. Mutations use authorized SSR forms with CSRF validation.
- ISP uses only information actually supplied by Speedtest.net, never an independent IP lookup or the selected test server's sponsor. The current NetPace integration does not obtain client ISP, so the detail view says there is no data; this does not assert that Speedtest.net itself has no such data. Server city, country and coordinates are labeled as server attributes, not the measurement location.
