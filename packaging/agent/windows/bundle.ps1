param(
    [Parameter(Mandatory)][string]$AgentPublish,
    [Parameter(Mandatory)][string]$UpdaterPublish,
    [Parameter(Mandatory)][string]$TrustedRoot,
    [Parameter(Mandatory)][string]$RootSha256,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'platform.ps1')
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$agent = (Resolve-Path -LiteralPath $AgentPublish).ProviderPath
$updater = (Resolve-Path -LiteralPath $UpdaterPublish).ProviderPath
$root = (Resolve-Path -LiteralPath $TrustedRoot).ProviderPath
if ($RootSha256 -notmatch '^[0-9a-fA-F]{64}$' -or (Get-FileHash -LiteralPath $root -Algorithm SHA256).Hash -ne $RootSha256) {
    throw 'The trusted publisher root fingerprint does not match.'
}
foreach ($file in @((Join-Path $agent 'Vigiloom.Agent.exe'), (Join-Path $agent 'Vigiloom.Agent.dll'),
    (Join-Path $agent 'coreclr.dll'), (Join-Path $agent 'hostfxr.dll'),
    (Join-Path $updater 'Vigiloom.Updater.exe'), (Join-Path $updater 'Vigiloom.Updater.dll'),
    (Join-Path $updater 'coreclr.dll'), (Join-Path $updater 'hostfxr.dll'))) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw 'Self-contained Windows Agent and Updater publications are required.' }
}
$architecture = Get-AgentExecutableArchitecture (Join-Path $agent 'Vigiloom.Agent.exe')
if ((Get-AgentExecutableArchitecture (Join-Path $updater 'Vigiloom.Updater.exe')) -ne $architecture) {
    throw 'Agent and Updater must have the same Windows architecture.'
}
foreach ($directory in @($agent, $updater)) {
    if (Get-ChildItem -LiteralPath $directory -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } | Select-Object -First 1) {
        throw 'Published application directories cannot contain links.'
    }
}
if (-not (Test-Path -LiteralPath $OutputDirectory)) { [void](New-Item -ItemType Directory -Path $OutputDirectory) }
$output = (Resolve-Path -LiteralPath $OutputDirectory).ProviderPath.TrimEnd('\')
foreach ($directory in @($agent, $updater)) {
    if (($output + '\').StartsWith($directory.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The output directory must be outside the published applications.'
    }
}
$artifact = Join-Path $output "vigiloom-agent-win-$architecture.zip"
$stage = [IO.Path]::GetFullPath((Join-Path $output ('.vigiloom-bundle-' + [Guid]::NewGuid().ToString('N'))))
if (-not $stage.StartsWith($output + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid staging path.' }
$createdArtifact = $false
try {
    [void](New-Item -ItemType Directory -Path (Join-Path $stage 'payload\agent'), (Join-Path $stage 'payload\updater') -Force)
    Copy-Item -Path (Join-Path $agent '*') -Destination (Join-Path $stage 'payload\agent') -Recurse -Force
    Copy-Item -Path (Join-Path $updater '*') -Destination (Join-Path $stage 'payload\updater') -Recurse -Force
    Copy-Item -LiteralPath $root -Destination (Join-Path $stage 'payload\root.json')
    if ((Get-FileHash -LiteralPath (Join-Path $stage 'payload\root.json') -Algorithm SHA256).Hash -ne $RootSha256) {
        throw 'The copied publisher root fingerprint does not match.'
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install.ps1'), (Join-Path $PSScriptRoot 'platform.ps1') -Destination $stage
    $stream = [IO.File]::Open($artifact, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $createdArtifact = $true
    try {
        $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $false)
        try {
            Get-ChildItem -LiteralPath $stage -Recurse -File -Force | ForEach-Object {
                $name = $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
                [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, $name, [IO.Compression.CompressionLevel]::Optimal)
            }
        } finally { $zip.Dispose() }
    } finally { $stream.Dispose() }
    $createdArtifact = $false
    Write-Host "Created $artifact"
} finally {
    if ($createdArtifact -and (Test-Path -LiteralPath $artifact)) { Remove-Item -LiteralPath $artifact -Force }
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}
