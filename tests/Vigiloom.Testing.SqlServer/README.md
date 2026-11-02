# SQL Server test databases

`SqlTestDatabase` provisions one uniquely named database per test on the shared Windows `(localdb)\MSSQLLocalDB` instance. Tests select the server explicitly with `TestDatabaseServer.FromEnvironment()`; use `ExecuteAsync` to preserve both test and cleanup errors. The helper does not read Web or Worker configuration. Administrative connections are not pooled; workload connections use a separate pool per test database, which is cleared during owned database cleanup. Stop and dispose hosts, background work, and connections before cleanup. `CompleteRun()` must be called from MSTest assembly cleanup.

An isolated SQL Server can be supplied explicitly with `VIGILOOM_TEST_SQL_CONNECTION` (initial catalog must be `master`) and `VIGILOOM_TEST_SQL_ALLOW_SOURCE` (the exact data source). Provision a dedicated test server and account; do not point either setting at a development or production database. Neither setting is read from product configuration or written to the manifest. Missing LocalDB or an invalid test configuration fails the database tests.

The database test category has been exercised on Windows LocalDB and on Linux with SQL Server 2022 in GitHub Actions; the Linux run executes all 15 tests rather than skipping platform-incompatible cases.

Each run has a `run.json`, a run lease and individual case manifests under the operating system temporary directory `VigiloomSqlTests`. The manifest is written before database creation. Normal cleanup checks the generated name and source; for LocalDB it also verifies the database's exact owned file paths before DROP and checks those files are removed.

If a test host is forcibly terminated, after it has exited recover its exact run manifest:

```powershell
dotnet run --project tests/Vigiloom.Testing.SqlServer -- recover-run <absolute-run-manifest-path>
```

The helper also accepts `recover <absolute-case-manifest-path>` for one case. Recovery refuses a live run or case, does not scan other runs and never stops or deletes the shared LocalDB instance. Cleanup errors are reported and leave the manifest for recovery. A case manifest and its containing run manifest are written atomically; recovery of an interrupted pre-manifest case is handled through `recover-run`.

The helper uses `Microsoft.Data.SqlClient`. The `LocalDb` / `EfLocalDb.MSTest` package family was evaluated; its documented instance and stale-directory cleanup does not match this project's requirement to keep the shared instance and other runs untouched. Reassess future versions if they can meet the ownership contract without side effects.
