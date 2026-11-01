param(
    [string]$Go = 'go',
    [ValidateSet('windows','linux')][string]$TargetOS = $(if($IsWindows){'windows'}else{'linux'}),
    [ValidateSet('amd64','386')][string]$TargetArch = 'amd64',
    [string]$OutputDirectory = ''
)
$ErrorActionPreference='Stop'
$repoRoot=Split-Path $PSScriptRoot -Parent
$goCommand=(Get-Command $Go -ErrorAction Stop).Source
if((& $goCommand version) -notmatch 'go1\.27\.3 '){throw 'Go 1.27.3 is required to build the pinned NDT7 adapter.'}
if(!$OutputDirectory){$OutputDirectory=Join-Path $repoRoot 'artifacts/ndt7'}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$previousOS=$env:GOOS; $previousArch=$env:GOARCH; $previousCgo=$env:CGO_ENABLED
try{
    $env:GOOS=$TargetOS; $env:GOARCH=$TargetArch; $env:CGO_ENABLED='0'
    $name=if($TargetOS -eq 'windows'){'vigiloom-ndt7.exe'}else{'vigiloom-ndt7'}
    & $goCommand -C (Join-Path $repoRoot 'tools/ndt7') build -mod=readonly -trimpath -ldflags '-s -w' -o (Join-Path $OutputDirectory $name) .
    if($LASTEXITCODE -ne 0){throw 'NDT7 build failed.'}
    $checksum=(Get-FileHash -LiteralPath (Join-Path $OutputDirectory $name) -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath (Join-Path $OutputDirectory "$name.sha256") -Value "$checksum  $name" -Encoding utf8NoBOM
} finally { $env:GOOS=$previousOS; $env:GOARCH=$previousArch; $env:CGO_ENABLED=$previousCgo }
