param([string]$AgentRoot, [string]$Source, [string]$State, [string]$Operation)
$ErrorActionPreference = 'Stop'
$base = Split-Path -Parent $AgentRoot
$current = Join-Path $base 'Updater'
$next = Join-Path $base 'Updater.next'
$previous = Join-Path $base 'Updater.previous'
$status = Join-Path $State 'updater-replacement.status'
function Set-Status([string]$Value) { [IO.File]::WriteAllText($status, "$Operation $Value") }
try {
    Set-Status 'started'
    $expectedAgentRoot = Join-Path $env:ProgramFiles 'VigiloomAgent\Agent'
    $expectedState = Join-Path $env:ProgramData 'VigiloomAgent\Updater'
    if (-not [string]::Equals([IO.Path]::GetFullPath($AgentRoot), [IO.Path]::GetFullPath($expectedAgentRoot), [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals([IO.Path]::GetFullPath($State), [IO.Path]::GetFullPath($expectedState), [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected installation directories.' }
    $staging = Join-Path $AgentRoot 'staging'
    $resolvedSource = [IO.Path]::GetFullPath($Source)
    if (-not $resolvedSource.StartsWith($staging.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolvedSource) -ne 'updater' -or
        -not (Test-Path -LiteralPath (Join-Path $resolvedSource 'Vigiloom.Updater.exe') -PathType Leaf) -or
        -not (Test-Path -LiteralPath $current -PathType Container) -or
        (Test-Path -LiteralPath $next) -or (Test-Path -LiteralPath $previous)) { throw 'Invalid Updater replacement paths.' }
    $maintenance = [IO.File]::Open((Join-Path $AgentRoot 'repair.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
    try {
        [void](New-Item -ItemType Directory -Path $next)
        [void](New-Item -ItemType File -Path (Join-Path $next '.vigiloom-updater-stage'))
        Copy-Item -Path (Join-Path $resolvedSource '*') -Destination $next -Recurse -Force
        $service = Get-Service -Name 'Vigiloom Updater'
        $processId = (Get-CimInstance Win32_Service -Filter "Name='Vigiloom Updater'").ProcessId
        $process = $null
        if ($processId) {
            try { $process = [Diagnostics.Process]::GetProcessById($processId); $null = $process.Handle }
            catch [ArgumentException] { $process = $null }
        }
        if ($service.Status -ne 'Stopped') { Stop-Service -Name 'Vigiloom Updater'; $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60)) }
        if ($process) {
            try { if (-not $process.WaitForExit(60000)) { throw 'The previous Updater process did not exit.' } }
            finally { $process.Dispose() }
        }
        $ownership = [IO.File]::Open((Join-Path $AgentRoot 'updater.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
        try {
            Move-Item -LiteralPath $current -Destination $previous
            Move-Item -LiteralPath $next -Destination $current
        } finally { $ownership.Dispose() }
        Start-Service -Name 'Vigiloom Updater'
        $newProcessId = (Get-CimInstance Win32_Service -Filter "Name='Vigiloom Updater'").ProcessId
        if (-not $newProcessId) { throw 'The updated Updater did not start.' }
        for ($i = 0; $i -lt 10; $i++) {
            $serviceState = Get-CimInstance Win32_Service -Filter "Name='Vigiloom Updater'"
            if ($serviceState.State -ne 'Running' -or $serviceState.ProcessId -ne $newProcessId) { throw 'The updated Updater stopped.' }
            $recovery = & (Join-Path $current 'Vigiloom.Updater.exe') recovery-status $State
            if ($LASTEXITCODE -ne 0 -or $recovery -ne 'ready') { throw 'The updated Updater recovery is not ready.' }
            Start-Sleep -Seconds 1
        }
        Remove-Item -LiteralPath (Join-Path $current '.vigiloom-updater-stage') -Force
        Remove-Item -LiteralPath $previous -Recurse -Force
        Set-Status 'succeeded'
    } finally { $maintenance.Dispose() }
} catch {
    Set-Status ("failed " + $_.Exception.GetType().Name)
    throw
} finally {
    & schtasks.exe /Delete /TN "Vigiloom-Updater-$Operation" /F 2>$null | Out-Null
    Remove-Item -LiteralPath (Join-Path $State "replace-updater-$Operation.xml") -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
}
