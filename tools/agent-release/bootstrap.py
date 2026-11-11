"""Create a new public TUF root and encrypted publisher keys for offline custody."""
import argparse
import getpass
from datetime import datetime, timedelta, timezone
from pathlib import Path

from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.hazmat.primitives.serialization import BestAvailableEncryption, Encoding, PrivateFormat
from securesystemslib.signer import CryptoSigner
from tuf.api.metadata import Metadata, Root


def create_keys(destination: Path, root_password: str, online_password: str) -> dict[str, CryptoSigner]:
    if min(len(root_password), len(online_password)) < 16 or root_password == online_password:
        raise ValueError("Use separate root and online key passwords of at least 16 characters.")
    if destination.resolve().is_relative_to(Path(__file__).resolve().parents[2]):
        raise ValueError("The signing-key directory must be outside the source repository.")
    destination.mkdir(parents=True, exist_ok=False)
    keys_dir = destination / "PRIVATE-KEYS"
    keys_dir.mkdir(mode=0o700)
    signers = {}
    for role in ("root", "targets", "snapshot", "timestamp"):
        key = ec.generate_private_key(ec.SECP256R1())
        signer = CryptoSigner(key)
        signers[role] = signer
        password = root_password if role == "root" else online_password
        encrypted = key.private_bytes(Encoding.PEM, PrivateFormat.PKCS8, BestAvailableEncryption(password.encode()))
        # Files are encrypted independently of filesystem permissions, including on Windows.
        with (keys_dir / f"{role}.pem").open("xb") as output:
            output.write(encrypted)
    return signers


def create(destination: Path, root_password: str, online_password: str) -> None:
    signers = create_keys(destination, root_password, online_password)
    metadata_dir = destination / "repository" / "metadata"
    metadata_dir.mkdir(parents=True)
    root = Metadata(Root(version=1, expires=datetime.now(timezone.utc) + timedelta(days=730), consistent_snapshot=False))
    for role, signer in signers.items():
        root.signed.add_key(signer.public_key, role)
    root.sign(signers["root"])
    root.signed.verify_delegate("root", root.signed_bytes, root.signatures)
    public_root = root.to_bytes()
    (destination / "bootstrap.json").write_bytes(public_root)
    (metadata_dir / "root.json").write_bytes(public_root)
    (metadata_dir / "1.root.json").write_bytes(public_root)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("destination", type=Path, help="New private directory outside the repository")
    args = parser.parse_args()
    root_password = getpass.getpass("Password for the offline root key: ")
    if root_password != getpass.getpass("Repeat root password: "):
        raise ValueError("The passwords do not match.")
    online_password = getpass.getpass("Password for online release keys: ")
    if online_password != getpass.getpass("Repeat online password: "):
        raise ValueError("The passwords do not match.")
    create(args.destination, root_password, online_password)
    print("Created public bootstrap and encrypted keys. Store the root key offline and back up the password securely.")


if __name__ == "__main__":
    main()
