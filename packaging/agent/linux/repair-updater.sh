#!/usr/bin/env bash
set -Eeuo pipefail

# Run from a trusted installation bundle; this process replaces the stopped Updater.
fail() { printf '%s\n' "$1" >&2; exit 1; }
[[ $# == 0 ]] || fail 'Usage: sudo ./repair-updater.sh'
[[ $(id -u) == 0 ]] || fail 'Run this command as root.'
[[ -d /run/systemd/system ]] && systemctl show --property=Version >/dev/null || fail 'A running systemd service manager is required.'
command -v flock >/dev/null || fail 'The flock command is required.'
bundle=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
source="$bundle/payload/updater"
base=/opt/vigiloom
current=$base/updater
previous=$base/updater.previous
next=$base/updater.next
agent=$base/agent
[[ -d $base && ! -L $base && -d $agent && ! -L $agent ]] || fail 'The existing installation was not found.'
exec {repairLock}>>"$agent/repair.lock"
flock -n "$repairLock" || fail 'Another Updater repair is already running.'
[[ -f /etc/systemd/system/vigiloom-updater.service && ! -L /etc/systemd/system/vigiloom-updater.service ]] || fail 'The existing Updater service was not found.'
grep -Fxq 'ExecStart=/opt/vigiloom/updater/Vigiloom.Updater' /etc/systemd/system/vigiloom-updater.service || fail 'Unexpected Updater service definition.'
for path in "$current" "$previous" "$next"; do [[ ! -L $path ]] || fail 'The Updater paths cannot be symbolic links.'; done
[[ ! -e $previous || -d $previous ]] || fail 'The previous Updater path is not a directory.'
[[ ! -e $next || -f $next/.vigiloom-updater-stage ]] || fail 'An unexpected staging directory already exists.'

# Recover a replacement interrupted before it was confirmed, then require a retry.
if [[ -d $previous ]]; then
    systemctl stop vigiloom-updater.service
    exec {ownership}>>"$agent/updater.lock"
    flock -n "$ownership" || fail 'Another Updater process still owns the installation.'
    if [[ -d $current ]]; then
        [[ ! -e $next ]] || rm -rf -- "$next"
        mv -- "$current" "$next"
    fi
    mv -- "$previous" "$current"
    exec {ownership}>&-
    [[ ! -e $next ]] || rm -rf -- "$next"
    if [[ -x $current/Vigiloom.Updater ]]; then
        systemctl start vigiloom-updater.service
        fail 'The previous Updater was restored after an interrupted repair. Run this command again to retry.'
    fi
    # A repair of a missing executable was interrupted. Continue with the new bundle;
    # the retained directory remains available for rollback until the replacement is ready.
fi
[[ -d $current && -f $source/Vigiloom.Updater ]] || fail 'The installed Updater directory or bundled Updater is missing.'
if ! (ulimit -c 0; env -u DOTNET_SYSTEM_GLOBALIZATION_APPLOCALICU DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 timeout 10s "$source/Vigiloom.Updater" check-runtime); then
    fail 'The bundled Updater cannot start. Check system ICU and other native dependencies.'
fi
[[ ! -e $next ]] || rm -rf -- "$next"
install -d -m 0755 "$next"
touch "$next/.vigiloom-updater-stage"
cp -a -- "$source/." "$next/"
chown -R root:root "$next"
chmod -R u=rwX,go=rX "$next"
chmod 0755 "$next/Vigiloom.Updater"
systemctl stop vigiloom-updater.service
exec {ownership}>>"$agent/updater.lock"
flock -n "$ownership" || fail 'Another Updater process still owns the installation.'
mv -- "$current" "$previous"
mv -- "$next" "$current"
exec {ownership}>&-
wait_for_recovery() {
    local pid started deadline result status
    pid=$(systemctl show --property MainPID --value vigiloom-updater.service)
    [[ $pid =~ ^[1-9][0-9]*$ ]] || return 1
    started=$SECONDS
    deadline=$((SECONDS + 240))
    while (( SECONDS < deadline )); do
        systemctl is-active --quiet vigiloom-updater.service || return 1
        [[ $(systemctl show --property MainPID --value vigiloom-updater.service) == "$pid" ]] || return 1
        set +e
        status=$(timeout 10s "$current/Vigiloom.Updater" recovery-status /var/lib/vigiloom/updater)
        result=$?
        set -e
        if [[ $result == 0 && $status == ready && $((SECONDS - started)) -ge 10 ]]; then
            systemctl is-active --quiet vigiloom-updater.service || return 1
            [[ $(systemctl show --property MainPID --value vigiloom-updater.service) == "$pid" ]] || return 1
            return 0
        fi
        [[ $result == 0 && $status == ready || $result == 1 && $status == pending ]] || return 1
        sleep 2
    done
    return 1
}
if ! systemctl start vigiloom-updater.service || ! wait_for_recovery; then
    systemctl stop vigiloom-updater.service || true
    exec {ownership}>>"$agent/updater.lock"
    flock -n "$ownership" || fail 'The replacement could not start; another Updater holds the installation lock. The previous version is retained.'
    mv -- "$current" "$next"
    mv -- "$previous" "$current"
    exec {ownership}>&-
    systemctl start vigiloom-updater.service || fail 'The replacement failed and the previous Updater was restored, but its service could not start.'
    rm -rf -- "$next"
    fail 'The replacement failed; the previous Updater was restored.'
fi
rm -f -- "$current/.vigiloom-updater-stage"
rm -rf -- "$previous"
printf '%s\n' 'The Updater was replaced. Agent identity, queued data, settings and recovery journal were retained.'
