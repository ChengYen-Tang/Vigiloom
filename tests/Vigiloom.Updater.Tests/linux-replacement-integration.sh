#!/usr/bin/env bash
set -Eeuo pipefail

# Run only on a disposable systemd test machine without an installed Vigiloom Agent.
[[ $(id -u) == 0 && -d /run/systemd/system ]] || { echo 'Root and systemd are required.' >&2; exit 1; }
workspace=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd -P)
agent=/opt/vigiloom/agent
updater=/opt/vigiloom/updater
state=/var/lib/vigiloom/req20-replacement-test
unit=/etc/systemd/system/vigiloom-updater.service
for path in "$agent" "$updater" /opt/vigiloom/updater.next /opt/vigiloom/updater.previous "$state" "$unit"; do
    [[ ! -e $path && ! -L $path ]] || { echo "Existing installation path: $path" >&2; exit 1; }
done
for path in /etc/systemd/system/vigiloom-updater.service.d /run/systemd/system/vigiloom-updater.service.d /usr/lib/systemd/system/vigiloom-updater.service.d; do
    [[ ! -e $path && ! -L $path ]] || { echo "Existing service configuration: $path" >&2; exit 1; }
done
[[ $(systemctl show vigiloom-updater.service --property=LoadState --value) == not-found ]] || {
    echo 'A Vigiloom Updater service already exists.' >&2; exit 1;
}
for operation in 0199123456787abc8def123456789abc 0199123456787abc8def123456789abd; do
    for prefix in vigiloom-updater-replace vigiloom-req20-test; do
        [[ $(systemctl show "$prefix-$operation.service" --property=LoadState --value) == not-found ]] || {
            echo 'A replacement test unit already exists.' >&2; exit 1;
        }
    done
done
created_opt=0; created_state=0
current_helper=''
[[ -d /opt/vigiloom ]] || { mkdir -p /opt/vigiloom; created_opt=1; }
[[ -d /var/lib/vigiloom ]] || { mkdir -p /var/lib/vigiloom; created_state=1; }
stop_unit() {
    local name=$1 state
    state=$(systemctl show "$name" --property=LoadState --value) || return 1
    if [[ $state == loaded ]]; then
        systemctl stop "$name" || return 1
        ! systemctl is-active --quiet "$name" || return 1
    elif [[ $state != not-found ]]; then
        return 1
    fi
}
cleanup_case() {
    stop_unit vigiloom-updater.service || { echo 'Updater is still running; test installation retained.' >&2; return 1; }
    if [[ -n $current_helper ]]; then
        stop_unit "$current_helper" || { echo 'Replacement helper is still running; test installation retained.' >&2; return 1; }
    fi
    stop_unit vigiloom-updater.service || { echo 'Updater restarted during cleanup; test installation retained.' >&2; return 1; }
    rm -f -- "$unit"
    systemctl daemon-reload
    rm -rf -- "$agent" "$updater" /opt/vigiloom/updater.next /opt/vigiloom/updater.previous "$state"
    current_helper=''
}
cleanup() {
    cleanup_case
    [[ $created_opt == 0 ]] || rmdir /opt/vigiloom
    [[ $created_state == 0 ]] || rmdir /var/lib/vigiloom
}
trap cleanup EXIT

run_case() {
    local mode=$1 operation=$2 source status old_pid new_pid
    source=$agent/staging/0199123456787abc8def123456789abc/updater
    mkdir -p "$updater" "$source" "$state"
    cat > "$updater/Vigiloom.Updater" <<'OLD'
#!/bin/bash
if [[ ${1:-} == recovery-status ]]; then echo ready; exit 0; fi
while true; do sleep 1; done
OLD
    if [[ $mode == success ]]; then
        cp -- "$updater/Vigiloom.Updater" "$source/Vigiloom.Updater"
        echo '# replacement' >> "$source/Vigiloom.Updater"
    else
        printf '#!/bin/bash\nexit 1\n' > "$source/Vigiloom.Updater"
    fi
    chmod 755 "$updater/Vigiloom.Updater" "$source/Vigiloom.Updater"
    cat > "$unit" <<'UNIT'
[Unit]
Description=Vigiloom isolated updater replacement test
[Service]
Type=simple
ExecStart=/opt/vigiloom/updater/Vigiloom.Updater
UNIT
    systemctl daemon-reload
    systemctl start vigiloom-updater.service
    old_pid=$(systemctl show -p MainPID --value vigiloom-updater.service)
    if [[ -n ${VIGILOOM_TEST_REPLACEMENT_LAUNCHER:-} ]]; then
        current_helper="vigiloom-updater-replace-$operation.service"
        "$VIGILOOM_TEST_REPLACEMENT_LAUNCHER" "$agent" "$source" "$state" "$operation"
    else
        current_helper="vigiloom-req20-test-$operation.service"
        cp -- "$workspace/src/Updater/Vigiloom.Updater/Maintenance/replace-linux.sh" "$state/replace.sh"
        systemd-run --unit="vigiloom-req20-test-$operation" --collect --property=Type=exec /bin/bash \
            "$state/replace.sh" "$agent" "$source" "$state" "$operation"
    fi
    for _ in {1..60}; do
        status=$(cat "$state/updater-replacement.status" 2>/dev/null || true)
        [[ $status == "$operation succeeded" || $status == "$operation failed "* ]] && break
        sleep 1
    done
    if [[ $mode == success ]]; then
        [[ $status == "$operation succeeded" ]]
        new_pid=$(systemctl show -p MainPID --value vigiloom-updater.service)
        [[ $old_pid != "$new_pid" && $new_pid != 0 && ! -e /opt/vigiloom/updater.previous && ! -e /opt/vigiloom/updater.next ]]
        grep -q '# replacement' "$updater/Vigiloom.Updater"
    else
        [[ $status == "$operation failed "* && -f /opt/vigiloom/updater.previous/Vigiloom.Updater ]]
        grep -q 'exit 1' "$updater/Vigiloom.Updater"
        ! systemctl is-active --quiet vigiloom-updater.service
    fi
    cleanup_case
    echo "Updater replacement $mode verified."
}

run_case success 0199123456787abc8def123456789abc
run_case failure 0199123456787abc8def123456789abd
