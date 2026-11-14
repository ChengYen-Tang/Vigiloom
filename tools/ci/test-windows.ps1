$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$solution = Join-Path $root 'Vigiloom.slnx'
$webProject = 'tests/Vigiloom.Web.Tests/Vigiloom.Web.Tests.csproj'
$temporarySolution = Join-Path $root ('.vigiloom-ci-' + [guid]::NewGuid().ToString('N') + '.slnx')
$jobs = @()
$failures = [System.Collections.Generic.List[System.Exception]]::new()

try {
    [xml]$document = Get-Content -LiteralPath $solution -Raw
    $projects = @($document.SelectNodes('//Project[@Path]'))
    $paths = @($projects | ForEach-Object { $_.GetAttribute('Path').Replace('\', '/') })
    $unique = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($path in $paths) {
        if (-not $unique.Add($path)) { throw "Duplicate solution project: $path" }
    }
    $webNodes = @($projects | Where-Object { $_.GetAttribute('Path').Replace('\', '/') -ieq $webProject })
    if ($webNodes.Count -ne 1) { throw 'The solution must contain exactly one Web test project.' }
    $null = $webNodes[0].ParentNode.RemoveChild($webNodes[0])
    $remaining = @($document.SelectNodes('//Project[@Path]') | ForEach-Object { $_.GetAttribute('Path').Replace('\', '/') })
    if ($remaining.Count -ne $paths.Count - 1 -or @($remaining | Where-Object { $_ -ieq $webProject }).Count -ne 0) {
        throw 'The two test lanes do not form a partition of the solution.'
    }
    $document.Save($temporarySolution)

    $runTests = {
        param($project, $nodes)
        & dotnet test $project --configuration Release --no-build --no-restore "-m:$nodes"
        if ($LASTEXITCODE -ne 0) { throw "Test lane failed with exit code $LASTEXITCODE." }
    }
    $jobs += Start-Job -WorkingDirectory $root -ScriptBlock $runTests -ArgumentList $webProject, 1
    $jobs += Start-Job -WorkingDirectory $root -ScriptBlock $runTests -ArgumentList $temporarySolution, 2
    $drained = [System.Collections.Generic.HashSet[int]]::new()
    while ($true) {
        foreach ($job in $jobs) {
            if ($drained.Contains($job.Id)) { continue }
            $terminal = $job.State -notin 'Running', 'NotStarted'
            Receive-Job $job -ErrorAction Continue
            if ($terminal) { $null = $drained.Add($job.Id) }
        }
        $running = @($jobs | Where-Object { $_.State -in 'Running', 'NotStarted' })
        if ($running.Count -eq 0 -and $drained.Count -eq $jobs.Count) { break }
        if ($running.Count -eq 0) { continue }
        $null = Wait-Job -Job $running -Any -Timeout 3
    }
    if (@($jobs | Where-Object State -ne 'Completed').Count -ne 0) { throw 'At least one test lane failed.' }
} catch {
    $failures.Add($_.Exception)
} finally {
    foreach ($job in $jobs) {
        try {
            if ($job.State -in 'Running', 'NotStarted') { Stop-Job $job -ErrorAction Stop }
        } catch { $failures.Add($_.Exception) }
        try { Remove-Job $job -Force -ErrorAction Stop }
        catch { $failures.Add($_.Exception) }
    }
    try {
        if (Test-Path -LiteralPath $temporarySolution) { Remove-Item -LiteralPath $temporarySolution -ErrorAction Stop }
    } catch { $failures.Add($_.Exception) }
}
if ($failures.Count -eq 1) { throw $failures[0] }
if ($failures.Count -gt 1) { throw [AggregateException]::new('Test execution or cleanup failed.', $failures) }
