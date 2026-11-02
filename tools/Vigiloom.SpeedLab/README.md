# Speed measurement lab

The default command runs one **real network measurement** through the NetworkMonitoring module's [`SpeedtestNetProbe`](../../src/Modules/Vigiloom.Modules.NetworkMonitoring/Application/SpeedTests/SpeedtestNetProbe.cs). It registers the same factory-managed HTTP handler as the Worker and calls `MeasureAsync`; server discovery, selection, latency, download, upload, traffic limits and timeouts are all handled by the module. It requires no database, Worker host or browser.

Run from the repository root with the .NET 10 SDK:

```sh
dotnet run --project tools/Vigiloom.SpeedLab --configuration Release
```

The probe requests Speedtest.net's suggested servers and falls back to the static directory if necessary. In automatic mode it screens up to 10 candidates concurrently, sorts responsive servers by HTTP latency and uses the fastest as primary plus up to three more for download, then uploads to the primary server. It starts with four requests and can increase concurrency in the first half of each direction: up to 24 total and 6 per download server, and up to 6 for upload. These are bounded heuristics inspired by the inspected client, not its verified algorithm. Each direction targets at least 15 seconds of measured transfer. The lab lease permits up to 4,096 MiB of content traffic and 60 seconds overall; the production settings have their own limits. Traffic or time limits can prevent either direction from reaching 15 seconds, in which case the result is not Success. Press Ctrl+C to cancel. The selected candidates may not provide the highest throughput; no equivalence to the official client is guaranteed.

Live uploads use independent request lanes that immediately send another payload after each successful response, without a batch barrier. Payload sizes adapt to confirmed throughput; the endpoint is resolved once. Upload speed uses approximately 500 ms samples of successfully confirmed payloads; these are not a continuous wire-level byte count. In-flight requests are cancelled at the end. This approximates only parts of the inspected official client and does not use its pipeline or remote server statistics. Real measurements are needed to evaluate stability.

Standard output contains one JSON result with `mode: "live"`, server, city, country, download/upload Mbps, latency in milliseconds, content bytes, coordinates, elapsed seconds, state and diagnostic code. Status messages go to standard error. Provider failures remain failures and never fall back to simulated results. Exit codes: `0` for Success, `1` for any other measurement result, `2` for invalid arguments, `130` for cancellation.

To run repeatable multi-server measurements with the same ordered server IDs, use `--servers`. The first ID is the primary server used for latency and upload:

```sh
dotnet run --project tools/Vigiloom.SpeedLab --configuration Release -- --servers 14696,8968,18458,14690
```

The selected IDs must all be present and pass latency screening; the tool fails rather than silently changing the server set.

For repeatable comparisons, select a Speedtest server by its numeric ID:

```sh
dotnet run --project tools/Vigiloom.SpeedLab --configuration Release -- --server-id 17204
```

An explicit ID runs a single-server measurement using four parallel requests per direction. The requested ID is checked against the provider's returned server IDs. If the server cannot be found or reached, the tool reports a failure rather than choosing a different server.


For usage without contacting any network service:

```sh
dotnet run --project tools/Vigiloom.SpeedLab --configuration Release -- --help
```

The original synthetic benchmark is available only with an explicit flag:

```sh
dotnet run --project tools/Vigiloom.SpeedLab --configuration Release -- --offline
```

Offline mode uses an in-memory HTTP handler and **does not connect to any network service**. It runs two trials each for 890/244 Mbps without additional request delay, 890/244 Mbps with 50 ms added per request, and 10/5 Mbps with the same delay. Each JSON line is marked `mode: "offline"` and reports the configured capacity, module result and separate observed reference rates. Fixture image sizes are synthetic and do not represent actual Speedtest.net image sizes.

On Windows, offline mode temporarily requests one-millisecond timer precision and restores it at exit. Host scheduling, CPU, cancellation, buffering and timer precision can change the simulated rate; cancelled transfers do not reclaim their scheduled time slots. Added request delay does not model real TCP, TLS, congestion control or packet loss. Offline results compare implementation changes under repeatable conditions; they cannot verify real-provider compatibility or official accuracy. Automated tests separately cover cancellation, small budgets and interrupted transfers.
