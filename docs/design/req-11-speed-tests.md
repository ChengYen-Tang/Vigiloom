# REQ-11 UI plan

Extend the selected `Vigiloom-UI-Concept-2-Revision-2.png` and the existing device/settings controls. Use the existing MudBlazor and Vizor.ECharts components.

- The independent Network speed navigation provides private history, date and server filtering, stable sort/page, two throughput trends and single-result detail. Non-success entries are gaps in the trends.
- The Administrator can enable Speedtest.net measurement and configure automatic scheduling, approximate data budget and timeout. Manual runs display their location and impact; cancellation and failures remain visible. Read only users can inspect results without management actions.
- History/chart interactivity uses per-page Auto with server and browser facades. Mutations use authorized SSR forms with CSRF validation.
