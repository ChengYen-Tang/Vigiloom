"""Create the next TUF root signed by both the current and new offline root keys."""
import argparse
import getpass
from datetime import datetime, timedelta, timezone
from pathlib import Path

from cryptography.hazmat.primitives.serialization import load_pem_private_key
from securesystemslib.signer import CryptoSigner
from tuf.api.metadata import Metadata, Root

import bootstrap
import publisher


def rotate(previous: Path, trusted_bootstrap: Path, old_root_key: Path, old_password: str,
           destination: Path, new_root_password: str, new_online_password: str) -> None:
    if destination.resolve().is_relative_to(previous.resolve()):
        raise ValueError("Replacement keys must be stored outside the published repository.")
    if new_online_password == old_password:
        raise ValueError("The online password must differ from the previous offline root password.")
    roots = publisher.verify_roots(previous, trusted_bootstrap, allow_expired=True)
    current = roots[max(roots)]
    publisher.verify_previous(previous, roots)
    old_key = CryptoSigner(load_pem_private_key(old_root_key.read_bytes(), old_password.encode()))
    if old_key.public_key.keyid not in current.signed.roles["root"].keyids:
        raise ValueError("The old root key is not trusted by the current root.")
    new_keys = bootstrap.create_keys(destination, new_root_password, new_online_password)
    next_root = Metadata(Root(version=current.signed.version + 1,
                              expires=datetime.now(timezone.utc) + timedelta(days=730), consistent_snapshot=False))
    for role, signer in new_keys.items():
        next_root.signed.add_key(signer.public_key, role)
    next_root.sign(old_key)
    next_root.sign(new_keys["root"], append=True)
    current.signed.verify_delegate("root", next_root.signed_bytes, next_root.signatures)
    next_root.signed.verify_delegate("root", next_root.signed_bytes, next_root.signatures)
    public_dir = destination / "PUBLIC-ROOT"
    public_dir.mkdir()
    (public_dir / "root.json").write_bytes(next_root.to_bytes())
    (public_dir / f"{next_root.signed.version}.root.json").write_bytes(next_root.to_bytes())


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--previous", type=Path, required=True)
    parser.add_argument("--bootstrap", type=Path, required=True)
    parser.add_argument("--old-root-key", type=Path, required=True)
    parser.add_argument("--destination", type=Path, required=True, help="New private directory outside the repository")
    args = parser.parse_args()
    old_password = getpass.getpass("Current offline root key password: ")
    new_root_password = getpass.getpass("New offline root key password: ")
    if new_root_password != getpass.getpass("Repeat new root password: "):
        raise ValueError("The root passwords do not match.")
    new_online_password = getpass.getpass("New online key password: ")
    if new_online_password != getpass.getpass("Repeat new online password: "):
        raise ValueError("The online passwords do not match.")
    rotate(args.previous, args.bootstrap, args.old_root_key, old_password, args.destination, new_root_password, new_online_password)
    print("Created a cross-signed public root and encrypted replacement keys; keep both generations backed up until deployment completes.")


if __name__ == "__main__":
    main()
