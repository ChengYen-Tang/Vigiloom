"""Build an immutable, TUF-signed Agent release repository in a staging directory.

Keys are loaded from environment variables by the release workflow. This tool never
uploads artifacts or writes private keys into the output repository.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
from datetime import datetime, timedelta, timezone
from pathlib import Path

import semver
from cryptography.hazmat.primitives.serialization import load_pem_private_key
from securesystemslib.signer import CryptoSigner
from tuf.api.metadata import Metadata, MetaFile, Root, Snapshot, TargetFile, Targets, Timestamp

RIDS = ("win-x64", "win-x86", "linux-x64")
MAX_PACKAGE_BYTES = 2 * 1024 * 1024 * 1024
HASH = re.compile(r"^[0-9a-f]{64}$")
MAX_DOCUMENT_BYTES = 1024 * 1024


def safe_file(path: Path, maximum: int = MAX_DOCUMENT_BYTES) -> bytes:
    if path.is_symlink() or getattr(path, "is_junction", lambda: False)() or not path.is_file():
        raise ValueError("A publisher input is missing or is a link.")
    if path.stat().st_size <= 0 or path.stat().st_size > maximum:
        raise ValueError("A publisher input has an invalid size.")
    with path.open("rb") as stream:
        data = stream.read(maximum + 1)
    if len(data) == 0 or len(data) > maximum:
        raise ValueError("A publisher input changed size while being read.")
    return data


def normalized_version(text: str) -> str:
    value = semver.Version.parse(text)
    if value.build is not None or str(value) != text:
        raise ValueError("A normalized semantic version without build metadata is required.")
    return text


def digest_file(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def read_signed(data: bytes, root: Metadata[Root], role: str) -> Metadata:
    metadata = Metadata.from_bytes(data)
    root.signed.verify_delegate(role, metadata.signed_bytes, metadata.signatures)
    return metadata


def load_signer(role: str, root: Metadata[Root]) -> CryptoSigner:
    name = f"VIGILOOM_{role.upper()}_KEY_PEM"
    pem = os.environ.get(name)
    if not pem:
        raise ValueError(f"Missing release signing key: {name}.")
    password = os.environ.get("VIGILOOM_RELEASE_KEY_PASSWORD")
    private_key = load_pem_private_key(pem.encode(), password.encode() if password else None)
    signer = CryptoSigner(private_key)
    if signer.public_key.keyid not in root.signed.roles[role].keyids:
        raise ValueError(f"Release signing key is not trusted for {role}.")
    return signer


def sign(metadata: Metadata, role: str, root: Metadata[Root], signer: CryptoSigner) -> bytes:
    metadata.sign(signer)
    root.signed.verify_delegate(role, metadata.signed_bytes, metadata.signatures)
    return metadata.to_bytes()


def verify_previous(repo: Path, roots: dict[int, Metadata[Root]]) -> tuple[int, list[dict], dict]:
    metadata_dir = repo / "metadata"
    names = ("timestamp.json", "snapshot.json", "targets.json")
    present = [(metadata_dir / name).exists() for name in names]
    marker = repo / "published.json"
    if not any(present) and not marker.exists():
        if (repo / "targets" / "catalog.json").exists():
            raise ValueError("Existing targets without publisher metadata cannot be initialized.")
        return 0, [], {}
    if not all(present) or not marker.is_file():
        raise ValueError("The previous repository state is incomplete; restore it before publishing.")
    target_bytes = safe_file(metadata_dir / names[2])
    candidate = Metadata.from_bytes(target_bytes)
    if not isinstance(candidate.signed, Targets):
        raise ValueError("The previous targets metadata has an invalid role.")
    root_version = candidate.signed.unrecognized_fields.get("vigiloom_root_version")
    if type(root_version) is not int or root_version not in roots:
        raise ValueError("The previous publisher authority is absent from the trusted root chain.")
    root = roots[root_version]
    timestamp = read_signed(safe_file(metadata_dir / names[0]), root, "timestamp")
    snapshot_bytes = safe_file(metadata_dir / names[1])
    timestamp.signed.snapshot_meta.verify_length_and_hashes(snapshot_bytes)
    snapshot = read_signed(snapshot_bytes, root, "snapshot")
    if snapshot.signed.version != timestamp.signed.snapshot_meta.version:
        raise ValueError("The previous snapshot version does not match timestamp metadata.")
    target_meta = snapshot.signed.meta["targets.json"]
    target_meta.verify_length_and_hashes(target_bytes)
    targets = read_signed(target_bytes, root, "targets")
    if targets.signed.version != target_meta.version:
        raise ValueError("The previous targets version does not match snapshot metadata.")
    catalog_bytes = safe_file(repo / "targets" / "catalog.json")
    targets.signed.targets["catalog.json"].verify_length_and_hashes(catalog_bytes)
    catalog = json.loads(catalog_bytes)
    if not isinstance(catalog, dict) or set(catalog) != {"schema", "packages"} or type(catalog.get("schema")) is not int or \
       catalog["schema"] != 1 or not isinstance(catalog.get("packages"), list):
        raise ValueError("The previous package catalogue is invalid.")
    identities: set[tuple[str, str]] = set()
    if len(catalog["packages"]) > 2000:
        raise ValueError("The previous package catalogue is too large.")
    for item in catalog["packages"]:
        if not isinstance(item, dict) or set(item) != {"version", "rid", "size", "sha256", "manifestSha256", "revoked"} or \
           item.get("rid") not in RIDS or not isinstance(item.get("revoked"), bool):
            raise ValueError("A previously published catalogue entry is invalid.")
        normalized_version(item["version"])
        identity = (item["version"].lower(), item["rid"])
        if identity in identities or type(item.get("size")) is not int or not 0 < item["size"] <= MAX_PACKAGE_BYTES:
            raise ValueError("A previously published package is duplicated or has an invalid size.")
        identities.add(identity)
        if not isinstance(item.get("manifestSha256"), str) or not HASH.fullmatch(item["manifestSha256"]) or \
           not isinstance(item.get("sha256"), str) or not HASH.fullmatch(item["sha256"]):
            raise ValueError("A previously published package has an invalid digest.")
        name = item["manifestSha256"] + ".json"
        target = targets.signed.targets[name]
        manifest_path = repo / "targets" / name
        if manifest_path.is_symlink():
            raise ValueError("A manifest cannot be a symbolic link.")
        manifest_bytes = safe_file(manifest_path)
        target.verify_length_and_hashes(manifest_bytes)
        if hashlib.sha256(manifest_bytes).hexdigest() != item["manifestSha256"]:
            raise ValueError("A manifest does not match its identifier.")
        manifest = json.loads(manifest_bytes)
        fields = {"schema", "version", "rid", "size", "sha256", "minimumUpdaterVersion", "minimumAgentVersion", "maximumAgentVersion"}
        if not isinstance(manifest, dict) or set(manifest) - fields or type(manifest.get("schema")) is not int or manifest["schema"] != 1 or \
           any(manifest.get(k) != item[k] for k in ("version", "rid", "size", "sha256")):
            raise ValueError("A manifest does not match the published catalogue.")
        normalized_version(manifest["minimumUpdaterVersion"])
        minimum = normalized_version(manifest["minimumAgentVersion"]) if "minimumAgentVersion" in manifest else None
        maximum = normalized_version(manifest["maximumAgentVersion"]) if "maximumAgentVersion" in manifest else None
        if minimum and maximum and semver.Version.parse(minimum) > semver.Version.parse(maximum):
            raise ValueError("A previously published manifest has an invalid compatibility range.")
    expected_targets = {"catalog.json"} | {item["manifestSha256"] + ".json" for item in catalog["packages"]}
    if set(targets.signed.targets) != expected_targets:
        raise ValueError("The previous targets metadata contains unexpected or missing files.")
    state = json.loads(safe_file(marker))
    version = state.get("metadataVersion")
    if type(version) is not int or version <= 0 or any(
        item.signed.version != version for item in (timestamp, snapshot, targets)
    ):
        raise ValueError("Publisher version state does not match signed metadata.")
    return version, catalog["packages"], targets.signed.targets


def verify_roots(source: Path, bootstrap: Path, allow_expired: bool = False) -> dict[int, Metadata[Root]]:
    initial_bytes = safe_file(bootstrap)
    current = Metadata.from_bytes(initial_bytes)
    if not isinstance(current.signed, Root):
        raise ValueError("Bootstrap metadata is not a TUF root.")
    current.signed.verify_delegate("root", current.signed_bytes, current.signatures)
    latest_bytes = safe_file(source / "metadata" / "root.json")
    latest = Metadata.from_bytes(latest_bytes)
    if not isinstance(latest.signed, Root) or latest.signed.version < current.signed.version:
        raise ValueError("The repository root is older than the bootstrap root.")
    if latest.signed.version - current.signed.version > 4096:
        raise ValueError("The root history exceeds the supported limit.")
    roots = {}
    first_version = current.signed.version
    for version in range(current.signed.version, latest.signed.version + 1):
        path = source / "metadata" / f"{version}.root.json"
        if path.is_symlink():
            raise ValueError("Root history cannot contain symbolic links.")
        data = safe_file(path)
        next_root = Metadata.from_bytes(data)
        if not isinstance(next_root.signed, Root) or next_root.signed.version != version:
            raise ValueError("The root history is incomplete or invalid.")
        if version == first_version and data != initial_bytes:
            raise ValueError("The root history does not match the independently trusted bootstrap.")
        current.signed.verify_delegate("root", next_root.signed_bytes, next_root.signatures)
        next_root.signed.verify_delegate("root", next_root.signed_bytes, next_root.signatures)
        current = next_root
        roots[version] = current
    if data != latest_bytes or current.signed.consistent_snapshot or not allow_expired and current.signed.expires <= datetime.now(timezone.utc):
        raise ValueError("The current root is invalid, expired, or does not match its history.")
    return roots


def build(args: argparse.Namespace) -> None:
    if (args.version is None) != (args.artifacts is None) or (args.version is not None and args.minimum_updater is None):
        raise ValueError("A release requires a version, artifacts and minimum Updater version together.")
    if args.version is None and (args.minimum_updater or args.minimum_agent or args.maximum_agent):
        raise ValueError("Compatibility options only apply to a new release.")
    version = normalized_version(args.version) if args.version else None
    minimum_updater = normalized_version(args.minimum_updater) if args.minimum_updater else None
    minimum_agent = normalized_version(args.minimum_agent) if args.minimum_agent else None
    maximum_agent = normalized_version(args.maximum_agent) if args.maximum_agent else None
    if minimum_agent and maximum_agent and semver.Version.parse(minimum_agent) > semver.Version.parse(maximum_agent):
        raise ValueError("The Agent compatibility range is invalid.")
    source = args.previous.resolve(strict=True)
    output = args.output.resolve()
    if output.exists() or output.is_relative_to(source) or source.is_relative_to(output):
        raise ValueError("Output must be a new directory separate from the previous repository.")
    for subdir in ("metadata", "targets", "packages"):
        path = source / subdir
        if path.is_symlink() or getattr(path, "is_junction", lambda: False)():
            raise ValueError("The previous repository must not contain linked storage directories.")
    roots = verify_roots(source, args.bootstrap)
    root = roots[max(roots)]
    previous_version, packages, existing_targets = verify_previous(source, roots)
    signers = {role: load_signer(role, root) for role in ("targets", "snapshot", "timestamp")}
    if len(packages) + (len(RIDS) if version else 0) > 2000:
        raise ValueError("The package catalogue is full.")
    output.mkdir(parents=True)
    for subdir in ("metadata", "targets", "packages"):
        (output / subdir).mkdir()
    for version_number in range(Metadata.from_bytes(safe_file(args.bootstrap)).signed.version, root.signed.version + 1):
        name = f"{version_number}.root.json"
        shutil.copyfile(source / "metadata" / name, output / "metadata" / name)
    shutil.copyfile(source / "metadata" / "root.json", output / "metadata" / "root.json")
    for item in packages:
        name = item["manifestSha256"] + ".json"
        shutil.copyfile(source / "targets" / name, output / "targets" / name)
    targets = dict(existing_targets)
    identities = {(p["version"].lower(), p["rid"]) for p in packages}
    indexed = {(p["version"].lower(), p["rid"]): p for p in packages}
    for item in getattr(args, "revoke", None) or []:
        parts = item.split("@")
        if len(parts) != 2 or parts[1] not in RIDS:
            raise ValueError("A revoked package must use VERSION@RID.")
        normalized_version(parts[0])
        entry = indexed.get((parts[0].lower(), parts[1]))
        if entry is None:
            raise ValueError("The package to revoke is not in the previous catalogue.")
        entry["revoked"] = True
    for rid in RIDS if version else ():
        if (version.lower(), rid) in identities:
            raise ValueError("A published version and runtime identifier cannot be replaced.")
        archive = args.artifacts / f"agent-{rid}.zip"
        if archive.is_symlink() or not archive.is_file():
            raise ValueError(f"Archive for {rid} is missing or is a symbolic link.")
        size = archive.stat().st_size
        if size <= 0 or size > MAX_PACKAGE_BYTES:
            raise ValueError(f"Invalid archive size for {rid}.")
        sha256 = digest_file(archive)
        destination = output / "packages" / sha256
        if destination.exists():
            if destination.stat().st_size != size or digest_file(destination) != sha256:
                raise ValueError("An existing content-addressed package is corrupt.")
        else:
            shutil.copyfile(archive, destination)
            if destination.stat().st_size != size or digest_file(destination) != sha256:
                raise ValueError("An archive changed while being copied.")
        manifest = {"schema": 1, "version": version, "rid": rid, "size": size,
                    "sha256": sha256, "minimumUpdaterVersion": minimum_updater}
        if minimum_agent is not None:
            manifest["minimumAgentVersion"] = minimum_agent
        if maximum_agent is not None:
            manifest["maximumAgentVersion"] = maximum_agent
        data = json.dumps(manifest, separators=(",", ":"), ensure_ascii=False).encode()
        manifest_hash = hashlib.sha256(data).hexdigest()
        name = manifest_hash + ".json"
        (output / "targets" / name).write_bytes(data)
        targets[name] = TargetFile.from_data(name, data, ["sha256"])
        packages.append({"version": version, "rid": rid, "size": size, "sha256": sha256,
                         "manifestSha256": manifest_hash, "revoked": False})
    catalog = json.dumps({"schema": 1, "packages": packages}, separators=(",", ":"), ensure_ascii=False).encode()
    if len(catalog) > 1024 * 1024:
        raise ValueError("The signed catalogue exceeds the client document limit.")
    (output / "targets" / "catalog.json").write_bytes(catalog)
    targets["catalog.json"] = TargetFile.from_data("catalog.json", catalog, ["sha256"])
    next_version = previous_version + 1
    now = datetime.now(timezone.utc)
    targets_data = sign(Metadata(Targets(version=next_version, expires=now + timedelta(days=30), targets=targets,
        unrecognized_fields={"vigiloom_root_version": root.signed.version})), "targets", root, signers["targets"])
    snapshot = Metadata(Snapshot(version=next_version, expires=now + timedelta(days=7),
                                meta={"targets.json": MetaFile.from_data(next_version, targets_data, ["sha256"])}))
    snapshot_data = sign(snapshot, "snapshot", root, signers["snapshot"])
    timestamp = Metadata(Timestamp(version=next_version, expires=now + timedelta(days=1),
                                  snapshot_meta=MetaFile.from_data(next_version, snapshot_data, ["sha256"])))
    timestamp_data = sign(timestamp, "timestamp", root, signers["timestamp"])
    for name, data in (("targets.json", targets_data), ("snapshot.json", snapshot_data), ("timestamp.json", timestamp_data)):
        if len(data) > 1024 * 1024:
            raise ValueError(f"{name} exceeds the client document limit.")
        (output / "metadata" / name).write_bytes(data)
    (output / "published.json").write_text(json.dumps({"metadataVersion": next_version}) + "\n", encoding="utf-8")
    verified_roots = verify_roots(output, args.bootstrap)
    verify_previous(output, verified_roots)
    print(f"Prepared signed Agent {'release ' + version if version else 'metadata refresh'} in {output}.")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--previous", type=Path, required=True, help="Last complete TUF repository, or initial root repository")
    parser.add_argument("--bootstrap", type=Path, required=True, help="Independently trusted initial public TUF root")
    parser.add_argument("--output", type=Path, required=True, help="New staging directory; never the live repository")
    parser.add_argument("--artifacts", type=Path, help="Directory containing agent-<rid>.zip for all supported RIDs")
    parser.add_argument("--version", help="New release version; omit to refresh metadata only")
    parser.add_argument("--minimum-updater")
    parser.add_argument("--minimum-agent")
    parser.add_argument("--maximum-agent")
    parser.add_argument("--revoke", action="append", help="Revoke an existing VERSION@RID; can be repeated")
    build(parser.parse_args())


if __name__ == "__main__":
    main()
