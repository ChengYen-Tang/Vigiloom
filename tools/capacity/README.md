# Capacity checks

Run these checks manually against an isolated environment, never as part of the
normal test suite. Start with the workload and recording guidance in
`docs/architecture/capacity-validation.md`. Provision the selected number of
test devices and a login session for an Administrator or Read only account. Use an HTTPS
endpoint with a trusted certificate; HTTP is allowed only on loopback.

Install [Grafana k6](https://grafana.com/docs/k6/latest/set-up/install-k6/).
For example, in PowerShell:

```powershell
$env:VIGILOOM_URL = 'https://localhost:5001'
$env:VIGILOOM_SESSION_COOKIE = '<authentication cookie name=value>'
$env:VIGILOOM_DEVICES = '100'
$env:VIGILOOM_DURATION = '5m'
k6 run --summary-export overview-result.json tools/capacity/overview.js
Remove-Item Env:VIGILOOM_SESSION_COOKIE
```

Supported device counts are 30, 100, 300 and 500, using respectively 2, 5,
10 and 15 concurrent readers. Preflight checks that the overview contains the
requested device count and that its sections are available. Each reader then
requests the overview and waits five seconds; HTTP errors, unavailable
sections and count mismatches fail the checks. The report includes request
counts, bytes, failures and latency percentiles. Keep credentials out of stored
results and remove test credentials and data after the exercise. Obtain the
authentication cookie from the isolated test browser's developer tools after
sign-in; treat it as a password and revoke the test session when finished.

This currently measures only the overview API under concurrent reads. It does
not create the devices, generate Agent or application traffic, measure real UI
rendering, or establish the system's supported capacity. Run those scenarios
and record the SQL and host measurements separately before drawing conclusions.

## Agent traffic

To provision test identities and run the sender automatically against a
dedicated SQL Server, set `VIGILOOM_TEST_SQL_CONNECTION` to its `master`
connection and `VIGILOOM_TEST_SQL_ALLOW_SOURCE` to the exact data source, as
described in `tests/Vigiloom.Testing.SqlServer/README.md`. Then run:

```powershell
./tools/capacity/run-agent-load.ps1 -Agents 30 -Seconds 300
```

The optional `-SdkVersion` overrides the pinned SDK only inside the temporary
working directory. The script uses a run-owned temporary database and real
Kestrel HTTPS, creates test Agent credentials, checks accepted counts against
database rows, fails on non-204 or unknown HTTP results, and prints the sender
summary and SQL version. It is not part of
the regular CI tests. It removes its temporary fixture, credentials, certificate
and database. If database cleanup cannot complete, the script preserves the
scratch directory and directs the operator to the fixture's recovery procedure.
Use an isolated server: this run creates and deletes a test database.

`Vigiloom.CapacitySender` uses the product's Agent batch codecs and sends one
20-point metric batch per Agent every 30 seconds and one 512-character host
log every five seconds. It requires 30, 100, 300 or 500 **distinct** already
enrolled test Agent credentials. Put the credentials in a private JSON array
file outside the repository; use only an isolated environment and revoke the
credentials and remove the file when finished.
For an isolated test server directly signed by a private root CA,
`VIGILOOM_TEST_CA` can point to that root certificate file. The sender still
verifies the certificate chain and hostname; omit this setting to use the
operating system trust store. Private CA chains with an intermediate have not
been verified by this test tool.

```powershell
$env:VIGILOOM_URL = 'https://localhost:5001'
$env:VIGILOOM_AGENT_CREDENTIALS_FILE = '<private path to JSON array>'
$env:VIGILOOM_DURATION_SECONDS = '300'
dotnet run --project tools/Vigiloom.CapacitySender --configuration Release
Remove-Item Env:VIGILOOM_AGENT_CREDENTIALS_FILE
```

The JSON summary separates accepted metric points and logs from batch HTTP
statuses; attempted payload bytes include failed requests. It also reports
average and maximum time for all HTTP attempts by type, including failures and
time waiting for a connection, through response headers. These are not
server-only timings. The
sender serializes requests per Agent as the real Agent batch worker does and
waits for each response, so slow responses reduce the actual input rate. This
does not simulate the real Agent queue or its batching behavior.
At the configured deadline it stops starting requests and waits up to the HTTP
timeout for in-flight requests; the output includes actual elapsed time. Use
observed counts and time, not target intervals, to calculate throughput. A
timed-out request may have been committed despite an unknown response. It does
not retry or simulate offline replay; 204 confirms batch
receipt but does not measure processing lag or the resources used by real
Agents. It does not generate application telemetry. Measure those separately.
