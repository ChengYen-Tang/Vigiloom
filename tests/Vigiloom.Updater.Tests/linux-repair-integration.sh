#!/usr/bin/env bash
set -Eeuo pipefail
if (( $# != 2 )); then echo 'Usage: sudo bash linux-repair-integration.sh <linux-agent-publish-dir> <linux-updater-publish-dir>' >&2; exit 2; fi
agent_publish=$(realpath -- "$1")
updater_publish=$(realpath -- "$2")
base=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd -P)
for p in /opt/vigiloom/agent /opt/vigiloom/updater /opt/vigiloom/updater.previous /opt/vigiloom/updater.next /opt/vigiloom/agent-tools /var/lib/vigiloom/agent /var/lib/vigiloom/updater /etc/vigiloom/agent.env /etc/vigiloom/updater.env /etc/systemd/system/vigiloom-agent.service /etc/systemd/system/vigiloom-updater.service; do [[ ! -e $p && ! -L $p ]] || exit 1; done
! getent passwd vigiloom-agent >/dev/null && ! getent group vigiloom-agent >/dev/null || exit 1
! systemctl cat vigiloom-agent.service >/dev/null 2>&1 || exit 1
! systemctl cat vigiloom-updater.service >/dev/null 2>&1 || exit 1
tmp=$(mktemp -d /run/vigiloom-repair.XXXXXXXX)
cleanup() {
    rc=$?; trap - EXIT
    systemctl disable --now vigiloom-agent.service vigiloom-updater.service >/dev/null 2>&1 || true
    rm -f /etc/systemd/system/vigiloom-agent.service /etc/systemd/system/vigiloom-updater.service /etc/vigiloom/agent.env /etc/vigiloom/updater.env
    systemctl daemon-reload >/dev/null 2>&1 || true
    rm -rf -- /opt/vigiloom/agent /opt/vigiloom/updater /opt/vigiloom/updater.previous /opt/vigiloom/updater.next /opt/vigiloom/agent-tools /var/lib/vigiloom/agent /var/lib/vigiloom/updater "$tmp"
    userdel vigiloom-agent >/dev/null 2>&1 || true
    exit "$rc"
}
trap cleanup EXIT
root=$base/tests/Vigiloom.UpdateSecurity.Tests/Fixtures/PythonTuf/root.json
fp=$(sha256sum "$root" | cut -d ' ' -f1)
mkdir "$tmp/out" "$tmp/bundle"
bash "$base/packaging/agent/linux/bundle.sh" "$agent_publish" "$updater_publish" "$root" "$fp" "$tmp/out"
tar -xzf "$tmp/out/vigiloom-agent-linux-x64.tar.gz" -C "$tmp/bundle"
printf '%s\n' 'https://127.0.0.1:1' '01991234-5678-7abc-8def-123456789abc' "$fp" 'vle_01991234-5678-7abc-8def-123456789abc.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' | bash "$tmp/bundle/install.sh"
for i in {1..30}; do [[ -f /var/lib/vigiloom/agent/identity.dat ]] && break; sleep 1; done
identity=$(sha256sum /var/lib/vigiloom/agent/identity.dat)
trust=$(sha256sum /var/lib/vigiloom/updater/trust/trust.sqlite)
echo next >"$tmp/bundle/payload/updater/repair-test-marker"
bash "$tmp/bundle/repair-updater.sh"
[[ -f /opt/vigiloom/updater/repair-test-marker && ! -e /opt/vigiloom/updater.previous ]]
systemctl is-active --quiet vigiloom-updater.service
[[ $(sha256sum /var/lib/vigiloom/agent/identity.dat) == "$identity" && $(sha256sum /var/lib/vigiloom/updater/trust/trust.sqlite) == "$trust" ]]
echo 'PASS: replacement preserved identity and trust.'
mv "$tmp/bundle/payload/updater/Vigiloom.Updater" "$tmp/bundle/payload/updater/Vigiloom.Updater.real"
cat >"$tmp/bundle/payload/updater/Vigiloom.Updater" <<'SCRIPT'
#!/usr/bin/env bash
if [[ ${1:-} == check-runtime ]]; then exec "$(dirname "$0")/Vigiloom.Updater.real" "$@"; fi
: > TEST_STARTED_PATH
exit 1
SCRIPT
sed -i "s|TEST_STARTED_PATH|$tmp/replacement-started|" "$tmp/bundle/payload/updater/Vigiloom.Updater"
chmod +x "$tmp/bundle/payload/updater/Vigiloom.Updater"
if bash "$tmp/bundle/repair-updater.sh"; then echo 'Expected replacement failure'; exit 1; fi
[[ -f $tmp/replacement-started ]]
[[ -f /opt/vigiloom/updater/repair-test-marker && ! -e /opt/vigiloom/updater.previous ]]
systemctl is-active --quiet vigiloom-updater.service
[[ $(sha256sum /var/lib/vigiloom/agent/identity.dat) == "$identity" && $(sha256sum /var/lib/vigiloom/updater/trust/trust.sqlite) == "$trust" ]]
echo 'PASS: failed replacement restored the previous Updater.'
exec {busy}>>/opt/vigiloom/agent/repair.lock
flock -n "$busy"
if bash "$tmp/bundle/repair-updater.sh"; then echo 'Expected maintenance lock rejection'; exit 1; fi
exec {busy}>&-
systemctl is-active --quiet vigiloom-updater.service
systemctl stop vigiloom-updater.service
mv /opt/vigiloom/updater /opt/vigiloom/updater.previous
cp -a /opt/vigiloom/updater.previous /opt/vigiloom/updater.next
touch /opt/vigiloom/updater.next/.vigiloom-updater-stage
if bash "$tmp/bundle/repair-updater.sh"; then echo 'Expected interrupted-repair recovery notice'; exit 1; fi
[[ -f /opt/vigiloom/updater/repair-test-marker && ! -e /opt/vigiloom/updater.previous && ! -e /opt/vigiloom/updater.next ]]
systemctl is-active --quiet vigiloom-updater.service
[[ $(sha256sum /var/lib/vigiloom/agent/identity.dat) == "$identity" && $(sha256sum /var/lib/vigiloom/updater/trust/trust.sqlite) == "$trust" ]]
echo 'PASS: concurrent repair rejected and interrupted replacement recovered.'
