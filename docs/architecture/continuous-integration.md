# Continuous integration

Every branch push and pull request runs the `CI` workflow. The Windows and Ubuntu jobs run independently and in parallel. Both restore and build the entire solution in Release using `global.json`, install Chromium for the browser tests, and run `dotnet test` on the entire solution without rebuilding. Projects that currently contain no tests remain part of the solution build; a successful run does not imply unimplemented functionality has been tested.

Windows uses the runner's LocalDB instance and the test fixture's isolated, uniquely named databases. Ubuntu starts a disposable SQL Server container, configures an explicit test-only connection and verifies that tests leave no test databases behind and do not alter the development-named sentinel on that disposable server. The container is removed even when earlier steps fail. Neither job connects to a developer or production database.

The `master` branch requires both `Windows build and test` and `Ubuntu build and test` checks before a pull request can merge. These names are part of the GitHub branch protection configuration: update that configuration if the job names change. Other branches still run CI, but do not have this merge restriction unless protection is explicitly extended.
