# Capacity checks

Run these checks manually against an isolated environment, never as part of the
normal test suite. Start with the workload and recording guidance in
`docs/architecture/capacity-validation.md`. Provision the selected number of
test devices and an API credential with monitoring-read access. Use an HTTPS
endpoint with a trusted certificate; HTTP is allowed only on loopback.

Install [Grafana k6](https://grafana.com/docs/k6/latest/set-up/install-k6/).
For example, in PowerShell:

```powershell
$env:VIGILOOM_URL = 'https://localhost:5001'
$env:VIGILOOM_API_TOKEN = '<test monitoring-read credential>'
$env:VIGILOOM_DEVICES = '100'
$env:VIGILOOM_DURATION = '5m'
k6 run --summary-export overview-result.json tools/capacity/overview.js
Remove-Item Env:VIGILOOM_API_TOKEN
```

Supported device counts are 30, 100, 300 and 500, using respectively 2, 5,
10 and 15 concurrent readers. Preflight checks that the overview contains the
requested device count and that its sections are available. Each reader then
requests the overview and waits five seconds; HTTP errors, unavailable
sections and count mismatches fail the checks. The report includes request
counts, bytes, failures and latency percentiles. Keep credentials out of stored
results and remove test credentials and data after the exercise.

This currently measures only the overview API under concurrent reads. It does
not create the devices, generate Agent or application traffic, measure real UI
rendering, or establish the system's supported capacity. Run those scenarios
and record the SQL and host measurements separately before drawing conclusions.
