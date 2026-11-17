# Blazor Auto browser verification

For interactive visual review of an isolated Web test site, set `VIGILOOM_VISUAL_QA=1` and run **one specific browser test case in one process**. The site then uses the stable origin `http://localhost:5043`, matching the normal development HTTP profile. For example, filter by `FullyQualifiedName~AdministratorCanAssignAndCancelVersion` for the Agent version page. Ensure no other process is listening on that port before the review. Do not run the whole browser suite with this flag: its parallel tests would compete for the same port. Leave the variable unset for ordinary automated tests and CI, which use dynamically assigned ports so independent runs can coexist.

The Web MSTest project includes real Chromium integration tests for the protected workspace and shared chart. They distinguish the prerendered document from the first interactive Server visit and a later WebAssembly visit using the same browser context and its cached assets. Tests also cover offline/reconnection, changing the user's security stamp and role, natural cookie expiry, and a missing chart sample on a real time axis.

Build the Web test project, then install Playwright's Chromium once before running the tests:

```powershell
dotnet build tests/Vigiloom.Web.Tests/Vigiloom.Web.Tests.csproj
pwsh tests/Vigiloom.Web.Tests/bin/Debug/net10.0/playwright.ps1 install chromium
dotnet test tests/Vigiloom.Web.Tests/Vigiloom.Web.Tests.csproj --filter "TestCategory=Browser"
```

On Linux, install the browser's OS dependencies as documented by Playwright; CI uses `playwright.ps1 install --with-deps chromium`. The workspace tests require the SQL test fixture: on Windows the default is LocalDB, while Linux needs an explicitly permitted disposable SQL Server. See `tests/Vigiloom.Testing.SqlServer/README.md` for isolation and cleanup. The chart host is registered only inside the test process and is not a product endpoint.
