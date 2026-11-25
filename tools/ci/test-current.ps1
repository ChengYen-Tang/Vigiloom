$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$temporary = Join-Path $root ('.vigiloom-ci-' + [guid]::NewGuid().ToString('N') + '.slnx')
# Update this list as migrated services replace their legacy module tests.
$testProjects = @(
    'Vigiloom.Architecture.Tests', 'Vigiloom.BuildingBlocks.Tests', 'Vigiloom.Contracts.Tests',
    'Vigiloom.Identity.Tests', 'Vigiloom.Modules.Resources.Tests',
    'Vigiloom.Agent.Core.Tests', 'Vigiloom.Agent.Platforms.Linux.Tests', 'Vigiloom.Agent.Platforms.Windows.Tests',
    'Vigiloom.Agent.Tests', 'Vigiloom.Updater.Core.Tests', 'Vigiloom.Updater.Tests', 'Vigiloom.UpdateSecurity.Tests'
)
try {
    [xml]$solution = Get-Content -LiteralPath (Join-Path $root 'Vigiloom.slnx') -Raw
    $nodes = @($solution.SelectNodes('//Project[@Path]'))
    $found = @($nodes | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_.GetAttribute('Path')) })
    foreach ($test in $testProjects) { if ($test -notin $found) { throw "Missing test project: $test" } }
    foreach ($node in $nodes) {
        $name = [IO.Path]::GetFileNameWithoutExtension($node.GetAttribute('Path'))
        if ($name.EndsWith('.Tests', [StringComparison]::Ordinal) -and $name -notin $testProjects) {
            $null = $node.ParentNode.RemoveChild($node)
        }
    }
    $solution.Save($temporary)
    & dotnet test $temporary --configuration Release --no-build --no-restore -m:2
    if ($LASTEXITCODE -ne 0) { throw "Current service tests failed with exit code $LASTEXITCODE." }
    & dotnet test (Join-Path $root 'tests/Vigiloom.Infrastructure.Tests/Vigiloom.Infrastructure.Tests.csproj') --configuration Release --no-build --no-restore --filter 'FullyQualifiedName~ServiceWorkTests|FullyQualifiedName~TransactionalWorkTests|FullyQualifiedName~CrossProcessTemporalTests'
    if ($LASTEXITCODE -ne 0) { throw "Temporal work tests failed with exit code $LASTEXITCODE." }
} finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force -ErrorAction Stop }
}
