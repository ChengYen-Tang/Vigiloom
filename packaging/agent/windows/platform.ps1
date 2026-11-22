function Test-AgentWindowsVersion([version]$Version, [int]$ProductType) {
    if ($Version.Major -eq 6) {
        return $Version.Minor -in @(2, 3) -and $ProductType -in @(2, 3)
    }
    if ($Version.Major -eq 10) { return $Version.Build -ge 14393 }
    return $Version.Major -gt 10
}

function Get-AgentExecutableArchitecture([string]$Path) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $reader = [IO.BinaryReader]::new($stream)
        if ($stream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5a4d) { throw "Invalid Windows executable: $Path" }
        [void]$stream.Seek(0x3c, [IO.SeekOrigin]::Begin)
        $offset = $reader.ReadUInt32()
        if ($offset -gt $stream.Length - 6) { throw "Invalid Windows executable: $Path" }
        [void]$stream.Seek($offset, [IO.SeekOrigin]::Begin)
        if ($reader.ReadUInt32() -ne 0x4550) { throw "Invalid Windows executable: $Path" }
        switch ($reader.ReadUInt16()) {
            0x014c { return 'x86' }
            0x8664 { return 'x64' }
            default { throw "Unsupported Windows executable architecture: $Path" }
        }
    } finally { $stream.Dispose() }
}
