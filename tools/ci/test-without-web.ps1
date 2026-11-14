$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$web = 'tests/Vigiloom.Web.Tests/Vigiloom.Web.Tests.csproj'
$temporary = Join-Path $root ('.vigiloom-ci-' + [guid]::NewGuid().ToString('N') + '.slnx')
$failure = $null
$cleanupFailure = $null
try {
    [xml]$solution = Get-Content -LiteralPath (Join-Path $root 'Vigiloom.slnx') -Raw
    $projects = @($solution.SelectNodes('//Project[@Path]'))
    $names = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($project in $projects) {
        $name = $project.GetAttribute('Path').Replace('\', '/')
        if (-not $names.Add($name)) { throw "Duplicate solution project: $name" }
    }
    $webNodes = @($projects | Where-Object { $_.GetAttribute('Path').Replace('\', '/') -ieq $web })
    if ($webNodes.Count -ne 1) { throw 'The solution must contain exactly one Web test project.' }
    $null = $webNodes[0].ParentNode.RemoveChild($webNodes[0])
    $remaining = @($solution.SelectNodes('//Project[@Path]'))
    if ($remaining.Count -ne $projects.Count - 1 -or @($remaining | Where-Object { $_.GetAttribute('Path').Replace('\', '/') -ieq $web }).Count -ne 0) {
        throw 'The Web and remaining projects do not partition the solution.'
    }
    $solution.Save($temporary)
    & dotnet test $temporary --configuration Release --no-build --no-restore -m:2
    if ($LASTEXITCODE -ne 0) { throw "Remaining solution tests failed with exit code $LASTEXITCODE." }
} catch {
    $failure = $_.Exception
} finally {
    try {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -ErrorAction Stop }
    } catch { $cleanupFailure = $_.Exception }
}
if ($failure -and $cleanupFailure) { throw [AggregateException]::new('Tests and temporary solution cleanup failed.', @($failure, $cleanupFailure)) }
if ($failure) { throw $failure }
if ($cleanupFailure) { throw $cleanupFailure }
