#!/usr/bin/env bash
# Runs only in a disposable hosted runner. The parent test keeps stdin open until cleanup.
set -Eeuo pipefail

if [[ ${GITHUB_ACTIONS:-} != true || ${RUNNER_ENVIRONMENT:-} != github-hosted || $(id -u) != 0 ]]; then
    echo 'This test requires a disposable GitHub-hosted runner and root.' >&2
    exit 1
fi
if (( $# != 2 )); then
    echo 'Usage: linux-systemd-agent.sh <unpacked-bundle> <test-ca.pem>' >&2
    exit 2
fi
bundle=$(realpath -- "$1")
ca=$(realpath -- "$2")
[[ -d /run/systemd/system && -f $bundle/install.sh && -f $ca ]] || exit 1

paths=(/opt/vigiloom/agent /opt/vigiloom/updater /opt/vigiloom/updater.previous
    /opt/vigiloom/updater.next /opt/vigiloom/agent-tools /var/lib/vigiloom/agent
    /var/lib/vigiloom/updater /etc/vigiloom/agent.env /etc/vigiloom/updater.env
    /etc/systemd/system/vigiloom-agent.service /etc/systemd/system/vigiloom-updater.service
    /etc/systemd/system/vigiloom-agent.service.d /etc/systemd/system/vigiloom-updater.service.d
    /etc/vigiloom-systemd-test-ca.pem)
for path in "${paths[@]}"; do [[ ! -e $path && ! -L $path ]] || exit 1; done
! getent passwd vigiloom-agent >/dev/null && ! getent group vigiloom-agent >/dev/null || exit 1
! systemctl cat vigiloom-agent.service >/dev/null 2>&1 || exit 1
! systemctl cat vigiloom-updater.service >/dev/null 2>&1 || exit 1

cleanup() {
    local status=$?
    trap - EXIT
    local unit state pid
    # Emit only the known, non-secret status messages; never dump service journals.
    journalctl -u vigiloom-agent.service -u vigiloom-updater.service --no-pager -o cat 2>/dev/null \
        | grep -E '^[[:space:]]*(Agent update handoff (status|unavailable): [A-Za-z]+\.|Updater (request result: [A-Za-z]+, [A-Za-z]*\.|request failed: [A-Za-z]+\.|recovery state: [A-Za-z]+\.))$' \
        | tail -n 50 | sed 's/^[[:space:]]*/SYSTEMD_TEST /' >&2 || true
    # Stop the controller first so it cannot restart the Agent during cleanup.
    for unit in vigiloom-updater.service vigiloom-agent.service; do
        state=$(systemctl show "$unit" --property=LoadState --value)
        if [[ $state == loaded ]]; then
            if ! systemctl disable --now "$unit"; then
                echo "Could not stop and disable $unit; test installation retained." >&2
                exit 1
            fi
            pid=$(systemctl show "$unit" --property=MainPID --value)
            if systemctl is-active --quiet "$unit" || [[ $pid != 0 ]]; then
                echo "$unit is still running; test installation retained." >&2
                exit 1
            fi
        elif [[ $state != not-found ]]; then
            echo "Cannot determine $unit state; test installation retained." >&2
            exit 1
        fi
    done
    rm -f -- /etc/systemd/system/vigiloom-agent.service /etc/systemd/system/vigiloom-updater.service \
        /etc/vigiloom/agent.env /etc/vigiloom/updater.env /etc/vigiloom-systemd-test-ca.pem
    rm -rf -- /etc/systemd/system/vigiloom-agent.service.d /etc/systemd/system/vigiloom-updater.service.d \
        /opt/vigiloom/agent /opt/vigiloom/updater /opt/vigiloom/updater.previous /opt/vigiloom/updater.next \
        /opt/vigiloom/agent-tools /var/lib/vigiloom/agent /var/lib/vigiloom/updater
    systemctl daemon-reload >/dev/null 2>&1 || true
    userdel vigiloom-agent >/dev/null 2>&1 || true
    exit "$status"
}
trap cleanup EXIT

install -m 0644 -- "$ca" /etc/vigiloom-systemd-test-ca.pem
for unit in vigiloom-agent vigiloom-updater; do
    install -d -m 0755 "/etc/systemd/system/$unit.service.d"
    printf '[Service]\nEnvironment=SSL_CERT_FILE=/etc/vigiloom-systemd-test-ca.pem\n' > "/etc/systemd/system/$unit.service.d/test-ca.conf"
done

# Inputs arrive over the parent's private stdin and are never written to the log.
read -r server
read -r device
read -r fingerprint
read -r enrollment
printf '%s\n' "$server" "$device" "$fingerprint" "$enrollment" | bash "$bundle/install.sh" >&2
unset enrollment
echo READY
read -r _ || true
