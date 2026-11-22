$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this script as Administrator.' }
if (-not (Get-Service -Name 'Vigiloom Agent' -ErrorAction SilentlyContinue)) { throw 'Install Vigiloom Agent first.' }
$channel = 'Microsoft-Windows-Sysmon/Operational'
$configuration = & wevtutil.exe gl $channel /f:xml
if ($LASTEXITCODE -ne 0) { throw 'Install Sysmon and enable its operational channel first.' }
$xml = [xml]($configuration -join "`n")
$sddl = $xml.DocumentElement.GetAttribute('channelAccess')
if ([string]::IsNullOrWhiteSpace($sddl)) { throw 'Cannot read the Sysmon channel access policy.' }
$sid = ([Security.Principal.NTAccount]::new('NT SERVICE', 'Vigiloom Agent')).Translate([Security.Principal.SecurityIdentifier])
$descriptor = [Security.AccessControl.RawSecurityDescriptor]::new($sddl)
if ($null -eq $descriptor.DiscretionaryAcl) { throw 'The Sysmon channel does not have an explicit access list.' }
$alreadyGranted = @($descriptor.DiscretionaryAcl | Where-Object {
    $_ -is [Security.AccessControl.QualifiedAce] -and $_.AceQualifier -eq [Security.AccessControl.AceQualifier]::AccessAllowed -and
    $_.SecurityIdentifier -eq $sid -and ($_.AccessMask -band 1) -eq 1
}).Count -gt 0
if (-not $alreadyGranted) {
    $ace = [Security.AccessControl.CommonAce]::new([Security.AccessControl.AceFlags]::None,
        [Security.AccessControl.AceQualifier]::AccessAllowed, 1, $sid, $false, $null)
    $descriptor.DiscretionaryAcl.InsertAce($descriptor.DiscretionaryAcl.Count, $ace)
    $updated = $descriptor.GetSddlForm([Security.AccessControl.AccessControlSections]::All)
    & wevtutil.exe sl $channel "/ca:$updated"
    if ($LASTEXITCODE -ne 0) { throw 'Could not grant read access to the Sysmon channel.' }
}
Write-Host 'Read permission for Vigiloom Agent was added to the Sysmon operational channel.'
