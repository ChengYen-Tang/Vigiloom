# REQ-38 operation audit page

The [concept](req-38-audit-concept.png) extends the selected Vigiloom style with a quiet settings heading, date filters and a readable audit table. It is a visual reference, not a source of product facts: its sample P95, API and delete events are illustrative and are not implemented by this slice. Keep the existing global navigation rather than introducing a separate settings sidebar.

Administrators can filter by an inclusive UTC date range of up to 31 days and page through committed events. The initial supported event is a successful change to central speed-test settings; the page makes clear that other operations and failures are not yet included. Show the actor name when available, otherwise its ID, and the previous and current non-secret values. A successful entry means the setting was saved, not that a measurement was run. Unknown event types or malformed details show an unavailable message and never reveal raw stored data. Empty, invalid range, account lookup failure and unavailable data have distinct states. Read only and anonymous users cannot access the page or its API.

Visual comparison, keyboard and accessibility verification are pending; the design is not a completed REQ-38 acceptance.
