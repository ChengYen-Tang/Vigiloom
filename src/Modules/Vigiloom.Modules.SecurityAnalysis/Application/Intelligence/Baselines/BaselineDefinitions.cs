using System.Security.Cryptography;
using System.Text.Json;
using Vigiloom.Contracts.Intelligence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Baselines;

internal sealed record BaselineDefinition(string Key, string Name, string Commit, string Url, string Sha256,
    string LicenseEvidence, BaselineCheck Check)
{
    internal byte[] Artifact => JsonSerializer.SerializeToUtf8Bytes(Check);
}

/// <summary>Only source versions whose rule semantics were reviewed are mapped.</summary>
internal static class BaselineDefinitions
{
    internal const string Schema = "baseline-check-v1";
    internal const string Mapping = "baseline-reviewed-v1";

    private const string LinuxCommit = "033b1b8672802ffa6bd555c373e2c73c32d25f5d";
    private const string WindowsCommit = "efa8aec879259ade7db396715850c58df9f44e81";
    private const string LinuxPath = "linux_os/guide/system/permissions/files/permissions_important_account_files/file_permissions_etc_shadow/rule.yml";
    private const string WindowsPath = "source/StigData/Processed/WindowsServer-2022-MS-2.8.xml";

    internal static readonly BaselineDefinition[] All =
    [
        new(BaselineSources.LinuxShadow, "ComplianceAsCode shadow permissions", LinuxCommit,
            $"https://raw.githubusercontent.com/ComplianceAsCode/content/{LinuxCommit}/{LinuxPath}",
            "cb9219e6304fe195c0b95333a73fc0740b365649149563b1f8cd92276791e048",
            $"ComplianceAsCode BSD-3-Clause; ComplianceAsCode/content@{LinuxCommit}",
            new("file_permissions_etc_shadow", "/etc/shadow permissions", "linux", ["ubuntu:22.04", "ubuntu:24.04", "debian:12"],
                "file-permissions-no-extended-acl", "/etc/shadow", "0640-or-stricter",
                $"https://github.com/ComplianceAsCode/content/blob/{LinuxCommit}/{LinuxPath}", "BSD-3-Clause")),
        new(BaselineSources.WindowsNetworkSelection, "PowerSTIG network selection", WindowsCommit,
            $"https://raw.githubusercontent.com/microsoft/PowerStig/{WindowsCommit}/{WindowsPath}",
            "f1674431abe94c25969c4bb10db5ace83ede126a0ff6e57e6d060c911eac7c36",
            $"Microsoft PowerSTIG MIT; originating DISA STIG; microsoft/PowerStig@{WindowsCommit}",
            new("V-254348", "Network selection UI", "windows", ["server:2022:member"],
                "registry-dword-64", @"HKLM\SOFTWARE\Policies\Microsoft\Windows\System\DontDisplayNetworkSelectionUI", "1",
                $"https://github.com/microsoft/PowerStig/blob/{WindowsCommit}/{WindowsPath}", "MIT"))
    ];

    internal static BaselineDefinition? Find(string key) => Array.Find(All, x => x.Key == key);

    internal static bool Matches(BaselineDefinition definition, byte[] content) =>
        Convert.ToHexStringLower(SHA256.HashData(content)) == definition.Sha256;
}
