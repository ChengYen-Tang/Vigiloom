"""Archive a .NET Agent publish directory for one supported runtime."""
import argparse
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile

import publisher


def package(publish_directory: Path, output_directory: Path, rid: str, version: str) -> Path:
    publisher.normalized_version(version)
    if rid not in publisher.RIDS:
        raise ValueError("Unsupported Agent runtime identifier.")
    source = publish_directory.resolve(strict=True)
    output = output_directory.resolve()
    if output == source or output.is_relative_to(source):
        raise ValueError("The archive destination must be outside the publish directory.")
    executable = source / ("Vigiloom.Agent.exe" if rid.startswith("win-") else "Vigiloom.Agent")
    if not executable.is_file() or executable.is_symlink():
        raise ValueError("The published Agent executable is missing.")
    output.mkdir(parents=True, exist_ok=True)
    archive = output / f"agent-{rid}.zip"
    # Reserve the output before writing so an error cannot delete a pre-existing file.
    with archive.open("xb") as output_file:
        try:
            with ZipFile(output_file, mode="w", compression=ZIP_DEFLATED, compresslevel=6, allowZip64=True) as result:
                for path in sorted(source.rglob("*")):
                    if path.is_symlink() or getattr(path, "is_junction", lambda: False)():
                        raise ValueError("The Agent publish directory must not contain links.")
                    if path.is_file():
                        result.write(path, path.relative_to(source).as_posix())
        except Exception:
            output_file.close()
            archive.unlink(missing_ok=True)
            raise
    return archive


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--publish-directory", type=Path, required=True)
    parser.add_argument("--output-directory", type=Path, required=True)
    parser.add_argument("--rid", required=True)
    parser.add_argument("--version", required=True)
    args = parser.parse_args()
    print(package(args.publish_directory, args.output_directory, args.rid, args.version))


if __name__ == "__main__":
    main()
