"""Atomically publish a signed staging repository to a private single-host filesystem store.

This tool does not serve HTTP and does not receive or store signing keys. Readers must
resolve the current generation for each request and expose only its metadata and targets
plus the content-addressed packages. Use a private origin with TLS and read-only access.
"""
from __future__ import annotations

import argparse
import hashlib
import os
import shutil
import tempfile
from datetime import datetime, timezone
from pathlib import Path

from filelock import FileLock
import publisher
from shared_read import replace_published


def sync_directory(path: Path) -> None:
    # Python cannot portably flush a directory handle on Windows. On Unix, sync
    # directory entries in addition to file contents before exposing the pointer.
    if os.name == "nt":
        return
    descriptor = os.open(path, os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(descriptor)
    finally:
        os.close(descriptor)


def current(store: Path) -> str | None:
    pointer = store / "current"
    if not pointer.exists():
        return None
    text = publisher.safe_file(pointer, 65).decode("ascii").strip()
    if not publisher.HASH.fullmatch(text):
        raise ValueError("The publication pointer is invalid.")
    return text


def verify_generation(path: Path, bootstrap: Path) -> tuple[int, list[dict]]:
    roots = publisher.verify_roots(path, bootstrap, allow_expired=True)
    version, packages, _ = publisher.verify_previous(path, roots)
    if version == 0:
        raise ValueError("A generation requires signed publisher metadata.")
    return version, packages


def generation_id(path: Path) -> str:
    return hashlib.sha256(publisher.safe_file(path / "metadata" / "timestamp.json")).hexdigest()


def verify_candidate(path: Path, bootstrap: Path) -> tuple[int, list[dict]]:
    roots = publisher.verify_roots(path, bootstrap)
    version, entries = verify_generation(path, bootstrap)
    now = datetime.now(timezone.utc)
    roles = {role: publisher.Metadata.from_bytes(publisher.safe_file(path / "metadata" / f"{role}.json"))
             for role in ("timestamp", "snapshot", "targets")}
    if roles["targets"].signed.unrecognized_fields.get("vigiloom_root_version") != max(roots):
        raise ValueError("Published metadata must use the current root authority.")
    if any(role.signed.expires <= now for role in roles.values()):
        raise ValueError("Published metadata must not be expired.")
    return version, entries


def verify_history(previous: Path, candidate: Path, versions) -> None:
    for version in versions:
        name = f"{version}.root.json"
        if publisher.safe_file(previous / "metadata" / name) != publisher.safe_file(candidate / "metadata" / name):
            raise ValueError("Published root history cannot be replaced.")


def committed_entries(store: Path, bootstrap: Path) -> list[dict]:
    revision = current(store)
    if revision is None:
        raise ValueError("The publication store is empty.")
    generation = store / "generations" / revision
    _, entries = verify_generation(generation, bootstrap)
    if generation_id(generation) != revision:
        raise ValueError("The publication pointer does not match its generation.")
    return entries


def audit_objects(store: Path, bootstrap: Path) -> list[str]:
    """Rehash the current generation's immutable objects; returns missing or damaged digests."""
    store = store.resolve(strict=True)
    with FileLock(str(store / ".publish.lock"), timeout=30):
        entries = committed_entries(store, bootstrap)
        invalid = []
        for digest, size in sorted({(entry["sha256"], entry["size"]) for entry in entries}):
            path = store / "packages" / digest
            if path.is_symlink() or not path.is_file() or path.stat().st_size != size or publisher.digest_file(path) != digest:
                invalid.append(digest)
        return invalid


def repair_object(store: Path, bootstrap: Path, digest: str, source: Path) -> None:
    """Replace a referenced damaged object only with bytes matching its signed digest and size."""
    if not publisher.HASH.fullmatch(digest):
        raise ValueError("A lowercase SHA-256 object identifier is required.")
    store = store.resolve(strict=True)
    with FileLock(str(store / ".publish.lock"), timeout=30):
        entries = committed_entries(store, bootstrap)
        sizes = {entry["size"] for entry in entries if entry["sha256"] == digest}
        if len(sizes) != 1:
            raise ValueError("The object is not uniquely referenced by the current signed catalogue.")
        objects = store / "packages"
        destination = objects / digest
        if destination.is_symlink():
            raise ValueError("A linked object cannot be repaired.")
        temporary = Path(tempfile.mkdtemp(prefix=".repair-", dir=objects))
        replacement = temporary / digest
        try:
            store_archive(source, replacement, digest, sizes.pop(), False)
        except Exception as error:
            # store_archive removes its own incomplete file; do not recursively
            # remove anything outside this newly created empty directory.
            try:
                temporary.rmdir()
            except OSError as cleanup:
                raise OSError(f"Repair validation failed and {temporary} could not be removed.") from ExceptionGroup(
                    "Validation and cleanup failed", [error, cleanup])
            raise
        try:
            replace_published(replacement, destination)
            sync_directory(objects)
        except OSError as error:
            # ReplaceFileW can fail after moving one of its inputs. Preserve the
            # staged bytes (if still present) for inspection and safe recovery.
            raise OSError(f"Repair outcome is uncertain; inspect {destination} and {replacement} before retrying.") from error
        temporary.rmdir()


def sync_copy(source: Path, destination: Path, maximum: int = publisher.MAX_DOCUMENT_BYTES) -> None:
    data = publisher.safe_file(source, maximum)
    with destination.open("xb") as output:
        output.write(data)
        output.flush()
        os.fsync(output.fileno())


def store_archive(source: Path, destination: Path, expected_hash: str, size: int, previously_published: bool) -> None:
    if destination.exists():
        if destination.is_symlink() or not destination.is_file() or destination.stat().st_size != size or \
           not previously_published and publisher.digest_file(destination) != expected_hash:
            raise ValueError("A stored archive is corrupt or unsafe.")
        return
    if source.is_symlink() or not source.is_file() or source.stat().st_size != size:
        raise ValueError("A referenced archive is unavailable or unsafe.")
    handle, name = tempfile.mkstemp(prefix=".upload-", dir=destination.parent)
    temporary = Path(name)
    try:
        digest = hashlib.sha256()
        count = 0
        with os.fdopen(handle, "wb") as output, source.open("rb") as input_file:
            while block := input_file.read(1024 * 1024):
                count += len(block)
                if count > size:
                    raise ValueError("An archive grew during publication.")
                digest.update(block)
                output.write(block)
            output.flush()
            os.fsync(output.fileno())
        if count != size or digest.hexdigest() != expected_hash:
            raise ValueError("An archive does not match its signed identity.")
        # The temporary file is on the same filesystem. Link creation is exclusive:
        # existing objects are never replaced, even after a concurrent publish.
        os.link(temporary, destination)
        sync_directory(destination.parent)
    finally:
        temporary.unlink(missing_ok=True)


def publish(staging: Path, store: Path, bootstrap: Path, expected: str | None) -> str:
    staging = staging.resolve(strict=True)
    store = store.resolve()
    bootstrap = bootstrap.resolve(strict=True)
    if staging == store or staging.is_relative_to(store) or store.is_relative_to(staging):
        raise ValueError("Staging and publication store must be separate directories.")
    if expected is not None and not publisher.HASH.fullmatch(expected):
        raise ValueError("The expected publication pointer is invalid.")
    next_version, entries = verify_candidate(staging, bootstrap)
    candidate_roots = publisher.verify_roots(staging, bootstrap)
    revision = generation_id(staging)
    store.mkdir(parents=True, exist_ok=True)
    sync_directory(store.parent)
    objects, generations = store / "packages", store / "generations"
    for directory in (objects, generations):
        if directory.is_symlink() or getattr(directory, "is_junction", lambda: False)():
            raise ValueError("Publication storage must not be linked.")
        directory.mkdir(exist_ok=True)
    sync_directory(store)
    with FileLock(str(store / ".publish.lock"), timeout=30):
        previous = current(store)
        if previous != expected:
            raise ValueError("Publication changed; rebuild from the current generation.")
        if previous is None:
            if next_version != 1:
                raise ValueError("The first publication must use metadata version 1.")
            previous_entries: list[dict] = []
            previous_roots = {}
        else:
            previous_path = generations / previous
            previous_roots = publisher.verify_roots(previous_path, bootstrap, allow_expired=True)
            if max(candidate_roots) < max(previous_roots):
                raise ValueError("A published root cannot be rolled back.")
            verify_history(previous_path, staging, previous_roots)
            old_version, previous_entries = verify_generation(previous_path, bootstrap)
            if generation_id(previous_path) != previous or next_version != old_version + 1:
                raise ValueError("The current generation or next metadata version is invalid.")
        indexed = {(item["version"].lower(), item["rid"]): item for item in entries}
        for old in previous_entries:
            new = indexed.get((old["version"].lower(), old["rid"]))
            if new is None or any(old[k] != new[k] for k in ("version", "rid", "size", "sha256", "manifestSha256")) or old["revoked"] and not new["revoked"]:
                raise ValueError("A published package cannot be replaced, removed or unrevoked.")
        previous_objects = {(entry["sha256"], entry["size"]) for entry in previous_entries}
        for entry in entries:
            digest = entry["sha256"]
            store_archive(staging / "packages" / digest, objects / digest, digest, entry["size"], (digest, entry["size"]) in previous_objects)

        destination = generations / revision
        if not destination.exists():
            with tempfile.TemporaryDirectory(prefix=".generation-", dir=generations) as temporary:
                temporary_path = Path(temporary)
                (temporary_path / "metadata").mkdir()
                (temporary_path / "targets").mkdir()
                metadata_names = {"root.json", "timestamp.json", "snapshot.json", "targets.json"} | \
                    {f"{version}.root.json" for version in candidate_roots}
                for name in metadata_names:
                    sync_copy(staging / "metadata" / name, temporary_path / "metadata" / name)
                for name in {"catalog.json"} | {entry["manifestSha256"] + ".json" for entry in entries}:
                    sync_copy(staging / "targets" / name, temporary_path / "targets" / name)
                sync_copy(staging / "published.json", temporary_path / "published.json")
                sync_directory(temporary_path / "metadata")
                sync_directory(temporary_path / "targets")
                sync_directory(temporary_path)
                verify_generation(temporary_path, bootstrap)
                if generation_id(temporary_path) != revision:
                    raise ValueError("The signed generation changed during publication.")
                # Move only an owned temporary directory within generations.
                temporary_path.rename(destination)
                sync_directory(generations)
        if destination.is_symlink() or verify_candidate(destination, bootstrap)[0] != next_version or generation_id(destination) != revision:
            raise ValueError("The candidate generation is invalid.")
        if previous is not None:
            verify_history(generations / previous, destination, previous_roots)
        handle, name = tempfile.mkstemp(prefix=".current-", dir=store)
        temporary_pointer = Path(name)
        try:
            with os.fdopen(handle, "wb") as output:
                output.write((revision + "\n").encode("ascii"))
                output.flush()
                os.fsync(output.fileno())
            os.replace(temporary_pointer, store / "current")
            sync_directory(store)
        finally:
            temporary_pointer.unlink(missing_ok=True)
    return revision


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    operation = parser.add_mutually_exclusive_group(required=True)
    operation.add_argument("--staging", type=Path)
    operation.add_argument("--audit", action="store_true")
    operation.add_argument("--repair", type=Path, help="Verified source archive to restore a referenced object")
    parser.add_argument("--store", required=True, type=Path)
    parser.add_argument("--bootstrap", required=True, type=Path)
    parser.add_argument("--expected", help="Current generation SHA-256, or 'none' for initial publication")
    parser.add_argument("--sha256", help="Object digest to repair")
    args = parser.parse_args()
    if args.audit:
        if args.expected is not None or args.sha256 is not None:
            parser.error("--audit does not accept --expected or --sha256")
        invalid = audit_objects(args.store, args.bootstrap)
        if invalid:
            for digest in invalid:
                print(f"Missing or damaged object: {digest}")
            raise SystemExit(1)
        print("All referenced objects match their signed hashes.")
    elif args.repair:
        if args.sha256 is None or args.expected is not None:
            parser.error("--repair requires --sha256 and does not accept --expected")
        repair_object(args.store, args.bootstrap, args.sha256, args.repair)
        print(f"Repaired object {args.sha256}.")
    else:
        if args.expected is None or args.sha256 is not None:
            parser.error("--staging requires --expected and does not accept --sha256")
        result = publish(args.staging, args.store, args.bootstrap, None if args.expected == "none" else args.expected)
        print(f"Published generation {result}.")


if __name__ == "__main__":
    main()
