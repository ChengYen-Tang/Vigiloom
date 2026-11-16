#!/usr/bin/env bash
set -Eeuo pipefail

# The release bundle supplies both self-contained applications and the publisher root.
bundle=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
agent_source="$bundle/payload/agent"
updater_source="$bundle/payload/updater"
root_source="$bundle/payload/root.json"
agent_root=/opt/vigiloom/agent
updater_root=/opt/vigiloom/updater
agent_state=/var/lib/vigiloom/agent
updater_state=/var/lib/vigiloom/updater
agent_env=/etc/vigiloom/agent.env
updater_env=/etc/vigiloom/updater.env
agent_unit=/etc/systemd/system/vigiloom-agent.service
updater_unit=/etc/systemd/system/vigiloom-updater.service

fail() { printf '%s\n' "$1" >&2; exit 1; }
[[ $(id -u) == 0 ]] || fail 'Run the installer as root.'
[[ $(uname -m) == x86_64 ]] || fail 'This Agent package supports Linux x64 only.'
[[ -d /run/systemd/system ]] || fail 'This package requires a running systemd service manager.'
check_parent() {
    local path=$1 agent_access=$2 owner mode
    [[ -e $path || -L $path ]] || return 0
    [[ -d $path && ! -L $path ]] || fail "Installation parent is not a real directory: $path"
    owner=$(stat -c '%u' -- "$path")
    mode=$(stat -c '%a' -- "$path")
    (( owner == 0 && (8#$mode & 0022) == 0 )) || fail "Installation parent must be root-owned and not writable by other users: $path"
    if [[ $agent_access == yes ]]; then
        (( (8#$mode & 0001) != 0 )) || fail "Installation parent must be traversable by the Agent account: $path"
    fi
}
for parent in /opt /opt/vigiloom /var /var/lib /var/lib/vigiloom; do check_parent "$parent" yes; done
for parent in /etc /etc/vigiloom /etc/systemd /etc/systemd/system; do check_parent "$parent" no; done
for source in "$agent_source/Vigiloom.Agent" "$updater_source/Vigiloom.Updater" "$root_source" \
    "$bundle/vigiloom-agent.service" "$bundle/vigiloom-updater.service"; do
    [[ -f $source ]] || fail 'The installer bundle is incomplete.'
done
for destination in "$agent_root" "$updater_root" "$agent_state" "$updater_state" \
    "$agent_env" "$updater_env" "$agent_unit" "$updater_unit"; do
    [[ ! -e $destination && ! -L $destination ]] || fail 'An existing Vigiloom Agent installation or state was found. It will not be overwritten.'
done
! getent passwd vigiloom-agent >/dev/null || fail 'The vigiloom-agent account already exists.'
! getent group vigiloom-agent >/dev/null || fail 'The vigiloom-agent group already exists.'
! systemctl cat vigiloom-agent.service >/dev/null 2>&1 || fail 'The Vigiloom Agent service already exists.'
! systemctl cat vigiloom-updater.service >/dev/null 2>&1 || fail 'The Vigiloom Updater service already exists.'

# Exercise the published runtimes before creating an account or asking for secrets.
# The runtime loads system ICU itself; checking package names alone is insufficient.
for executable in "$agent_source/Vigiloom.Agent" "$updater_source/Vigiloom.Updater"; do
    if ! (ulimit -c 0; env -u DOTNET_SYSTEM_GLOBALIZATION_APPLOCALICU DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 timeout 10s "$executable" check-runtime); then
        printf '%s\n' \
            'The Agent or Updater could not start. Check ICU and other native .NET dependencies.' \
            'If the diagnostic above reports missing ICU, install the ICU runtime package for your distribution, then retry.' \
            'For other errors, follow the diagnostic above.' \
            'Fedora/RHEL: sudo dnf install libicu' \
            'Ubuntu/Debian: install the libicu package for your OS version (for example, libicu74 on Ubuntu 24.04).' \
            'See https://learn.microsoft.com/dotnet/core/install/linux for distribution-specific dependencies.' >&2
        exit 1
    fi
done

read -r -p 'Vigiloom server HTTPS URL: ' server
[[ $server == https://?* && $server != *[!a-zA-Z0-9._~:/%\[\]-]* ]] || fail 'Enter an HTTPS URL without credentials, query, fragment, or whitespace.'
read -r -p 'Device ID: ' device
[[ $device =~ ^[[:xdigit:]]{8}-[[:xdigit:]]{4}-7[[:xdigit:]]{3}-[89aAbB][[:xdigit:]]{3}-[[:xdigit:]]{12}$ ]] || fail 'Enter the device UUID shown in Vigiloom.'
read -r -p 'Publisher root SHA-256 fingerprint: ' fingerprint
[[ $fingerprint =~ ^[[:xdigit:]]{64}$ ]] || fail 'Enter the trusted publisher root SHA-256 fingerprint.'
read -r -s -p 'One-time Agent enrollment value: ' enrollment
printf '\n'
[[ $enrollment =~ ^vle_[[:xdigit:]]{8}-[[:xdigit:]]{4}-7[[:xdigit:]]{3}-[89aAbB][[:xdigit:]]{3}-[[:xdigit:]]{12}\.[a-zA-Z0-9_-]{43}$ ]] || fail 'Enter a valid one-time Agent enrollment value.'

activated=0
created_account=0
cleanup_failed_install() {
    local status=$?
    trap - ERR
    unset enrollment
    if (( activated )); then
        printf '%s\n' 'Service startup failed. The installation and Agent identity have been retained for diagnosis.' >&2
    else
        systemctl disable --now vigiloom-agent.service >/dev/null 2>&1 || true
        systemctl disable --now vigiloom-updater.service >/dev/null 2>&1 || true
        rm -f -- "$agent_unit" "$updater_unit" "$agent_env" "$updater_env"
        rm -rf -- "$agent_root" "$updater_root" "$agent_state" "$updater_state"
        if (( created_account )); then userdel vigiloom-agent >/dev/null 2>&1 || true; fi
        systemctl daemon-reload >/dev/null 2>&1 || true
        printf '%s\n' 'Installation failed before Agent startup; newly created installation files were removed.' >&2
    fi
    exit "$status"
}
trap cleanup_failed_install ERR

umask 077
useradd --system --user-group --no-create-home --home-dir "$agent_state" --shell /usr/sbin/nologin vigiloom-agent
created_account=1
agent_uid=$(id -u vigiloom-agent)
agent_gid=$(id -g vigiloom-agent)
[[ -d /opt/vigiloom ]] || install -d -m 0755 /opt/vigiloom
[[ -d /var/lib/vigiloom ]] || install -d -m 0755 /var/lib/vigiloom
[[ -d /etc/vigiloom ]] || install -d -m 0755 /etc/vigiloom
install -d -m 0755 "$agent_root/initial" "$updater_root"
install -d -o vigiloom-agent -g vigiloom-agent -m 0700 "$agent_state"
install -d -m 0700 "$updater_state"
cp -a -- "$agent_source/." "$agent_root/initial/"
cp -a -- "$updater_source/." "$updater_root/"
chown -R root:root "$agent_root" "$updater_root"
chmod -R u=rwX,go=rX "$agent_root" "$updater_root"
chmod 0755 "$agent_root/initial/Vigiloom.Agent" "$updater_root/Vigiloom.Updater"
ln -s -- "$agent_root/initial" "$agent_root/current"
printf '%s' "$enrollment" > "$agent_state/enrollment.pending"
unset enrollment
chown vigiloom-agent:vigiloom-agent "$agent_state/enrollment.pending"
chmod 0600 "$agent_state/enrollment.pending"
printf 'Vigiloom__Agent__Server=%s\nVigiloom__Agent__StateDirectory=%s\nVigiloom__Agent__UpdaterSocket=/run/vigiloom-updater/updater.sock\n' \
    "$server" "$agent_state" > "$agent_env"
printf 'Vigiloom__Updater__Server=%s\nVigiloom__Updater__DeviceId=%s\nVigiloom__Updater__InstallationRoot=%s\nVigiloom__Updater__StateDirectory=%s\nVigiloom__Updater__IpcDirectory=/run/vigiloom-updater\nVigiloom__Updater__AgentUid=%s\nVigiloom__Updater__AgentGroupId=%s\n' \
    "$server" "$device" "$agent_root" "$updater_state" "$agent_uid" "$agent_gid" > "$updater_env"
Vigiloom__Updater__StateDirectory="$updater_state" "$updater_root/Vigiloom.Updater" provision-trust "$root_source" "$fingerprint"
install -m 0644 "$bundle/vigiloom-agent.service" "$agent_unit"
install -m 0644 "$bundle/vigiloom-updater.service" "$updater_unit"
systemctl daemon-reload
systemctl enable --now vigiloom-updater.service
systemctl enable vigiloom-agent.service
# Registration may succeed even if systemctl reports an error; keep identity after this point.
activated=1
systemctl start vigiloom-agent.service
trap - ERR
printf '%s\n' 'Vigiloom Agent and Updater services are installed. Check enrollment status in Vigiloom.'
