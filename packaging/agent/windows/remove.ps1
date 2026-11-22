$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run as Administrator.' }
. (Join-Path $PSScriptRoot 'native.ps1')
. (Join-Path $PSScriptRoot 'service-operations.ps1')
$programRoot = Join-Path $env:ProgramFiles 'VigiloomAgent'
$agentRoot = Join-Path $programRoot 'Agent'
$maintenance = [IO.File]::Open((Join-Path $agentRoot 'repair.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
$stateRoot = Join-Path $env:ProgramData 'VigiloomAgent'
$backup = Join-Path $stateRoot 'Updater\service-backup.json'
$agentName = 'Vigiloom Agent'
$updaterName = 'Vigiloom Updater'
$agentRegistry = "HKLM:\SYSTEM\CurrentControlSet\Services\$agentName"
$updaterRegistry = "HKLM:\SYSTEM\CurrentControlSet\Services\$updaterName"
if (-not (Test-Path -LiteralPath $agentRegistry) -or -not (Test-Path -LiteralPath $updaterRegistry)) {
    throw 'Both Vigiloom services are required; no services were removed.'
}
$expectedUpdater = '"' + (Join-Path $programRoot 'Updater\Vigiloom.Updater.exe') + '"'
$prefix = '"' + $agentRoot.TrimEnd('\') + '\'
function Get-ServiceConfiguration {
    $agent = Get-ItemProperty -LiteralPath $agentRegistry
    $updater = Get-ItemProperty -LiteralPath $updaterRegistry
    if (-not $agent.ImagePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not $agent.ImagePath.EndsWith('\Vigiloom.Agent.exe"', [StringComparison]::OrdinalIgnoreCase) -or
        $updater.ImagePath -ne $expectedUpdater -or $agent.ObjectName -ne 'NT SERVICE\Vigiloom Agent' -or
        $updater.ObjectName -ne 'LocalSystem') { throw 'Unexpected service configuration; no services were removed.' }
    return @{ AgentImage = $agent.ImagePath; AgentEnvironment = @($agent.Environment); UpdaterEnvironment = @($updater.Environment) }
}
[void](Get-ServiceConfiguration)

Stop-VigiloomService $updaterName
$lock = [IO.File]::Open((Join-Path $agentRoot 'updater.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    $data = Get-ServiceConfiguration
    $data | ConvertTo-Json | Set-Content -LiteralPath $backup -Encoding UTF8
    Stop-VigiloomService $agentName
    [Vigiloom.Setup.Native]::Delete($agentName)
    [Vigiloom.Setup.Native]::Delete($updaterName)
} finally { $lock.Dispose() }
Write-Host 'Vigiloom services were removed. Identity, queued data, settings and installed versions were retained.'
Write-Host 'Run restore.ps1 from the installed Tools directory to restore this installation.'
Write-Host 'Removing local services does not revoke the Agent credential in the central platform.'
} finally { $maintenance.Dispose() }
