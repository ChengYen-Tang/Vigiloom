#!/usr/bin/env bash
set -Eeuo pipefail

fail() { printf '%s\n' "$1" >&2; exit 1; }
[[ $# == 0 ]] || fail 'Usage: sudo /opt/vigiloom/agent-tools/remove.sh'
[[ $(id -u) == 0 ]] || fail 'Run this command as root.'
[[ -d /run/systemd/system ]] && systemctl show --property=Version >/dev/null || fail 'A running systemd service manager is required.'
command -v flock >/dev/null || fail 'The flock command is required to stop the installation safely.'

backup=/etc/vigiloom/service-backup
agent=/etc/systemd/system/vigiloom-agent.service
updater=/etc/systemd/system/vigiloom-updater.service
[[ -d /etc/vigiloom && ! -L /etc/vigiloom ]] || fail 'Vigiloom configuration was not found.'
[[ ! -L $backup ]] || fail 'The service backup directory cannot be a symbolic link.'

check_unit() {
    local active=$1 saved=$2 command=$3 unit
    if [[ -f $active && ! -L $active ]]; then unit=$active
    elif [[ -f $saved && ! -L $saved ]]; then unit=$saved
    else fail "The Vigiloom service definition is missing: $active"; fi
    grep -Fxq -- "$command" "$unit" || fail "Unexpected Vigiloom service definition: $unit"
}
check_unit "$agent" "$backup/vigiloom-agent.service" 'ExecStart=/opt/vigiloom/agent/current/Vigiloom.Agent'
check_unit "$updater" "$backup/vigiloom-updater.service" 'ExecStart=/opt/vigiloom/updater/Vigiloom.Updater'
install -d -m 0700 "$backup"
[[ ! -f $agent ]] || install -m 0644 "$agent" "$backup/vigiloom-agent.service"
[[ ! -f $updater ]] || install -m 0644 "$updater" "$backup/vigiloom-updater.service"

# Stop the updater first so it cannot start a new switch while Agent is stopping.
for service in vigiloom-updater.service vigiloom-agent.service; do
    if [[ -f /etc/systemd/system/$service ]]; then
        systemctl stop "$service"
    elif systemctl is-active --quiet "$service"; then
        systemctl stop "$service"
    fi
    if systemctl is-active --quiet "$service"; then fail "Could not stop $service."; fi
    if [[ $service == vigiloom-updater.service ]]; then
        [[ -d /opt/vigiloom/agent && ! -L /opt/vigiloom/agent ]] || fail 'The Agent installation directory is missing.'
        exec {ownership}>>/opt/vigiloom/agent/updater.lock
        flock -n "$ownership" || fail 'Another Updater process still owns the installation; retry after it stops.'
    fi
    if [[ -f /etc/systemd/system/$service ]]; then systemctl disable "$service"; fi
done
rm -f -- "$agent" "$updater"
systemctl daemon-reload
printf '%s\n' 'Vigiloom services were removed. Agent identity, queued data, settings, trust and installed versions were retained.' \
    'Run sudo /opt/vigiloom/agent-tools/restore.sh to restore the same installation.' \
    'Removing local services does not revoke the Agent credential in the central platform.'
