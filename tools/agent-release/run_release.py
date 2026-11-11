"""Build and atomically publish a release or metadata refresh on the private publisher host."""
import argparse
import re
import tempfile
from pathlib import Path

import publisher
import repository_store


def copy_previous(source: Path, destination: Path, root_updates: Path | None) -> None:
    """Copy only publisher metadata and targets, never historical archives or private keys."""
    for directory in ("metadata", "targets", "packages"):
        (destination / directory).mkdir()
    allowed_metadata = re.compile(r"^(?:root|timestamp|snapshot|targets|[1-9][0-9]*\.root)\.json$")
    allowed_target = re.compile(r"^(?:catalog|[0-9a-f]{64})\.json$")
    for category, pattern in (("metadata", allowed_metadata), ("targets", allowed_target)):
        directory = source / category
        if directory.is_symlink() or getattr(directory, "is_junction", lambda: False)():
            raise ValueError("The previous repository contains a linked directory.")
        if directory.exists():
            for item in directory.iterdir():
                if not pattern.fullmatch(item.name):
                    raise ValueError("The previous repository contains an unexpected file.")
                repository_store.sync_copy(item, destination / category / item.name)
    if (source / "published.json").exists():
        repository_store.sync_copy(source / "published.json", destination / "published.json")
    if root_updates is not None:
        if root_updates.is_symlink() or getattr(root_updates, "is_junction", lambda: False)():
            raise ValueError("The root update directory cannot be linked.")
        for item in root_updates.iterdir():
            if item.name != "root.json" and not re.fullmatch(r"[1-9][0-9]*\.root\.json", item.name):
                raise ValueError("The root update directory contains an unexpected file.")
            data = publisher.safe_file(item)
            target = destination / "metadata" / item.name
            if target.exists() and item.name != "root.json":
                if publisher.safe_file(target) != data:
                    raise ValueError("An existing root history entry cannot change.")
            else:
                target.write_bytes(data)


def run(store: Path, bootstrap: Path, initial: Path | None, root_updates: Path | None,
        artifacts: Path | None, version: str | None, minimum_updater: str | None,
        revoke: list[str]) -> str:
    expected = repository_store.current(store)
    previous = store / "generations" / expected if expected is not None else initial
    if previous is None or not previous.is_dir():
        raise ValueError("A trusted initial root repository is required for the first publication.")
    with tempfile.TemporaryDirectory(prefix="vigiloom-publisher-") as temporary:
        scratch = Path(temporary)
        source, staged = scratch / "previous", scratch / "candidate"
        source.mkdir()
        copy_previous(previous, source, root_updates)
        publisher.build(argparse.Namespace(previous=source, bootstrap=bootstrap, output=staged, artifacts=artifacts,
            version=version, minimum_updater=minimum_updater, minimum_agent=None, maximum_agent=None, revoke=revoke))
        return repository_store.publish(staged, store, bootstrap, expected)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--store", type=Path, required=True)
    parser.add_argument("--bootstrap", type=Path, required=True)
    parser.add_argument("--initial", type=Path)
    parser.add_argument("--root-updates", type=Path)
    parser.add_argument("--artifacts", type=Path)
    parser.add_argument("--version")
    parser.add_argument("--minimum-updater")
    parser.add_argument("--revoke", action="append", default=[])
    args = parser.parse_args()
    revision = run(args.store, args.bootstrap, args.initial, args.root_updates, args.artifacts,
                   args.version, args.minimum_updater, args.revoke)
    print(f"Published generation {revision}.")


if __name__ == "__main__":
    main()
