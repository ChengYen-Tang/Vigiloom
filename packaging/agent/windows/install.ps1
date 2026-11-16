# Requires Windows PowerShell 5.1 started as Administrator.
$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Start Windows PowerShell as Administrator before running this installer.'
}
$version = [Environment]::OSVersion.Version
if ($version.Major -lt 6 -or ($version.Major -eq 6 -and $version.Minor -lt 2)) {
    throw 'This Agent requires a Windows version supported by .NET 10.'
}
if ($version.Major -eq 6 -and (Get-CimInstance Win32_OperatingSystem).ProductType -eq 1) {
    throw 'This Windows client version is not supported by .NET 10.'
}
if ($version.Major -eq 6) {
    Write-Warning '.NET 10 support on Windows Server 2012 and 2012 R2 requires Extended Security Updates. Check the Microsoft support policy for this system.'
}

$bundle = $PSScriptRoot
. (Join-Path $bundle 'platform.ps1')
$agentSource = Join-Path $bundle 'payload\agent'
$updaterSource = Join-Path $bundle 'payload\updater'
$rootSource = Join-Path $bundle 'payload\root.json'
$programRoot = Join-Path $env:ProgramFiles 'VigiloomAgent'
$stateRoot = Join-Path $env:ProgramData 'VigiloomAgent'
$agentRoot = Join-Path $programRoot 'Agent\initial'
$updaterRoot = Join-Path $programRoot 'Updater'
$agentState = Join-Path $stateRoot 'Agent'
$updaterState = Join-Path $stateRoot 'Updater'
$agentName = 'Vigiloom Agent'
$updaterName = 'Vigiloom Updater'
foreach ($file in @((Join-Path $agentSource 'Vigiloom.Agent.exe'), (Join-Path $updaterSource 'Vigiloom.Updater.exe'), $rootSource)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw 'The installer bundle is incomplete.' }
}
$architecture = Get-AgentExecutableArchitecture (Join-Path $agentSource 'Vigiloom.Agent.exe')
if ((Get-AgentExecutableArchitecture (Join-Path $updaterSource 'Vigiloom.Updater.exe')) -ne $architecture) {
    throw 'Agent and Updater must have the same Windows architecture.'
}
if ($architecture -eq 'x64' -and -not [Environment]::Is64BitOperatingSystem) {
    throw 'This x64 Agent package requires a 64-bit Windows system; use the x86 package instead.'
}
if ((Test-Path -LiteralPath $programRoot) -or (Test-Path -LiteralPath $stateRoot) -or
    (Get-Service -Name $agentName -ErrorAction SilentlyContinue) -or (Get-Service -Name $updaterName -ErrorAction SilentlyContinue)) {
    throw 'An existing Vigiloom Agent installation or state was found. It will not be overwritten.'
}

$serverText = Read-Host 'Vigiloom server HTTPS URL'
$server = $null
if (-not [Uri]::TryCreate($serverText, [UriKind]::Absolute, [ref]$server) -or $server.Scheme -ne 'https' -or
    $server.UserInfo -or $server.Query -or $server.Fragment) { throw 'Enter an HTTPS URL without credentials, query or fragment.' }
$serverText = $server.AbsoluteUri.TrimEnd('/') + '/'
$device = Read-Host 'Device ID'
if ($device -notmatch '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-7[0-9a-fA-F]{3}-[89aAbB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}$') {
    throw 'Enter the device UUID shown in Vigiloom.'
}
$fingerprint = Read-Host 'Publisher root SHA-256 fingerprint'
if ($fingerprint -notmatch '^[0-9a-fA-F]{64}$' -or (Get-FileHash -LiteralPath $rootSource -Algorithm SHA256).Hash -ne $fingerprint) {
    throw 'The trusted publisher root fingerprint does not match.'
}
$secureEnrollment = Read-Host 'One-time Agent enrollment value' -AsSecureString
$address = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureEnrollment)
try { $enrollment = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($address) }
finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($address); $secureEnrollment.Dispose() }
if ($enrollment -cnotmatch '^vle_[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-7[0-9a-fA-F]{3}-[89aAbB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}\.[a-zA-Z0-9_-]{43}$') {
    throw 'Enter a valid one-time Agent enrollment value.'
}

if (-not ('Vigiloom.Setup.Native' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Vigiloom.Setup {
    public static class Native {
        [StructLayout(LayoutKind.Sequential)] struct Luid { public uint Low; public int High; }
        [StructLayout(LayoutKind.Sequential)] struct TokenPrivileges { public uint Count; public Luid Id; public uint Attributes; }
        [StructLayout(LayoutKind.Sequential)] struct ServiceSidInfo { public uint Type; }
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool CloseHandle(IntPtr handle);
        [DllImport("advapi32.dll", SetLastError=true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool LookupPrivilegeValue(string system, string name, out Luid id);
        [DllImport("advapi32.dll", SetLastError=true)] static extern bool AdjustTokenPrivileges(IntPtr token, bool disable, ref TokenPrivileges privileges, uint length, IntPtr previous, IntPtr returned);
        [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr OpenSCManager(string machine, string database, uint access);
        [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr OpenService(IntPtr manager, string name, uint access);
        [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr CreateService(IntPtr manager, string name, string display, uint access, uint type, uint start, uint error, string binary, string group, IntPtr tag, string dependencies, string account, string password);
        [DllImport("advapi32.dll", SetLastError=true)] static extern bool ChangeServiceConfig2(IntPtr service, uint level, ref ServiceSidInfo info);
        [DllImport("advapi32.dll", SetLastError=true)] static extern bool DeleteService(IntPtr service);
        [DllImport("advapi32.dll", SetLastError=true)] static extern bool CloseServiceHandle(IntPtr service);

        static void Check(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error()); }
        public static void EnableRestorePrivilege() {
            IntPtr token;
            Check(OpenProcessToken(GetCurrentProcess(), 0x28, out token));
            try {
                Luid id; Check(LookupPrivilegeValue(null, "SeRestorePrivilege", out id));
                var privileges = new TokenPrivileges { Count = 1, Id = id, Attributes = 2 };
                Check(AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero));
                int error = Marshal.GetLastWin32Error();
                if (error != 0) throw new Win32Exception(error);
            } finally { CloseHandle(token); }
        }
        public static void Create(string name, string binary, string account, bool serviceSid) {
            var manager = OpenSCManager(null, null, 2);
            if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try {
                // Windows PowerShell converts a null string argument to an empty string.
                var service = CreateService(manager, name, name, 0xF01FF, 0x10, 2, 1, binary, null, IntPtr.Zero, null,
                    string.IsNullOrEmpty(account) ? null : account, null);
                if (service == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                try {
                    if (serviceSid) { var info = new ServiceSidInfo { Type = 1 }; Check(ChangeServiceConfig2(service, 5, ref info)); }
                } catch { DeleteService(service); throw; }
                finally { CloseServiceHandle(service); }
            } finally { CloseServiceHandle(manager); }
        }
        public static void Delete(string name) {
            var manager = OpenSCManager(null, null, 1);
            if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try {
                var service = OpenService(manager, name, 0x10000);
                if (service == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                try { Check(DeleteService(service)); } finally { CloseServiceHandle(service); }
            } finally { CloseServiceHandle(manager); }
        }
    }
}
'@
}

function New-ProtectedDirectory([string]$Path, [Security.Principal.SecurityIdentifier]$Owner,
    [Security.Principal.SecurityIdentifier[]]$FullAccess, [Security.Principal.SecurityIdentifier]$Traverse) {
    $security = New-Object Security.AccessControl.DirectorySecurity
    $security.SetAccessRuleProtection($true, $false)
    $security.SetOwner($Owner)
    $inherit = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit
    foreach ($sid in $FullAccess) {
        $rule = New-Object Security.AccessControl.FileSystemAccessRule($sid, 'FullControl', $inherit, 'None', 'Allow')
        $security.AddAccessRule($rule)
    }
    if ($Traverse) {
        $rule = New-Object Security.AccessControl.FileSystemAccessRule($Traverse, 'ReadAndExecute', 'None', 'None', 'Allow')
        $security.AddAccessRule($rule)
    }
    [void][IO.Directory]::CreateDirectory($Path, $security)
}

$agentCreated = $false
$updaterCreated = $false
$programCreated = $false
$stateCreated = $false
$activated = $false
try {
    [Vigiloom.Setup.Native]::EnableRestorePrivilege()
    [Vigiloom.Setup.Native]::Create($agentName, ('"' + (Join-Path $agentRoot 'Vigiloom.Agent.exe') + '"'), 'NT SERVICE\Vigiloom Agent', $true)
    $agentCreated = $true
    [Vigiloom.Setup.Native]::Create($updaterName, ('"' + (Join-Path $updaterRoot 'Vigiloom.Updater.exe') + '"'), $null, $false)
    $updaterCreated = $true
    $agentSid = ([Security.Principal.NTAccount]::new('NT SERVICE', $agentName)).Translate([Security.Principal.SecurityIdentifier])
    $systemSid = [Security.Principal.SecurityIdentifier]::new([Security.Principal.WellKnownSidType]::LocalSystemSid, $null)
    $adminsSid = [Security.Principal.SecurityIdentifier]::new([Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid, $null)
    New-ProtectedDirectory $stateRoot $adminsSid @($systemSid, $adminsSid) $agentSid
    $stateCreated = $true
    New-ProtectedDirectory $agentState $agentSid @($agentSid, $systemSid, $adminsSid) $null
    New-ProtectedDirectory $updaterState $adminsSid @($systemSid, $adminsSid) $null
    [void](New-Item -ItemType Directory -Path $programRoot)
    $programCreated = $true
    [void](New-Item -ItemType Directory -Path $agentRoot, $updaterRoot -Force)
    Copy-Item -Path (Join-Path $agentSource '*') -Destination $agentRoot -Recurse -Force
    Copy-Item -Path (Join-Path $updaterSource '*') -Destination $updaterRoot -Recurse -Force
    $bytes = [Text.Encoding]::ASCII.GetBytes($enrollment)
    try { [IO.File]::WriteAllBytes((Join-Path $agentState 'enrollment.pending'), $bytes) }
    finally { [Array]::Clear($bytes, 0, $bytes.Length); $enrollment = $null }
    $agentRegistry = "HKLM:\SYSTEM\CurrentControlSet\Services\$agentName"
    $updaterRegistry = "HKLM:\SYSTEM\CurrentControlSet\Services\$updaterName"
    New-ItemProperty -LiteralPath $agentRegistry -Name Environment -PropertyType MultiString -Value @(
        "Vigiloom__Agent__Server=$serverText", "Vigiloom__Agent__StateDirectory=$agentState",
        'Vigiloom__Agent__UpdaterPipe=Vigiloom.Updater') | Out-Null
    New-ItemProperty -LiteralPath $updaterRegistry -Name Environment -PropertyType MultiString -Value @(
        "Vigiloom__Updater__Server=$serverText", "Vigiloom__Updater__DeviceId=$device",
        "Vigiloom__Updater__InstallationRoot=$(Join-Path $programRoot 'Agent')", "Vigiloom__Updater__StateDirectory=$updaterState",
        "Vigiloom__Updater__AgentSid=$($agentSid.Value)") | Out-Null
    $previousState = $env:Vigiloom__Updater__StateDirectory
    try {
        $env:Vigiloom__Updater__StateDirectory = $updaterState
        & (Join-Path $updaterRoot 'Vigiloom.Updater.exe') provision-trust $rootSource $fingerprint
        if ($LASTEXITCODE -ne 0) { throw 'Could not provision the trusted publisher root.' }
    } finally { $env:Vigiloom__Updater__StateDirectory = $previousState }
    Start-Service -Name $updaterName
    # Registration may succeed even if service startup reports an error; keep identity after this point.
    $activated = $true
    Start-Service -Name $agentName
    Write-Host 'Vigiloom Agent and Updater services are installed. Check enrollment status in Vigiloom.'
}
catch {
    $enrollment = $null
    if ($activated) {
        Write-Warning 'Service startup failed. The installation and Agent identity have been retained for diagnosis.'
    } else {
        if ($updaterCreated) { Stop-Service -Name $updaterName -ErrorAction SilentlyContinue; [Vigiloom.Setup.Native]::Delete($updaterName) }
        if ($agentCreated) { [Vigiloom.Setup.Native]::Delete($agentName) }
        if ($programCreated) { Remove-Item -LiteralPath $programRoot -Recurse -Force }
        if ($stateCreated) { Remove-Item -LiteralPath $stateRoot -Recurse -Force }
        Write-Warning 'Installation failed before Agent startup; newly created installation files were removed.'
    }
    throw
}
