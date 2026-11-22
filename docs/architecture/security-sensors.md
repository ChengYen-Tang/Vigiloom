# IOC, YARA and optional host sensors

REQ-36 uses existing host observations rather than installing another collector by
default. An indicator match is a lead for investigation, not proof of compromise.

## Sources and engine

| Component | Scope and terms | Requirements |
| --- | --- | --- |
| [Signature-Base IOC files](https://github.com/Neo23x0/signature-base#license) | The existing REQ-32 adapter selects its reviewed IP, domain and hash files under DRL-1.1, with source attribution. Other files and individual YARA rules have their own terms. | No API key is used for these repository files. Do not imply they provide URL indicators or an expiry for each entry. |
| [YARA](https://github.com/VirusTotal/yara/blob/master/COPYING) | The engine is BSD-3-Clause; this does not license rules supplied to it. | Run only an explicitly selected file with bounded size and execution time. Rule compatibility and the engine's actual availability must be reported. |
| [Sysmon for Windows](https://learn.microsoft.com/en-us/sysinternals/downloads/sysmon) | Separate Microsoft service and driver with its own license; the Agent can read its Windows Event Log channel when explicitly configured. | Administrator installs and configures Sysmon. It adds CPU, memory and log storage overhead. Reading the channel requires appropriate event-log access. |
| [Sysmon for Linux](https://github.com/microsoft/Sysmon-Linux#license) | Separate service: its repository is MIT, its eBPF programs GPL-2.0, and its SysinternalsEBPF dependency LGPL-2.1 with GPL-2.0 eBPF code. | Administrator installation and suitable kernel/eBPF support are required. Events are written to the host logging system; kernel compatibility and access must be verified on each supported distribution. |

The [YARA release](https://github.com/VirusTotal/yara/releases) offers separate
Windows x86 and x64 binaries; Linux packaging depends on the distribution. A
package or source being available does not establish that Vigiloom has installed,
integrated or tested it. Rules with unresolved terms are not silently published.
The existing Agent reads Windows event channels and Linux journal or text logs;
the optional sensor still requires explicit enablement and an actual integration
test. No sensor installation or host isolation is automatic.

In a Windows Sandbox test, Sysmon installed its service and driver, produced a
process-creation event, and was removed with its own uninstall command. The
Vigiloom event decoder successfully read the event's ID, image and hash fields.
The installed Sysmon service rejected a normal `Stop-Service` request; use the
vendor's supported controls rather than assuming the service can be stopped this
way. The Agent's virtual service account could not read the channel by default.
After an Administrator ran the bundled `grant-sysmon-read.ps1`, the actual Agent
event reader running as that account read a process event; running the script a
second time also succeeded. This test does not by itself establish end-to-end
delivery to the central platform.

On Fedora 42 x64 under WSL, the Vigiloom scanner was exercised with the installed
YARA engine: a match, a non-match, the file size check, timeout and cancellation
of a running scan passed. The test package was removed afterward. The Windows x64
engine was also exercised separately, including timeout; these checks do not establish full Agent
deployment or all platform combinations. A separate Linux Agent worker check
used a simulated central cancellation response and confirmed that the running
YARA process stopped and no scan result was queued.
