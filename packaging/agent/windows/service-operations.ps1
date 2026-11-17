function Stop-VigiloomService([string]$name) {
    $service = Get-Service -Name $name
    $process = $null
    try {
        $pidValue = (Get-CimInstance Win32_Service -Filter "Name='$name'").ProcessId
        if ($pidValue) {
            try { $process = [Diagnostics.Process]::GetProcessById([int]$pidValue); [void]$process.Handle }
            catch [ArgumentException] { $process = $null }
        }
        if ($service.Status -ne 'Stopped') {
            if ($service.Status -ne 'StopPending') { $service.Stop() }
            $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
        }
        if ($process -and -not $process.WaitForExit(60000)) { throw "The $name process has not exited." }
    } finally { if ($process) { $process.Dispose() }; $service.Dispose() }
}
