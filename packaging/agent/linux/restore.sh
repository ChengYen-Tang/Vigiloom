#!/usr/bin/env bash
set -Eeuo pipefail

fail() { printf '%s\n' "$1" >&2; exit 1; }
[[ $# == 0 ]] || fail 'Usage: sudo /opt/vigiloom/agent-tools/restore.sh'
[[ $(id -u) == 0 ]] || fail 'Run this command as root.'
[[ -d /run/systemd/system ]] && systemctl show --property=Version >/dev/null || fail 'A running systemd service manager is required.'
command -v flock >/dev/null || fail 'The flock command is required.'
[[ -d /opt/vigiloom/agent && ! -L /opt/vigiloom/agent ]] || fail 'The Agent installation directory is missing.'
exec {maintenance}>>/opt/vigiloom/agent/repair.lock
flock -n "$maintenance" || fail 'Another installation maintenance operation is already running.'

backup=/etc/vigiloom/service-backup
agent=/etc/systemd/system/vigiloom-agent.service
updater=/etc/systemd/system/vigiloom-updater.service
[[ -d $backup && ! -L $backup ]] || fail 'The retained service definitions were not found.'
[[ -x /opt/vigiloom/updater/Vigiloom.Updater && -x /opt/vigiloom/agent/current/Vigiloom.Agent ]] || fail 'Retained Agent or Updater files are missing.'
[[ -d /var/lib/vigiloom/agent && -d /var/lib/vigiloom/updater ]] || fail 'Retained Agent or Updater data is missing.'
[[ -f /etc/vigiloom/agent.env && -f /etc/vigiloom/updater.env ]] || fail 'Retained service settings are missing.'
grep -Fxq 'Vigiloom__Updater__StateDirectory=/var/lib/vigiloom/updater' /etc/vigiloom/updater.env || fail 'The Updater state directory is not the expected retained location.'
getent passwd vigiloom-agent >/dev/null && getent group vigiloom-agent >/dev/null || fail 'The retained Agent account is missing.'
if systemctl is-active --quiet vigiloom-agent.service; then fail 'Agent is already running; stop it before restoring services.'; fi

restore_unit() {
    local saved=$1 target=$2 command=$3
    [[ -f $saved && ! -L $saved ]] && grep -Fxq -- "$command" "$saved" || fail "Invalid retained service definition: $saved"
    if [[ -e $target || -L $target ]]; then
        [[ -f $target && ! -L $target ]] && cmp -s -- "$saved" "$target" || fail "A different service definition already exists: $target"
    else
        install -m 0644 "$saved" "$target"
    fi
}
restore_unit "$backup/vigiloom-agent.service" "$agent" 'ExecStart=/opt/vigiloom/agent/current/Vigiloom.Agent'
restore_unit "$backup/vigiloom-updater.service" "$updater" 'ExecStart=/opt/vigiloom/updater/Vigiloom.Updater'
systemctl daemon-reload

# Do not enable Agent until the Updater has finished any interrupted recovery.
systemctl disable vigiloom-agent.service
systemctl enable --now vigiloom-updater.service
deadline=$((SECONDS + 240))
while :; do
    systemctl is-active --quiet vigiloom-updater.service || fail 'Updater is not running; retained data has not been changed.'
    set +e
    status=$(timeout 10s /opt/vigiloom/updater/Vigiloom.Updater recovery-status /var/lib/vigiloom/updater)
    result=$?
    set -e
    if [[ $result == 0 && $status == ready ]]; then break; fi
    [[ $result == 1 && $status == pending ]] || fail 'Updater recovery failed or its journal cannot be read; Agent was not enabled.'
    (( SECONDS < deadline )) || fail 'Updater recovery did not complete in time; Agent was not enabled.'
    sleep 2
done
systemctl is-active --quiet vigiloom-updater.service || fail 'Updater stopped during recovery; Agent was not enabled.'
systemctl enable --now vigiloom-agent.service
printf '%s\n' 'Vigiloom services were restored with the retained Agent identity and data.'
