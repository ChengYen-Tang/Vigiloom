param([Parameter(Mandatory)][string]$Bundle, [Parameter(Mandatory)][string]$TestCertificate)
$ErrorActionPreference = 'Stop'

if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'This test requires a disposable GitHub-hosted runner.'
}
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator is required.' }
$bundlePath = (Resolve-Path -LiteralPath $Bundle).ProviderPath
$certificatePath = (Resolve-Path -LiteralPath $TestCertificate).ProviderPath
$programBase = [IO.Path]::GetFullPath([Environment]::GetFolderPath('ProgramFiles')).TrimEnd('\')
$dataBase = [IO.Path]::GetFullPath([Environment]::GetFolderPath('CommonApplicationData')).TrimEnd('\')
if ([IO.Path]::GetFullPath($env:ProgramFiles).TrimEnd('\') -ne $programBase -or
    [IO.Path]::GetFullPath($env:ProgramData).TrimEnd('\') -ne $dataBase) { throw 'Unexpected installation roots.' }
$programRoot = [IO.Path]::GetFullPath((Join-Path $programBase 'VigiloomAgent'))
$stateRoot = [IO.Path]::GetFullPath((Join-Path $dataBase 'VigiloomAgent'))
foreach ($path in @($programRoot, $stateRoot)) {
    if (Test-Path -LiteralPath $path) { throw 'A Vigiloom installation or state already exists.' }
}
foreach ($name in @('Vigiloom Agent', 'Vigiloom Updater')) {
    if (Get-Service -Name $name -ErrorAction SilentlyContinue) { throw 'A Vigiloom service already exists.' }
}
foreach ($source in @('Vigiloom.Agent', 'Vigiloom.Updater')) {
    if ([Diagnostics.EventLog]::SourceExists($source)) { throw 'A Vigiloom event source already exists.' }
}

$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($certificatePath)
$store = [Security.Cryptography.X509Certificates.X509Store]::new('Root', 'LocalMachine')
$store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
$addedCertificate = $false
$cleanupAllowed = $true
$startedAt = Get-Date
try {
    if ($store.Certificates.Find('FindByThumbprint', $certificate.Thumbprint, $false).Count -ne 0) {
        throw 'The test CA is already present.'
    }
    $store.Add($certificate)
    $addedCertificate = $true
    function Read-Host {
        param([string]$Prompt, [switch]$AsSecureString)
        $value = [Console]::In.ReadLine()
        if ($null -eq $value) { throw 'Installer input ended unexpectedly.' }
        if ($AsSecureString) { return ConvertTo-SecureString $value -AsPlainText -Force }
        return $value
    }
    & (Join-Path $bundlePath 'install.ps1') *>&1 | Out-Null
    $cleanupAllowed = $false
    [Console]::Out.WriteLine('READY')
    [Console]::Out.Flush()
    if ([Console]::In.ReadLine() -eq 'CLEANUP') { $cleanupAllowed = $true }
}
finally {
    if (-not $cleanupAllowed) {
        $store.Close()
        $certificate.Dispose()
        throw 'The parent did not confirm safe cleanup; the test installation was retained.'
    }
    try {
        # Only emit fixed, non-secret status messages; never dump the Windows event log.
        try {
            $events = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = @('Vigiloom.Agent', 'Vigiloom.Updater'); StartTime = $startedAt } -ErrorAction SilentlyContinue | Select-Object -First 100
            foreach ($event in $events) {
                if ($event.Message -match '(?m)^\s*((?:Agent installation enrollment (?:completed\.|is pending: [A-Za-z]+\.)|Agent update handoff (?:status|unavailable): [A-Za-z]+\.|Updater (?:request result: [A-Za-z]+, [A-Za-z]*\.|request failed: [A-Za-z]+\.|recovery state: [A-Za-z]+\.)))\s*$') {
                    [Console]::Error.WriteLine("WINDOWS_TEST $($Matches[1])")
                }
            }
        } catch { }
        . (Join-Path $bundlePath 'service-operations.ps1')
        . (Join-Path $bundlePath 'native.ps1')
        # Stop the controller first; it must not restart the Agent during cleanup.
        foreach ($name in @('Vigiloom Updater', 'Vigiloom Agent')) {
            if (Get-Service -Name $name -ErrorAction SilentlyContinue) { Stop-VigiloomService $name }
        }
        foreach ($name in @('Vigiloom Updater', 'Vigiloom Agent')) {
            $service = Get-CimInstance Win32_Service -Filter "Name='$name'"
            if ($service -and $service.ProcessId -ne 0) { throw 'A test service is still running; state was retained.' }
        }
        foreach ($name in @('Vigiloom Agent', 'Vigiloom Updater')) {
            if (Get-Service -Name $name -ErrorAction SilentlyContinue) { [Vigiloom.Setup.Native]::Delete($name) }
        }
        foreach ($path in @($programRoot, $stateRoot)) {
            if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
        }
        foreach ($source in @('Vigiloom.Agent', 'Vigiloom.Updater')) {
            if ([Diagnostics.EventLog]::SourceExists($source)) { [Diagnostics.EventLog]::DeleteEventSource($source) }
        }
    }
    finally {
        if ($addedCertificate) { $store.Remove($certificate) }
        $store.Close()
        $certificate.Dispose()
    }
}
