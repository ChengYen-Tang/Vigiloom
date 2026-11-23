param(
    [ValidateSet(30, 100, 300, 500)][int]$Agents = 30,
    [ValidateRange(1, 86400)][int]$Seconds = 300,
    [string]$SdkVersion = '',
    [switch]$IncludeServices,
    [switch]$Collector,
    [switch]$IncludeQueries
)

$ErrorActionPreference = 'Stop'
if ($Collector -and -not $IncludeServices) { throw 'Collector requires -IncludeServices.' }
if (-not $env:VIGILOOM_TEST_SQL_CONNECTION -or -not $env:VIGILOOM_TEST_SQL_ALLOW_SOURCE) {
    throw 'Set VIGILOOM_TEST_SQL_CONNECTION and VIGILOOM_TEST_SQL_ALLOW_SOURCE for a dedicated test SQL Server.'
}
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$scratch = Join-Path $root '.codex-agent-capacity'
$fixture = Join-Path $root 'tests/Vigiloom.Web.Tests/ManualAgentCapacityTests.cs'
$source = Join-Path $PSScriptRoot 'AgentLoadFixture.cs.txt'
if ((Test-Path -LiteralPath $scratch) -or (Test-Path -LiteralPath $fixture)) { throw 'Capacity scratch or fixture path is already in use.' }
$sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
$oldTemp = $env:TEMP; $oldTmp = $env:TMP; $oldTmpDir = $env:TMPDIR
$oldSize = $env:VIGILOOM_CAPACITY_SIZE; $oldDuration = $env:VIGILOOM_DURATION_SECONDS
$oldSender = $env:VIGILOOM_CAPACITY_SENDER
$oldServices = $env:VIGILOOM_CAPACITY_INCLUDE_SERVICES
$oldCollector = $env:VIGILOOM_CAPACITY_COLLECTOR
$oldQueries = $env:VIGILOOM_CAPACITY_QUERIES
$copied = $false
New-Item -ItemType Directory -Path $scratch | Out-Null
try {
    $env:TEMP = $scratch; $env:TMP = $scratch; $env:TMPDIR = $scratch
    $env:VIGILOOM_CAPACITY_SIZE = "$Agents"; $env:VIGILOOM_DURATION_SECONDS = "$Seconds"
    $env:VIGILOOM_CAPACITY_INCLUDE_SERVICES = if ($IncludeServices) { '1' } else { '0' }
    $env:VIGILOOM_CAPACITY_COLLECTOR = if ($Collector) { '1' } else { '0' }
    $env:VIGILOOM_CAPACITY_QUERIES = if ($IncludeQueries) { '1' } else { '0' }
    if ($SdkVersion) {
        @{ sdk = @{ version = $SdkVersion; rollForward = 'disable' } } | ConvertTo-Json -Compress | Set-Content -Encoding utf8 (Join-Path $scratch 'global.json')
    }
    Push-Location $scratch
    try {
        dotnet build (Join-Path $root 'tools/Vigiloom.CapacitySender/Vigiloom.CapacitySender.csproj') -c Release -p:UseSharedCompilation=false -v:q
        if ($LASTEXITCODE -ne 0) { throw 'Capacity sender build failed.' }
        $env:VIGILOOM_CAPACITY_SENDER = Join-Path $root 'tools/Vigiloom.CapacitySender/bin/Release/net10.0/Vigiloom.CapacitySender.dll'
        Copy-Item -LiteralPath $source -Destination $fixture
        $copied = $true
        $output = Join-Path $scratch 'result.log'
        dotnet test (Join-Path $root 'tests/Vigiloom.Web.Tests/Vigiloom.Web.Tests.csproj') -c Release -p:UseSharedCompilation=false --filter 'FullyQualifiedName~ManualAgentCapacityTests' --logger 'console;verbosity=detailed' *> $output
        if ($LASTEXITCODE -ne 0) {
            Select-String -LiteralPath $output -Pattern '"agents":|OTLP:|QUERIES:|SqlException|deadlocked|Timeout expired|Error Number|Agent device lock|InvalidOperationException|HttpRequestException|Exception|Error Message|錯誤訊息|Stack Trace' -Context 0,3 | Select-Object -First 30 | ForEach-Object { $_.ToString() }
            Get-Content -LiteralPath $output -Tail 60
            throw 'Capacity run failed.'
        }
        $summary = @(Select-String -LiteralPath $output -Pattern '"agents":')
        $sqlVersion = @(Select-String -LiteralPath $output -Pattern 'SQL:')
        if ($summary.Count -ne 1 -or $sqlVersion.Count -ne 1) { throw 'Capacity test produced no unique result; check test discovery.' }
        $summary[0].Line; $sqlVersion[0].Line
        if ($IncludeQueries) {
            $querySummary = @(Select-String -LiteralPath $output -Pattern 'QUERIES:')
            if ($querySummary.Count -ne 1) { throw 'Capacity test produced no unique query result.' }
            $querySummary[0].Line
        }
        if ($IncludeServices) {
            $serviceSummary = @(Select-String -LiteralPath $output -Pattern 'OTLP:')
            if ($serviceSummary.Count -ne 1) { throw 'Capacity test produced no unique OTLP result.' }
            $serviceSummary[0].Line
            if ($Collector) {
                @(Select-String -LiteralPath $output -Pattern 'Collector:|Collector drain seconds:') | ForEach-Object { $_.Line }
            }
        }
    }
    finally { Pop-Location }
}
finally {
    $env:TEMP = $oldTemp; $env:TMP = $oldTmp; $env:TMPDIR = $oldTmpDir
    $env:VIGILOOM_CAPACITY_SIZE = $oldSize; $env:VIGILOOM_DURATION_SECONDS = $oldDuration
    $env:VIGILOOM_CAPACITY_SENDER = $oldSender
    $env:VIGILOOM_CAPACITY_INCLUDE_SERVICES = $oldServices
    $env:VIGILOOM_CAPACITY_COLLECTOR = $oldCollector
    $env:VIGILOOM_CAPACITY_QUERIES = $oldQueries
    if ($copied -and (Test-Path -LiteralPath $fixture)) {
        if ((Get-FileHash -LiteralPath $fixture -Algorithm SHA256).Hash -ne $sourceHash) { throw "Fixture changed during the run; retain $fixture for inspection." }
        Remove-Item -LiteralPath $fixture -Force
        # Prevent a later --no-build test run from discovering the temporary fixture.
        foreach ($name in @('Vigiloom.Web.Tests.dll', 'Vigiloom.Web.Tests.pdb')) {
            $path = Join-Path $root "tests/Vigiloom.Web.Tests/bin/Release/net10.0/$name"
            if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
        }
    }
    $manifests = @(Get-ChildItem -LiteralPath (Join-Path $scratch 'VigiloomSqlTests') -Recurse -Filter run.json -ErrorAction SilentlyContinue)
    if ($manifests.Count -gt 0) { throw "SQL test cleanup did not finish; preserve $scratch and recover its run manifest using tests/Vigiloom.Testing.SqlServer." }
    if (Test-Path -LiteralPath (Join-Path $scratch 'capacity-collector.txt')) { throw "Collector cleanup did not finish; preserve $scratch and inspect capacity-collector.txt." }
    if ((Resolve-Path -LiteralPath $scratch).Path -ne [IO.Path]::GetFullPath($scratch)) { throw 'Unexpected capacity scratch path.' }
    Remove-Item -LiteralPath $scratch -Recurse -Force
}
