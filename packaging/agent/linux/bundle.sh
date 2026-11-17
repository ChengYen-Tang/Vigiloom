#!/usr/bin/env bash
set -Eeuo pipefail

if (( $# != 5 )); then
    printf '%s\n' 'Usage: bundle.sh <agent-publish-dir> <updater-publish-dir> <trusted-root-file> <root-sha256> <output-dir>' >&2
    exit 2
fi
fail() { printf '%s\n' "$1" >&2; exit 1; }
agent=$(cd -- "$1" && pwd -P)
updater=$(cd -- "$2" && pwd -P)
[[ -f $3 && ! -L $3 ]] || fail 'A trusted publisher root file is required.'
root=$(realpath -- "$3")
fingerprint=${4,,}
[[ $fingerprint =~ ^[a-f0-9]{64}$ ]] || fail 'A trusted root SHA-256 fingerprint is required.'
[[ $(sha256sum -- "$root" | cut -d ' ' -f 1) == "$fingerprint" ]] || fail 'The publisher root fingerprint does not match.'
for file in "$agent/Vigiloom.Agent" "$agent/Vigiloom.Agent.dll" "$agent/libcoreclr.so" "$agent/libhostfxr.so" \
    "$updater/Vigiloom.Updater" "$updater/Vigiloom.Updater.dll" "$updater/libcoreclr.so" "$updater/libhostfxr.so"; do
    [[ -f $file && ! -L $file ]] || fail 'Self-contained Linux Agent and Updater publications are required.'
done
[[ -z $(find "$agent" "$updater" -type l -print -quit) ]] || fail 'Published application directories cannot contain symbolic links.'
mkdir -p -- "$5"
output=$(cd -- "$5" && pwd -P)
case "$output/" in "$agent/"*|"$updater/"*) fail 'The output directory must be outside the published applications.' ;; esac
artifact="$output/vigiloom-agent-linux-x64.tar.gz"
( set -o noclobber; : > "$artifact" ) 2>/dev/null || fail 'The output archive already exists.'
stage=
complete=0
cleanup() {
    if [[ -n $stage ]]; then rm -rf -- "$stage"; fi
    if (( ! complete )); then rm -f -- "$artifact"; fi
}
trap cleanup EXIT
stage=$(mktemp -d "$output/.vigiloom-linux-bundle.XXXXXXXX")
mkdir -p -- "$stage/payload/agent" "$stage/payload/updater"
cp -a -- "$agent/." "$stage/payload/agent/"
cp -a -- "$updater/." "$stage/payload/updater/"
cp -- "$root" "$stage/payload/root.json"
[[ $(sha256sum -- "$stage/payload/root.json" | cut -d ' ' -f 1) == "$fingerprint" ]] || fail 'The copied publisher root fingerprint does not match.'
source_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
cp -- "$source_dir/install.sh" "$source_dir/remove.sh" "$source_dir/restore.sh" "$source_dir/repair-updater.sh" "$source_dir/vigiloom-agent.service" "$source_dir/vigiloom-updater.service" "$stage/"
chmod 0755 "$stage/install.sh" "$stage/remove.sh" "$stage/restore.sh" "$stage/repair-updater.sh" "$stage/payload/agent/Vigiloom.Agent" "$stage/payload/updater/Vigiloom.Updater"
tar -C "$stage" -czf "$artifact" .
complete=1
printf 'Created %s\n' "$artifact"
