"""Create a disposable signed release for the cross-process integration test."""
import argparse
import os
import sys
from pathlib import Path
from zipfile import ZipFile

parser = argparse.ArgumentParser()
parser.add_argument("tools", type=Path)
parser.add_argument("output", type=Path)
args = parser.parse_args()
sys.path.insert(0, str(args.tools.resolve(strict=True)))
import bootstrap  # noqa: E402
import publisher  # noqa: E402
import run_release  # noqa: E402

bootstrap_dir = args.output / "bootstrap"
bootstrap.create(bootstrap_dir, "offline-test-password", "online-test-password")
for role in ("targets", "snapshot", "timestamp"):
    os.environ[f"VIGILOOM_{role.upper()}_KEY_PEM"] = (bootstrap_dir / "PRIVATE-KEYS" / f"{role}.pem").read_text()
os.environ["VIGILOOM_RELEASE_KEY_PASSWORD"] = "online-test-password"
artifacts = args.output / "artifacts"
artifacts.mkdir()
for rid in publisher.RIDS:
    with ZipFile(artifacts / f"agent-{rid}.zip", "x") as archive:
        archive.writestr("fixture.txt", "isolated test Agent " + rid)
run_release.run(args.output / "store", bootstrap_dir / "bootstrap.json", bootstrap_dir / "repository", None,
                artifacts, "1.0.0", "1.0.0", [])
