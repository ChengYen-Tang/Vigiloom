$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run as Administrator.' }
. (Join-Path $PSScriptRoot 'native.ps1')
$programRoot = Join-Path $env:ProgramFiles 'VigiloomAgent'
$agentRoot = Join-Path $programRoot 'Agent'
$updaterExe = Join-Path $programRoot 'Updater\Vigiloom.Updater.exe'
$updaterState = Join-Path $env:ProgramData 'VigiloomAgent\Updater'
$backup = Join-Path $updaterState 'service-backup.json'
if (-not (Test-Path -LiteralPath $backup -PathType Leaf) -or -not (Test-Path -LiteralPath $updaterExe -PathType Leaf)) {
    throw 'The retained service configuration or Updater was not found.'
}
$saved = Get-Content -LiteralPath $backup -Raw | ConvertFrom-Json
$prefix = '"' + $agentRoot.TrimEnd('\') + '\'
if (-not $saved.AgentImage.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
    -not $saved.AgentImage.EndsWith('\Vigiloom.Agent.exe"', [StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-Path -LiteralPath $saved.AgentImage.Trim('"') -PathType Leaf) -or
    @($saved.UpdaterEnvironment) -notcontains "Vigiloom__Updater__StateDirectory=$updaterState") {
    throw 'The retained service configuration is invalid.'
}
$agent = Get-Service -Name 'Vigiloom Agent' -ErrorAction SilentlyContinue
if ($agent) {
    try { if ($agent.Status -ne 'Stopped') { throw 'Agent is already running; stop it before restoring services.' } }
    finally { $agent.Dispose() }
}
function Restore-ServiceDefinition([string]$name, [string]$image, [string]$account, [bool]$serviceSid, [string[]]$environment) {
    $key = "HKLM:\SYSTEM\CurrentControlSet\Services\$name"
    if (Test-Path -LiteralPath $key) {
        $current = Get-ItemProperty -LiteralPath $key
        if ($current.ImagePath -ne $image -or $current.ObjectName -ne $(if ($account) { $account } else { 'LocalSystem' })) {
            throw "A different service already exists: $name"
        }
    } else {
        [Vigiloom.Setup.Native]::Create($name, $image, $account, $serviceSid, $true)
    }
    Set-Service -Name $name -StartupType Manual
    New-ItemProperty -LiteralPath $key -Name Environment -PropertyType MultiString -Value $environment -Force | Out-Null
}
Restore-ServiceDefinition 'Vigiloom Agent' $saved.AgentImage 'NT SERVICE\Vigiloom Agent' $true @($saved.AgentEnvironment)
Restore-ServiceDefinition 'Vigiloom Updater' ('"' + $updaterExe + '"') '' $false @($saved.UpdaterEnvironment)
Set-Service -Name 'Vigiloom Updater' -StartupType Automatic
Start-Service -Name 'Vigiloom Updater'
$deadline = [DateTime]::UtcNow.AddMinutes(4)
do {
    $service = Get-Service -Name 'Vigiloom Updater'
    try { if ($service.Status -ne 'Running') { throw 'Updater is not running; Agent was not enabled.' } }
    finally { $service.Dispose() }
    $status = & $updaterExe recovery-status $updaterState
    $result = $LASTEXITCODE
    if ($result -eq 0 -and $status -eq 'ready') { break }
    if ($result -ne 1 -or $status -ne 'pending') { throw 'Updater recovery failed or its journal cannot be read; Agent was not enabled.' }
    if ([DateTime]::UtcNow -ge $deadline) { throw 'Updater recovery did not complete in time; Agent was not enabled.' }
    Start-Sleep -Seconds 2
} while ($true)
$service = Get-Service -Name 'Vigiloom Updater'
try { if ($service.Status -ne 'Running') { throw 'Updater stopped during recovery; Agent was not enabled.' } }
finally { $service.Dispose() }
Set-Service -Name 'Vigiloom Agent' -StartupType Automatic
Start-Service -Name 'Vigiloom Agent'
Write-Host 'Vigiloom services were restored with the retained Agent identity and data.'
