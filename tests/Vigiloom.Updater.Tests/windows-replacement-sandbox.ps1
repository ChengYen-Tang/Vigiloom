param([string]$Shared = 'C:\VigiloomTest')
$ErrorActionPreference = 'Stop'
if ($env:USERNAME -ne 'WDAGUtilityAccount') { throw 'This test runs only inside Windows Sandbox.' }
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'The Sandbox test requires elevation.' }
$resultFile = Join-Path $Shared 'result.txt'
$base = Join-Path $env:ProgramFiles 'VigiloomAgent'
$agent = Join-Path $base 'Agent'
$updater = Join-Path $base 'Updater'
$state = Join-Path $env:ProgramData 'VigiloomAgent\Updater'
$operation = '0199123456787abc8def123456789abc'
$taskName = "Vigiloom-Updater-$operation"
$source = Join-Path $agent "staging\$operation\updater"
$unit = 'Vigiloom Updater'
$createdService = $false
$createdTask = $false
function Wait-TaskCleanup([string]$Name, [string]$Id) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        $saved = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try { & schtasks.exe /Query /TN $Name 2>&1 | Out-Null; $exists = $LASTEXITCODE -eq 0 }
        finally { $ErrorActionPreference = $saved }
        $xmlPath = Join-Path $state "replace-updater-$Id.xml"
        $scriptPath = Join-Path $state "replace-updater-$Id.ps1"
        if (-not $exists -and -not (Test-Path -LiteralPath $xmlPath) -and -not (Test-Path -LiteralPath $scriptPath)) { return }
        Start-Sleep -Milliseconds 500
    } while ($timer.Elapsed -lt [TimeSpan]::FromSeconds(15))
    throw 'Replacement task or temporary files were not cleaned.'
}
try {
    if ((Get-Service -Name $unit -ErrorAction SilentlyContinue) -or (Test-Path -LiteralPath $base) -or (Test-Path -LiteralPath $state)) {
        throw 'A Vigiloom installation already exists inside this Sandbox.'
    }
    [void](New-Item -ItemType Directory -Path $updater, $source, $state -Force)
    $fake = @'
using System;
using System.ServiceProcess;
public sealed class FakeUpdater : ServiceBase {
    public static void Main(string[] args) {
        if (args.Length > 0 && args[0] == "recovery-status") { Console.WriteLine("ready"); return; }
        Run(new FakeUpdater());
    }
    protected override void OnStart(string[] args) { }
    protected override void OnStop() { }
}
'@
    Add-Type -TypeDefinition $fake -Language CSharp -ReferencedAssemblies 'System.ServiceProcess.dll' `
        -OutputAssembly (Join-Path $updater 'Vigiloom.Updater.exe') -OutputType ConsoleApplication
    Copy-Item -LiteralPath (Join-Path $updater 'Vigiloom.Updater.exe') -Destination (Join-Path $source 'Vigiloom.Updater.exe')
    [IO.File]::WriteAllText((Join-Path $updater 'version.txt'), 'old')
    [IO.File]::WriteAllText((Join-Path $source 'version.txt'), 'new')
    $image = '"' + (Join-Path $updater 'Vigiloom.Updater.exe') + '"'
    $creation = & sc.exe create $unit 'binPath=' $image 'start=' 'demand' 'obj=' 'LocalSystem'
    if ($LASTEXITCODE -ne 0) { throw "Could not create test Updater service: $creation" }
    $createdService = $true
    Start-Service -Name $unit
    $oldPid = (Get-CimInstance Win32_Service -Filter "Name='Vigiloom Updater'").ProcessId
    $launcher = Join-Path $Shared 'harness\UpdaterLauncherTest.exe'
    & $launcher $agent $source $state $operation
    if ($LASTEXITCODE -ne 0) { throw 'Product replacement launcher failed.' }
    $createdTask = $true
    $statusFile = Join-Path $state 'updater-replacement.status'
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    $status = ''
    while ($deadline.Elapsed -lt [TimeSpan]::FromSeconds(90)) {
        if (Test-Path -LiteralPath $statusFile) { $status = [IO.File]::ReadAllText($statusFile) }
        if ($status -like "$operation succeeded*" -or $status -like "$operation failed*") { break }
        Start-Sleep -Milliseconds 500
    }
    if ($status -ne "$operation succeeded") { throw "Replacement did not succeed: $status" }
    Wait-TaskCleanup $taskName $operation
    $newPid = (Get-CimInstance Win32_Service -Filter "Name='Vigiloom Updater'").ProcessId
    if (-not $newPid -or $newPid -eq $oldPid -or [IO.File]::ReadAllText((Join-Path $updater 'version.txt')) -ne 'new' -or
        (Test-Path -LiteralPath (Join-Path $base 'Updater.previous')) -or (Test-Path -LiteralPath (Join-Path $base 'Updater.next'))) {
        throw 'Replacement did not start the new process or clean the previous version.'
    }
    $failedOperation = '0199123456787abc8def123456789abd'
    $failedSource = Join-Path $agent "staging\$failedOperation\updater"
    [void](New-Item -ItemType Directory -Path $failedSource -Force)
    [IO.File]::WriteAllText((Join-Path $failedSource 'Vigiloom.Updater.exe'), 'intentionally invalid executable')
    $taskName = "Vigiloom-Updater-$failedOperation"
    & $launcher $agent $failedSource $state $failedOperation
    if ($LASTEXITCODE -ne 0) { throw 'Product replacement launcher failed during failure test.' }
    $deadline.Restart()
    $status = ''
    while ($deadline.Elapsed -lt [TimeSpan]::FromSeconds(90)) {
        if (Test-Path -LiteralPath $statusFile) { $status = [IO.File]::ReadAllText($statusFile) }
        if ($status -like "$failedOperation failed*") { break }
        Start-Sleep -Milliseconds 500
    }
    if ($status -notlike "$failedOperation failed*" -or
        -not (Test-Path -LiteralPath (Join-Path $base 'Updater.previous\Vigiloom.Updater.exe') -PathType Leaf) -or
        [IO.File]::ReadAllText((Join-Path $updater 'Vigiloom.Updater.exe')) -ne 'intentionally invalid executable' -or
        (Get-Service -Name $unit).Status -ne 'Stopped') {
        throw "Failure did not preserve both versions without rollback: $status"
    }
    Wait-TaskCleanup $taskName $failedOperation
    [IO.File]::WriteAllText($resultFile, 'PASS: Windows scheduled replacement, cleanup, and failed update preservation')
} catch {
    [IO.File]::WriteAllText($resultFile, "FAIL: $($_.Exception.ToString())")
    throw
} finally {
    if ($createdService) {
        Stop-Service -Name $unit -ErrorAction SilentlyContinue
        & sc.exe delete $unit 2>$null | Out-Null
    }
    if ($createdTask) { & schtasks.exe /Delete /TN $taskName /F 2>$null | Out-Null }
}
