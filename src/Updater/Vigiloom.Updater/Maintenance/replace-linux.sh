#!/usr/bin/env bash
set -Eeuo pipefail

# Runs as a separate transient unit from an installer-protected state directory.
agent=$1
source=$2
state=$3
operation=$4
base=$(dirname -- "$agent")
current=$base/updater
next=$base/updater.next
previous=$base/updater.previous
status=$state/updater-replacement.status

record() { printf '%s %s\n' "$operation" "$1" > "$status"; }
failed() { local code=$?; record "failed exit-$code"; }
trap failed ERR
record started
[[ $(id -u) == 0 && -d $agent && -d $source && -d $current && ! -e $next && ! -e $previous ]]
[[ $source == "$agent"/staging/*/updater && -f $source/Vigiloom.Updater ]]
exec {maintenance}>"$agent/repair.lock"
flock -n "$maintenance"
install -d -m 0755 "$next"
touch "$next/.vigiloom-updater-stage"
cp -a -- "$source/." "$next/"
chown -R root:root "$next"
chmod -R u=rwX,go=rX "$next"
chmod 0755 "$next/Vigiloom.Updater"
systemctl stop vigiloom-updater.service
exec {ownership}>"$agent/updater.lock"
flock -n "$ownership"
mv -- "$current" "$previous"
mv -- "$next" "$current"
exec {ownership}>&-
systemctl start vigiloom-updater.service
pid=$(systemctl show --property MainPID --value vigiloom-updater.service)
[[ $pid =~ ^[1-9][0-9]*$ ]]
for _ in {1..10}; do
    systemctl is-active --quiet vigiloom-updater.service
    [[ $(systemctl show --property MainPID --value vigiloom-updater.service) == "$pid" ]]
    [[ $("$current/Vigiloom.Updater" recovery-status "$state") == ready ]]
    sleep 1
done
rm -f -- "$current/.vigiloom-updater-stage"
rm -rf -- "$previous"
record succeeded
trap - ERR
rm -f -- "$0"
