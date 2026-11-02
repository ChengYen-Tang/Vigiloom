# Offline speed measurement lab

This diagnostic compares the speed-test client against a synthetic, shared bandwidth bottleneck. It uses an in-memory HTTP handler and **does not connect to Speedtest.net or any other network service**. It runs two trials each for 890/244 Mbps without additional request delay, 890/244 Mbps with 50 ms added per request, and 10/5 Mbps with the same delay.

Run from the repository root with the .NET 10 SDK:

```sh
dotnet run --project tools/Vigiloom.SpeedLab --configuration Release
```

Each JSON line reports the configured capacity, result, total content traffic, and a separate count and elapsed-rate estimate from the simulated sender/receiver. The reference estimates use elapsed time between their first and last observed bytes, while the product calculates over completed transfer batches; the two timings are not identical. The download fixture assigns payload sizes based on the requested image dimensions solely to make different request sizes observable. Actual Speedtest.net image sizes are not assumed to match this model.

On Windows, the lab temporarily requests one-millisecond timer precision and restores it at exit. Host scheduling, CPU, cancellation, buffering and timer precision can change the simulated rate; cancelled transfers do not reclaim their scheduled time slots. The lab reports observed reference rates rather than assuming the configured rates were achieved. Added request delay does not model real TCP, TLS, congestion control, packet loss, or the official Speedtest implementation. This tool helps compare implementation changes under repeatable conditions; it cannot verify real-provider compatibility or official accuracy. Automated tests separately cover cancellation, small budgets and interrupted transfers.
