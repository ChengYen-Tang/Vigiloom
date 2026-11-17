$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run as Administrator.' }
. (Join-Path $PSScriptRoot 'platform.ps1')
. (Join-Path $PSScriptRoot 'service-operations.ps1')
$programRoot = Join-Path $env:ProgramFiles 'VigiloomAgent'
$agentRoot = Join-Path $programRoot 'Agent'
$current = Join-Path $programRoot 'Updater'
$previous = Join-Path $programRoot 'Updater.previous'
$next = Join-Path $programRoot 'Updater.next'
$updaterState = Join-Path $env:ProgramData 'VigiloomAgent\Updater'
$source = Join-Path $PSScriptRoot 'payload\updater'
$maintenance = [IO.File]::Open((Join-Path $agentRoot 'repair.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    $registry = 'HKLM:\SYSTEM\CurrentControlSet\Services\Vigiloom Updater'
    $service = Get-ItemProperty -LiteralPath $registry
    if ($service.ImagePath -ne ('"' + (Join-Path $current 'Vigiloom.Updater.exe') + '"') -or $service.ObjectName -ne 'LocalSystem') {
        throw 'Unexpected Updater service configuration.'
    }
    foreach ($path in @($current, $previous, $next)) {
        if (Test-Path -LiteralPath $path) {
            $item = Get-Item -LiteralPath $path -Force
            if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Unexpected Updater directory.' }
        }
    }
    if ((Test-Path -LiteralPath $previous) -and -not (Test-Path -LiteralPath (Join-Path $previous 'Vigiloom.Updater.exe') -PathType Leaf)) { throw 'The previous Updater directory is invalid.' }
    if ((Test-Path -LiteralPath $next) -and -not (Test-Path -LiteralPath (Join-Path $next '.vigiloom-updater-stage') -PathType Leaf)) { throw 'An unexpected staging directory already exists.' }
    if (Test-Path -LiteralPath $previous) {
        Stop-VigiloomService 'Vigiloom Updater'
        $lock = [IO.File]::Open((Join-Path $agentRoot 'updater.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        try {
            if (Test-Path -LiteralPath $current) {
                if (Test-Path -LiteralPath $next) { Remove-Item -LiteralPath $next -Recurse -Force }
                Move-Item -LiteralPath $current -Destination $next
            }
            Move-Item -LiteralPath $previous -Destination $current
        } finally { $lock.Dispose() }
        Start-Service -Name 'Vigiloom Updater'
        if (Test-Path -LiteralPath $next) { Remove-Item -LiteralPath $next -Recurse -Force }
        throw 'The previous Updater was restored after an interrupted repair. Run this command again to retry.'
    }
    $installedExe = Join-Path $current 'Vigiloom.Updater.exe'
    $sourceExe = Join-Path $source 'Vigiloom.Updater.exe'
    if (-not (Test-Path -LiteralPath $installedExe -PathType Leaf) -or -not (Test-Path -LiteralPath $sourceExe -PathType Leaf) -or
        (Get-AgentExecutableArchitecture $installedExe) -ne (Get-AgentExecutableArchitecture $sourceExe)) { throw 'The installed and bundled Updater architectures do not match.' }
    & $sourceExe check-runtime
    if ($LASTEXITCODE -ne 0) { throw 'The bundled Updater cannot start.' }
    if (Test-Path -LiteralPath $next) { Remove-Item -LiteralPath $next -Recurse -Force }
    [void](New-Item -ItemType Directory -Path $next)
    [void](New-Item -ItemType File -Path (Join-Path $next '.vigiloom-updater-stage'))
    Copy-Item -Path (Join-Path $source '*') -Destination $next -Recurse -Force
    Stop-VigiloomService 'Vigiloom Updater'
    $lock = [IO.File]::Open((Join-Path $agentRoot 'updater.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        Move-Item -LiteralPath $current -Destination $previous
        Move-Item -LiteralPath $next -Destination $current
    } finally { $lock.Dispose() }
    $ready = $false
    try {
        Start-Service -Name 'Vigiloom Updater'
        $processId = (Get-CimInstance Win32_Service -Filter "Name='Vigiloom Updater'").ProcessId
        if (-not $processId) { throw 'Updater did not start.' }
        $started = [Diagnostics.Stopwatch]::StartNew()
        while ($started.Elapsed -lt [TimeSpan]::FromMinutes(4)) {
            $service = Get-CimInstance Win32_Service -Filter "Name='Vigiloom Updater'"
            if ($service.State -ne 'Running' -or $service.ProcessId -ne $processId) { throw 'Updater stopped during recovery.' }
            $status = & (Join-Path $current 'Vigiloom.Updater.exe') recovery-status $updaterState
            $result = $LASTEXITCODE
            if ($result -eq 0 -and $status -eq 'ready' -and $started.Elapsed -ge [TimeSpan]::FromSeconds(10)) {
                $service = Get-CimInstance Win32_Service -Filter "Name='Vigiloom Updater'"
                if ($service.State -ne 'Running' -or $service.ProcessId -ne $processId) { throw 'Updater stopped during recovery.' }
                $ready = $true
                break
            }
            if (-not (($result -eq 0 -and $status -eq 'ready') -or ($result -eq 1 -and $status -eq 'pending'))) { throw 'Updater recovery failed.' }
            Start-Sleep -Seconds 2
        }
        if (-not $ready) { throw 'Updater recovery timed out.' }
    } catch {
        Stop-VigiloomService 'Vigiloom Updater'
        $lock = [IO.File]::Open((Join-Path $agentRoot 'updater.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        try {
            Move-Item -LiteralPath $current -Destination $next
            Move-Item -LiteralPath $previous -Destination $current
        } finally { $lock.Dispose() }
        Start-Service -Name 'Vigiloom Updater'
        Remove-Item -LiteralPath $next -Recurse -Force
        throw 'The replacement failed; the previous Updater was restored.'
    }
    Remove-Item -LiteralPath (Join-Path $current '.vigiloom-updater-stage') -Force
    Remove-Item -LiteralPath $previous -Recurse -Force
    Write-Host 'The Updater was replaced. Agent identity, queued data, trust, settings and recovery journal were retained.'
} finally { $maintenance.Dispose() }
