"""Archive a .NET Agent publish directory for one supported runtime."""
import argparse
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile



def package(publish_directory: Path, output_directory: Path, rid: str,
            updater_directory: Path | None = None) -> Path:
    if rid not in ("win-x64", "win-x86", "linux-x64"):
        raise ValueError("Unsupported Agent runtime identifier.")
    source = publish_directory.resolve(strict=True)
    output = output_directory.resolve()
    if output == source or output.is_relative_to(source):
        raise ValueError("The archive destination must be outside the publish directory.")
    executable = source / ("Vigiloom.Agent.exe" if rid.startswith("win-") else "Vigiloom.Agent")
    if not executable.is_file() or executable.is_symlink():
        raise ValueError("The published Agent executable is missing.")
    updater = updater_directory.resolve(strict=True) if updater_directory is not None else None
    if updater is not None:
        updater_executable = updater / ("Vigiloom.Updater.exe" if rid.startswith("win-") else "Vigiloom.Updater")
        if not updater_executable.is_file() or updater_executable.is_symlink():
            raise ValueError("The published Updater executable is missing.")
        if output == updater or output.is_relative_to(updater):
            raise ValueError("The archive destination must be outside the Updater publish directory.")
        if (source / "updater").exists():
            raise ValueError("The Agent publish directory reserves the updater directory.")
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
                if updater is not None:
                    for path in sorted(updater.rglob("*")):
                        if path.is_symlink() or getattr(path, "is_junction", lambda: False)():
                            raise ValueError("The Updater publish directory must not contain links.")
                        if path.is_file():
                            result.write(path, (Path("updater") / path.relative_to(updater)).as_posix())
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
    parser.add_argument("--updater-directory", type=Path)
    args = parser.parse_args()
    print(package(args.publish_directory, args.output_directory, args.rid, args.updater_directory))


if __name__ == "__main__":
    main()
