# REQ-11 UI plan

> **Work in progress, 2026-11-02:** The M-Lab-specific consent and provider copy below was based on an incorrect provider selection. Redesign these areas for the authorized Speedtest.net integration and its actual terms before delivery.

Extend the selected `Vigiloom-UI-Concept-2-Revision-2.png` and the existing device/settings controls with Product Design. White cards/light-blue shell, existing blue actions, MudBlazor semantic badges, shared Vizor.ECharts time-series component. No extra UI/chart packages.

- Independent **Network speed** navigation: private history, date range/server filter, stable sort/page, latest state, downloadable-looking actions only when implemented (no placeholder controls).
- Two throughput trends (download/upload, decimal Mbps) share the same query result as the table; non-success values are gaps. Latency ms and partial results are explicit in single-result detail. No coordinate map when no data is available.
- Administrator settings page owns informed M-Lab consent and automatic schedule/limits. Manual start creates a pending job then shows its status; cancellation stops only active work. Read only sees data and no management actions. Errors preserve input and expose retry/login recovery.
- History/chart interactivity uses per-page Auto with server and browser facades; initial prerender does not duplicate the load. Mutations stay on authorized SSR forms with CSRF. Detail is a simple authorized SSR page.
- Desktop/mobile and actual Server→cached WebAssembly execution, role/session expiry, empty/no-server/partial/queued/running/success/error/conflict states will be verified against real SQL and browser rendering. No fake production data or mocked graph evidence.
