# Read-only security baseline: source and check contract

REQ-35 selects and executes a verified subset of actual upstream configuration checks. REQ-32 owns source acquisition, versions, publication and rollback; REQ-35 owns endpoint observation and results. This document is an implementation input, not a claim that any baseline or OS is already supported.

## Selected source material

| Source | Version inspected | Scope and conditions |
|---|---|---|
| [ComplianceAsCode content](https://github.com/ComplianceAsCode/content/tree/033b1b8672802ffa6bd555c373e2c73c32d25f5d) | commit `033b1b8672802ffa6bd555c373e2c73c32d25f5d` | Linux source for the initial checked subset. The repository [license](https://github.com/ComplianceAsCode/content/blob/033b1b8672802ffa6bd555c373e2c73c32d25f5d/LICENSE) is BSD-3-Clause. Retain the required notices when distributing applicable material and verify the selected content's own notices. Profile references to CIS are not permission to redistribute a CIS benchmark PDF or claim certification. |
| [Microsoft PowerSTIG](https://github.com/microsoft/PowerStig/tree/efa8aec879259ade7db396715850c58df9f44e81) | commit `efa8aec879259ade7db396715850c58df9f44e81`; processed Windows Server 2022 member-server STIG v2r8 | Initial Windows rule metadata and read-only check semantics. Use the selected processed file under PowerSTIG's [MIT license](https://github.com/microsoft/PowerStig/blob/efa8aec879259ade7db396715850c58df9f44e81/LICENSE), retain its Microsoft copyright and license notice, and attribute the originating DISA STIG. The archived STIG's `terms-of-use` element is empty; it does not establish additional terms. Do not run PowerSTIG remediation or claim STIG certification. |
| [Wazuh SCA](https://documentation.wazuh.com/current/user-manual/capabilities/sec-config-assessment/creating-custom-policies.html) | [Windows Server 2022 policy at commit `3bf19121`](https://github.com/wazuh/wazuh/blob/3bf19121e8604c99566fc5e78267648a5161b062/ruleset/sca/windows/cis_win2022.yml) | Reference for policy selection, requirements and results. The inspected policy declares GPL version 2 and references a CIS benchmark; do not copy or redistribute it without resolving the applicable content and project license terms. The file itself warns that some checks depend on English system language. |

Selecting a software engine's license does not establish the license of every rule, benchmark or derived document. Source and rule provenance must remain visible through REQ-32. Unverified content is not a selectable or published source.

The notices for the selected source material are retained in [ComplianceAsCode.txt](../licenses/ComplianceAsCode.txt) and [PowerSTIG.txt](../licenses/PowerSTIG.txt). The source refresh checks the pinned files against their reviewed hashes; it does not automatically accept newer upstream commits. New versions require a reviewed mapping and corresponding notices before publication.

## First selected checks

These selected checks are starting cases for T02, not a claim that the entire upstream profile is implemented. They become available only after their source publication, conversion and actual target execution are verified.

| Source and check | Applicability | Read-only observation | Important boundary |
|---|---|---|---|
| ComplianceAsCode [`file_permissions_etc_shadow`](https://github.com/ComplianceAsCode/content/blob/033b1b8672802ffa6bd555c373e2c73c32d25f5d/linux_os/guide/system/permissions/files/permissions_important_account_files/file_permissions_etc_shadow/rule.yml) | Ubuntu 22.04 or 24.04, or Debian 12; verify OS identity before evaluating | Read `/etc/shadow` metadata, not password hashes. `0640` is the maximum allowed mode for these products; more restrictive modes such as `0600` and `0400` also pass. | Honor the upstream [file-permissions template](https://github.com/ComplianceAsCode/content/blob/033b1b8672802ffa6bd555c373e2c73c32d25f5d/shared/templates/file_permissions/oval.template), including symlink and extended-ACL semantics. If the Agent cannot establish equivalent effective permissions, report unknown or permission denied rather than a pass. |
| PowerSTIG [`V-254348`](https://github.com/microsoft/PowerStig/blob/efa8aec879259ade7db396715850c58df9f44e81/source/StigData/Processed/WindowsServer-2022-MS-2.8.xml) | Windows Server 2022 member server; do not infer applicability to domain controllers or other versions | Read the 64-bit HKLM policy registry view for `SOFTWARE\Policies\Microsoft\Windows\System`, `DontDisplayNetworkSelectionUI`, REG_DWORD `1`. | Missing or different value is a finding according to this rule; access denial is not a finding. Do not run the upstream DSC resource. Validate x86 Agent registry-view behavior on a real supported target. |

Other operating systems, policies and controls require explicit mapping and verification. No blanket CIS, STIG, Wazuh SCA, OVAL or SCAP compatibility is implied by supporting these examples.

Administrator-authored checks reuse these two operations with a chosen target and expected value: Linux regular-file permissions with no extended ACL, or a 64-bit HKLM DWORD. The draft records its supported OS versions, reference and guidance, and is not sent to Agents until an administrator explicitly publishes that immutable revision. Activation and rollback use the existing source history and audit. These local checks are not represented as unchanged upstream rules, and do not execute commands or modify the host.

For the Linux check, the Agent needs permission to traverse the path and read the relevant file and ACL metadata; it does not need permission to read `/etc/shadow` contents. For the Windows check, it needs `KEY_QUERY_VALUE` access to the named HKLM key and must request the 64-bit registry view on the supported 64-bit Windows Server target, including from an x86 Agent process. Effective access depends on the target's security policy: verify a normal service account and an explicitly denied account on each target, and report denial without automatically elevating the Agent.

For the selected Linux check, an observed regular file with no extended ACL passes when none of its permission bits exceeds the allowed `0640` mask. The upstream template describes an extended ACL as failing the mode check; if the implementation cannot determine whether one exists, it must report unknown. The template excludes symbolic links, so do not call a symlink a passing file without verifying the actual target semantics. Treat inaccessible metadata as permission denied; missing or otherwise unassessable data is unknown.

For the selected Windows check, a readable `REG_DWORD` value of `1` passes; an absent value, a different type or a different value is a finding. Denied access is permission denied. A confirmed nonmatching OS or server role is not applicable; an unknown OS or role is unknown.

## Result and execution semantics

- Keep pass, fail, not applicable, unknown, permission denied and execution error distinct. Include policy and check identity, source revision, OS applicability, observation time and a bounded explanation; do not include sensitive file contents or credentials.
- Evaluate applicability before reading a target. Missing privileges, unreadable source, unsupported expressions, platform ambiguity or incomplete observation cannot become pass. A finding is not evidence of compromise.
- Select checks using centrally published, versioned policy data. The Agent executes only supported, explicitly mapped read-only operations and reports through the existing versioned configuration and durable transport. Never execute an arbitrary command, script, remediation or expression from a downloaded policy.
- Local exceptions and administrative changes must be attributable without modifying upstream source material. Evidence and management are authenticated; public projections do not expose host settings.
- Verify both normal and restricted service accounts against actual Windows and Linux targets, cancellation and bounded execution, applicability, source update and rollback, including correct behavior after restart. Missing platform coverage remains visible as not assessed.
