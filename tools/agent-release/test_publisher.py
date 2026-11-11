import argparse
import json
import os
import tempfile
import unittest
import subprocess
import sys
import time
import http.client
import socket
from zipfile import ZipFile
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timedelta, timezone
from pathlib import Path
from unittest.mock import patch

from securesystemslib.signer import CryptoSigner
from tuf.api.metadata import Metadata, MetaFile, Root

import publisher
import bootstrap
import rotate_root
import repository_store
import origin
import package
import run_release


class PublisherTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="vigiloom-release-test-")
        self.addCleanup(self.temporary.cleanup)
        self.path = Path(self.temporary.name)
        self.source = self.path / "initial"
        (self.source / "metadata").mkdir(parents=True)
        self.keys = {role: CryptoSigner.generate_ecdsa() for role in ("root", "targets", "snapshot", "timestamp")}
        root = Metadata(Root(version=1, expires=datetime.now(timezone.utc) + timedelta(days=365), consistent_snapshot=False))
        for role, key in self.keys.items():
            root.signed.add_key(key.public_key, role)
        root.sign(self.keys["root"])
        self.root_bytes = root.to_bytes()
        (self.source / "metadata" / "root.json").write_bytes(self.root_bytes)
        (self.source / "metadata" / "1.root.json").write_bytes(self.root_bytes)
        self.bootstrap = self.path / "bootstrap.json"
        self.bootstrap.write_bytes(self.root_bytes)
        self.artifacts = self.path / "artifacts"
        self.artifacts.mkdir()
        for rid in publisher.RIDS:
            (self.artifacts / f"agent-{rid}.zip").write_bytes(("test archive " + rid).encode())

    def build(self, source: Path, output: Path, version: str | None, keys: dict | None = None, revoke=None):
        signers = keys or self.keys
        environment = {f"VIGILOOM_{role.upper()}_KEY_PEM": signers[role].private_bytes.decode()
                       for role in ("targets", "snapshot", "timestamp")}
        args = argparse.Namespace(previous=source, bootstrap=self.bootstrap, output=output,
                                  artifacts=self.artifacts if version else None, version=version,
                                  minimum_updater="1.0.0" if version else None,
                                  minimum_agent=None, maximum_agent=None, revoke=revoke)
        with patch.dict(os.environ, environment, clear=False):
            os.environ.pop("VIGILOOM_RELEASE_KEY_PASSWORD", None)
            publisher.build(args)

    def test_build_and_refresh_preserve_packages_and_exclude_extra_files(self):
        (self.source / "private.txt").write_text("must not be published", encoding="utf-8")
        first = self.path / "first"
        self.build(self.source, first, "1.0.0")
        self.assertFalse((first / "private.txt").exists())
        catalog = (first / "targets" / "catalog.json").read_bytes()
        self.assertEqual(3, len(json.loads(catalog)["packages"]))
        second = self.path / "second"
        self.build(first, second, None)
        self.assertEqual(catalog, (second / "targets" / "catalog.json").read_bytes())
        self.assertEqual(3, len(list((first / "packages").iterdir())))
        self.assertEqual([], list((second / "packages").iterdir()))
        self.assertEqual(2, json.loads((second / "published.json").read_text())["metadataVersion"])
        with self.assertRaises(ValueError):
            self.build(second, self.path / "duplicate", "1.0.0")

    def test_package_contains_agent_files_and_refuses_overwrite(self):
        directory = self.path / "agent-publish"
        directory.mkdir()
        executable = directory / "Vigiloom.Agent"
        executable.write_bytes(b"test executable")
        executable.chmod(0o755)
        (directory / "appsettings.json").write_text("{}", encoding="utf-8")
        output = self.path / "archives"
        archive = package.package(directory, output, "linux-x64", "1.2.3")
        with ZipFile(archive) as result:
            self.assertEqual(b"test executable", result.read("Vigiloom.Agent"))
            if os.name != "nt":
                self.assertNotEqual(0, (result.getinfo("Vigiloom.Agent").external_attr >> 16) & 0o111)
        with self.assertRaises(FileExistsError):
            package.package(directory, output, "linux-x64", "1.2.3")
        with self.assertRaises(ValueError):
            package.package(directory, output, "linux-x86", "1.2.3")

    def test_release_pipeline_builds_and_publishes_from_the_current_generation(self):
        store = self.path / "private-store"
        environment = {f"VIGILOOM_{role.upper()}_KEY_PEM": self.keys[role].private_bytes.decode()
                       for role in ("targets", "snapshot", "timestamp")}
        with patch.dict(os.environ, environment, clear=False):
            os.environ.pop("VIGILOOM_RELEASE_KEY_PASSWORD", None)
            first = run_release.run(store, self.bootstrap, self.source, None, self.artifacts, "1.0.0", "1.0.0", [])
            second = run_release.run(store, self.bootstrap, None, None, None, None, None, ["1.0.0@win-x64"])
        self.assertNotEqual(first, second)
        self.assertEqual(second, repository_store.current(store))
        entries = repository_store.committed_entries(store, self.bootstrap)
        self.assertTrue(next(item for item in entries if item["rid"] == "win-x64")["revoked"])
        self.assertEqual(3, len(list((store / "packages").iterdir())))

    def test_revocation_updates_signed_catalogue_without_copying_archives(self):
        first = self.path / "first"
        self.build(self.source, first, "1.0.0")
        second = self.path / "revoked"
        self.build(first, second, None, revoke=["1.0.0@win-x64"])
        packages = json.loads((second / "targets" / "catalog.json").read_bytes())["packages"]
        self.assertTrue(next(item for item in packages if item["rid"] == "win-x64")["revoked"])
        self.assertFalse(next(item for item in packages if item["rid"] == "linux-x64")["revoked"])
        self.assertEqual([], list((second / "packages").iterdir()))

    def test_publish_store_preserves_old_objects_and_rejects_stale_pointer(self):
        first = self.path / "first"
        self.build(self.source, first, "1.0.0")
        store = self.path / "private-store"
        pointer = repository_store.publish(first, store, self.bootstrap, None)
        self.assertEqual(pointer, repository_store.current(store))
        self.assertEqual(3, len(list((store / "packages").iterdir())))
        second = self.path / "second"
        self.build(first, second, None)
        with self.assertRaisesRegex(ValueError, "changed"):
            repository_store.publish(second, store, self.bootstrap, "0" * 64)
        with patch.object(publisher, "digest_file", side_effect=AssertionError("historical archive was rehashed")):
            updated = repository_store.publish(second, store, self.bootstrap, pointer)
        self.assertNotEqual(pointer, updated)
        self.assertEqual(updated, repository_store.current(store))
        self.assertEqual(3, len(list((store / "packages").iterdir())))
        self.assertTrue((store / "generations" / pointer / "metadata" / "timestamp.json").exists())

    def test_publish_store_recovers_from_interrupted_pointer_and_missing_archive(self):
        first = self.path / "first"
        self.build(self.source, first, "1.0.0")
        store = self.path / "private-store"
        pointer = repository_store.publish(first, store, self.bootstrap, None)
        second = self.path / "second"
        self.build(first, second, None)
        with patch.object(repository_store.os, "replace", side_effect=OSError("simulated failure")):
            with self.assertRaises(OSError):
                repository_store.publish(second, store, self.bootstrap, pointer)
        self.assertEqual(pointer, repository_store.current(store))
        updated = repository_store.publish(second, store, self.bootstrap, pointer)
        self.assertEqual(updated, repository_store.current(store))
        for rid in publisher.RIDS:
            (self.artifacts / f"agent-{rid}.zip").write_bytes(("new archive " + rid).encode())
        third = self.path / "third"
        self.build(second, third, "1.1.0")
        next((third / "packages").iterdir()).unlink()
        with self.assertRaisesRegex(ValueError, "unavailable"):
            repository_store.publish(third, store, self.bootstrap, updated)
        self.assertEqual(updated, repository_store.current(store))

    def test_publish_store_rejects_root_rollback_after_online_key_rotation(self):
        first = self.path / "first"
        self.build(self.source, first, "1.0.0")
        store = self.path / "private-store"
        pointer = repository_store.publish(first, store, self.bootstrap, None)
        legacy_second = self.path / "legacy-second"
        self.build(first, legacy_second, None)
        root = Metadata.from_bytes(self.root_bytes)
        root.signed.version = 2
        new_keys = dict(self.keys)
        for role in ("targets", "snapshot", "timestamp"):
            root.signed.revoke_key(self.keys[role].public_key.keyid, role)
            new_keys[role] = CryptoSigner.generate_ecdsa()
            root.signed.add_key(new_keys[role].public_key, role)
        root.sign(self.keys["root"])
        for name in ("root.json", "2.root.json"):
            (first / "metadata" / name).write_bytes(root.to_bytes())
        rotated = self.path / "rotated"
        self.build(first, rotated, None, new_keys)
        updated = repository_store.publish(rotated, store, self.bootstrap, pointer)
        legacy_third = self.path / "legacy-third"
        self.build(legacy_second, legacy_third, None)
        with self.assertRaisesRegex(ValueError, "rolled back"):
            repository_store.publish(legacy_third, store, self.bootstrap, updated)
        self.assertEqual(updated, repository_store.current(store))

    def test_publish_store_rejects_expired_signed_metadata(self):
        first = self.path / "first"
        self.build(self.source, first, "1.0.0")
        timestamp = Metadata.from_bytes((first / "metadata" / "timestamp.json").read_bytes())
        timestamp.signed.expires = datetime.now(timezone.utc) - timedelta(minutes=1)
        timestamp.sign(self.keys["timestamp"])
        (first / "metadata" / "timestamp.json").write_bytes(timestamp.to_bytes())
        store = self.path / "private-store"
        with self.assertRaisesRegex(ValueError, "expired"):
            repository_store.publish(first, store, self.bootstrap, None)
        self.assertIsNone(repository_store.current(store))

    def test_audit_and_repair_reject_wrong_or_unreferenced_bytes(self):
        first = self.path / "first"
        self.build(self.source, first, "1.0.0")
        store = self.path / "private-store"
        pointer = repository_store.publish(first, store, self.bootstrap, None)
        digest = json.loads((first / "targets" / "catalog.json").read_bytes())["packages"][0]["sha256"]
        correct = (first / "packages" / digest).read_bytes()
        damaged = store / "packages" / digest
        damaged.write_bytes(bytes([correct[0] ^ 1]) + correct[1:])
        self.assertEqual([digest], repository_store.audit_objects(store, self.bootstrap))
        wrong = self.path / "wrong-archive"
        wrong.write_bytes(b"x" * len(correct))
        with self.assertRaises(ValueError):
            repository_store.repair_object(store, self.bootstrap, digest, wrong)
        self.assertEqual([digest], repository_store.audit_objects(store, self.bootstrap))
        with self.assertRaises(ValueError):
            repository_store.repair_object(store, self.bootstrap, "0" * 64, first / "packages" / digest)
        with origin.open_readonly(damaged) as existing_reader:
            repository_store.repair_object(store, self.bootstrap, digest, first / "packages" / digest)
            self.assertNotEqual(correct, existing_reader.read())
        self.assertEqual([], repository_store.audit_objects(store, self.bootstrap))
        self.assertEqual(pointer, repository_store.current(store))
        self.assertEqual(correct, damaged.read_bytes())

    def test_repair_preserves_verified_staging_after_partial_replace_failure(self):
        first = self.path / "first"
        self.build(self.source, first, "1.0.0")
        store = self.path / "private-store"
        repository_store.publish(first, store, self.bootstrap, None)
        digest = json.loads((first / "targets" / "catalog.json").read_bytes())["packages"][0]["sha256"]
        original = first / "packages" / digest
        destination = store / "packages" / digest
        def partial_failure(source, target):
            target.unlink()
            raise OSError("simulated partial replacement")
        with patch.object(repository_store, "replace_published", side_effect=partial_failure):
            with self.assertRaisesRegex(OSError, "outcome is uncertain"):
                repository_store.repair_object(store, self.bootstrap, digest, original)
        self.assertEqual([digest], repository_store.audit_objects(store, self.bootstrap))
        retained = list((store / "packages").glob(".repair-*/" + digest))
        self.assertEqual(1, len(retained))
        self.assertEqual(original.read_bytes(), retained[0].read_bytes())
        repository_store.repair_object(store, self.bootstrap, digest, original)
        self.assertEqual([], repository_store.audit_objects(store, self.bootstrap))

    def test_concurrent_publishers_cannot_both_commit_same_parent(self):
        first = self.path / "first"
        self.build(self.source, first, "1.0.0")
        store = self.path / "private-store"
        parent = repository_store.publish(first, store, self.bootstrap, None)
        candidates = (self.path / "candidate-a", self.path / "candidate-b")
        self.build(first, candidates[0], None, revoke=["1.0.0@win-x64"])
        self.build(first, candidates[1], None, revoke=["1.0.0@win-x86"])
        def attempt(candidate):
            try:
                return repository_store.publish(candidate, store, self.bootstrap, parent)
            except ValueError:
                return None
        with ThreadPoolExecutor(max_workers=2) as executor:
            results = list(executor.map(attempt, candidates))
        self.assertEqual(1, sum(result is not None for result in results))
        self.assertIn(repository_store.current(store), results)

    def test_two_processes_race_one_parent_and_only_one_commits(self):
        first = self.path / "first"
        self.build(self.source, first, "1.0.0")
        store = self.path / "private-store"
        parent = repository_store.publish(first, store, self.bootstrap, None)
        candidates = (self.path / "candidate-a", self.path / "candidate-b")
        self.build(first, candidates[0], None, revoke=["1.0.0@win-x64"])
        self.build(first, candidates[1], None, revoke=["1.0.0@win-x86"])
        flags = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
        processes = [subprocess.Popen([sys.executable, str(Path(repository_store.__file__)), "--staging", str(candidate),
                         "--store", str(store), "--bootstrap", str(self.bootstrap), "--expected", parent],
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, creationflags=flags) for candidate in candidates]
        try:
            exits = [process.wait(timeout=30) for process in processes]
        finally:
            for process in processes:
                if process.poll() is None:
                    process.kill()
                    process.wait()
        self.assertEqual([0, 1], sorted(exits))
        self.assertNotEqual(parent, repository_store.current(store))

    def test_process_termination_before_pointer_keeps_previous_generation(self):
        first = self.path / "first"
        self.build(self.source, first, "1.0.0")
        store = self.path / "private-store"
        parent = repository_store.publish(first, store, self.bootstrap, None)
        second = self.path / "second"
        self.build(first, second, None)
        marker = self.path / "ready"
        script = """import os, sys, time
from pathlib import Path
import repository_store
def pause(source, destination):
    Path(sys.argv[5]).write_text('ready', encoding='ascii')
    time.sleep(60)
repository_store.os.replace = pause
repository_store.publish(Path(sys.argv[1]), Path(sys.argv[2]), Path(sys.argv[3]), sys.argv[4])
"""
        flags = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
        process = subprocess.Popen([sys.executable, "-c", script, str(second), str(store), str(self.bootstrap), parent, str(marker)],
                                   cwd=Path(repository_store.__file__).resolve().parent,
                                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, creationflags=flags)
        try:
            deadline = time.monotonic() + 30
            while not marker.exists() and process.poll() is None and time.monotonic() < deadline:
                time.sleep(0.05)
            self.assertTrue(marker.exists(), "The child did not reach the pointer boundary.")
        finally:
            if process.poll() is None:
                process.kill()
            process.wait(timeout=10)
        self.assertEqual(parent, repository_store.current(store))
        updated = repository_store.publish(second, store, self.bootstrap, parent)
        self.assertEqual(updated, repository_store.current(store))

    def test_origin_requires_auth_and_streams_only_published_paths(self):
        first = self.path / "first"
        self.build(self.source, first, "1.0.0")
        (first / "metadata" / "2.root.json").write_bytes(b"unpublished")
        store = self.path / "private-store"
        repository_store.publish(first, store, self.bootstrap, None)
        token = "test-origin-token-" + "a" * 32
        application = origin.Origin(store, token)
        entries = json.loads((first / "targets" / "catalog.json").read_bytes())["packages"]
        digest = entries[0]["sha256"]
        expected = (first / "packages" / digest).read_bytes()
        def request(path, auth="", method="GET", byte_range=None):
            result = {}
            def start(status, headers):
                result.update(status=status, headers=dict(headers))
            environ = {"REQUEST_METHOD": method, "PATH_INFO": path, "HTTP_AUTHORIZATION": auth}
            if byte_range is not None:
                environ["HTTP_RANGE"] = byte_range
            response = application(environ, start)
            try:
                result["body"] = b"".join(response)
            finally:
                if hasattr(response, "close"):
                    response.close()
            return result
        self.assertTrue(request("/targets/catalog.json")["status"].startswith("401"))
        auth = "Bearer " + token
        self.assertTrue(request("/targets/catalog.json", auth, "POST")["status"].startswith("405"))
        self.assertEqual((first / "targets" / "catalog.json").read_bytes(), request("/targets/catalog.json", auth)["body"])
        self.assertTrue(request("/metadata/../../current", auth)["status"].startswith("404"))
        part = request("/packages/" + digest, auth, byte_range="bytes=1-4")
        self.assertTrue(part["status"].startswith("206"))
        self.assertEqual(expected[1:5], part["body"])
        self.assertEqual(f"bytes 1-4/{len(expected)}", part["headers"]["Content-Range"])
        self.assertEqual(b"", request("/packages/" + digest, auth, method="HEAD")["body"])
        stream = application({"REQUEST_METHOD": "GET", "PATH_INFO": "/packages/" + digest,
                              "HTTP_AUTHORIZATION": auth}, lambda status, headers: None)
        self.assertEqual(expected, next(stream))
        stream.close()
        self.assertTrue(stream.file.closed)
        self.assertTrue(request("/packages/" + digest, auth, byte_range="bytes=999999-")["status"].startswith("416"))
        self.assertTrue(request("/packages/" + digest, auth, byte_range="bytes=" + "9" * 5000 + "-")["status"].startswith("416"))
        revision = repository_store.current(store)
        self.assertFalse((store / "generations" / revision / "metadata" / "2.root.json").exists())
        (store / "generations" / revision / "metadata" / "2.root.json").write_bytes(b"unpublished")
        self.assertTrue(request("/metadata/2.root.json", auth)["status"].startswith("404"))
        (store / "generations" / revision / "metadata" / "1.root.json").unlink()
        self.assertTrue(request("/metadata/1.root.json", auth)["status"].startswith("503"))
        self.assertTrue(request("/metadata/2.root.json", auth)["status"].startswith("404"))

    def test_origin_serves_through_a_real_local_http_process(self):
        first = self.path / "first"
        self.build(self.source, first, "1.0.0")
        store = self.path / "private-store"
        repository_store.publish(first, store, self.bootstrap, None)
        with socket.socket() as listener:
            listener.bind(("127.0.0.1", 0))
            port = listener.getsockname()[1]
        token = "test-origin-token-" + "a" * 32
        environment = dict(os.environ, VIGILOOM_ORIGIN_READ_TOKEN=token)
        flags = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
        process = subprocess.Popen([sys.executable, str(Path(origin.__file__)), "--store", str(store), "--port", str(port)],
                                   env=environment, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, creationflags=flags)
        try:
            deadline = time.monotonic() + 15
            while True:
                try:
                    connection = http.client.HTTPConnection("127.0.0.1", port, timeout=2)
                    connection.request("GET", "/targets/catalog.json", headers={"Authorization": "Bearer " + token, "Connection": "close"})
                    response = connection.getresponse()
                    body = response.read()
                    connection.close()
                    break
                except OSError:
                    if process.poll() is not None or time.monotonic() >= deadline:
                        self.fail("The isolated origin did not start.")
                    time.sleep(0.05)
            self.assertEqual(200, response.status)
            self.assertEqual((first / "targets" / "catalog.json").read_bytes(), body)
        finally:
            if process.poll() is None:
                process.terminate()
            process.wait(timeout=10)

    def test_online_keys_can_rotate_before_the_next_metadata_refresh(self):
        first = self.path / "first"
        self.build(self.source, first, "1.0.0")
        root = Metadata.from_bytes(self.root_bytes)
        new_keys = dict(self.keys)
        root.signed.version = 2
        for role in ("targets", "snapshot", "timestamp"):
            root.signed.revoke_key(self.keys[role].public_key.keyid, role)
            new_keys[role] = CryptoSigner.generate_ecdsa()
            root.signed.add_key(new_keys[role].public_key, role)
        root.sign(self.keys["root"])
        (first / "metadata" / "2.root.json").write_bytes(root.to_bytes())
        (first / "metadata" / "root.json").write_bytes(root.to_bytes())
        second = self.path / "second"
        self.build(first, second, None, new_keys)
        roots = publisher.verify_roots(second, self.bootstrap)
        version, packages, _ = publisher.verify_previous(second, roots)
        self.assertEqual(2, version)
        self.assertEqual(3, len(packages))

    def test_expired_metadata_can_be_refreshed(self):
        first = self.path / "first"
        self.build(self.source, first, "1.0.0")
        expiry = datetime.now(timezone.utc) - timedelta(days=1)
        metadata = first / "metadata"
        targets = Metadata.from_bytes((metadata / "targets.json").read_bytes())
        targets.signed.expires = expiry
        targets.sign(self.keys["targets"])
        target_bytes = targets.to_bytes()
        snapshot = Metadata.from_bytes((metadata / "snapshot.json").read_bytes())
        snapshot.signed.expires = expiry
        snapshot.signed.meta["targets.json"] = MetaFile.from_data(1, target_bytes, ["sha256"])
        snapshot.sign(self.keys["snapshot"])
        snapshot_bytes = snapshot.to_bytes()
        timestamp = Metadata.from_bytes((metadata / "timestamp.json").read_bytes())
        timestamp.signed.expires = expiry
        timestamp.signed.snapshot_meta = MetaFile.from_data(1, snapshot_bytes, ["sha256"])
        timestamp.sign(self.keys["timestamp"])
        for name, data in (("targets.json", target_bytes), ("snapshot.json", snapshot_bytes), ("timestamp.json", timestamp.to_bytes())):
            (metadata / name).write_bytes(data)
        self.build(first, self.path / "refreshed", None)

    def test_bootstrap_encrypts_keys_and_works_with_publisher(self):
        destination = self.path / "bootstrap-output"
        bootstrap.create(destination, "offline-password-for-test", "online-password-for-test")
        for role in ("root", "targets", "snapshot", "timestamp"):
            self.assertIn(b"ENCRYPTED PRIVATE KEY", (destination / "PRIVATE-KEYS" / f"{role}.pem").read_bytes())
        args = argparse.Namespace(previous=destination / "repository", bootstrap=destination / "bootstrap.json",
                                  output=self.path / "from-bootstrap", artifacts=self.artifacts,
                                  version="1.0.0", minimum_updater="1.0.0", minimum_agent=None, maximum_agent=None)
        environment = {f"VIGILOOM_{role.upper()}_KEY_PEM": (destination / "PRIVATE-KEYS" / f"{role}.pem").read_text()
                       for role in ("targets", "snapshot", "timestamp")}
        environment["VIGILOOM_RELEASE_KEY_PASSWORD"] = "online-password-for-test"
        with patch.dict(os.environ, environment, clear=False):
            publisher.build(args)
        self.assertFalse((args.output / "PRIVATE-KEYS").exists())
        with self.assertRaises(ValueError):
            rotate_root.rotate(args.output, args.bootstrap, destination / "PRIVATE-KEYS" / "root.pem",
                               "offline-password-for-test", args.output / "private-output", "new-offline-password", "new-online-password")
        with self.assertRaises(ValueError):
            rotate_root.rotate(args.output, args.bootstrap, destination / "PRIVATE-KEYS" / "root.pem",
                               "offline-password-for-test", self.path / "bad-passwords", "new-offline-password", "offline-password-for-test")
        rotated = self.path / "rotated-keys"
        rotate_root.rotate(args.output, args.bootstrap, destination / "PRIVATE-KEYS" / "root.pem",
                           "offline-password-for-test", rotated, "new-offline-password", "new-online-password")
        for name in ("root.json", "2.root.json"):
            (args.output / "metadata" / name).write_bytes((rotated / "PUBLIC-ROOT" / name).read_bytes())
        environment = {f"VIGILOOM_{role.upper()}_KEY_PEM": (rotated / "PRIVATE-KEYS" / f"{role}.pem").read_text()
                       for role in ("targets", "snapshot", "timestamp")}
        environment["VIGILOOM_RELEASE_KEY_PASSWORD"] = "new-online-password"
        refresh = argparse.Namespace(previous=args.output, bootstrap=args.bootstrap, output=self.path / "rotated-repository",
                                     artifacts=None, version=None, minimum_updater=None, minimum_agent=None, maximum_agent=None)
        with patch.dict(os.environ, environment, clear=False):
            publisher.build(refresh)
        roots = publisher.verify_roots(refresh.output, args.bootstrap)
        self.assertEqual([1, 2], sorted(roots))
        self.assertEqual(2, publisher.verify_previous(refresh.output, roots)[0])

    def test_expired_root_can_rotate_offline_without_weakening_publication(self):
        destination = self.path / "expired-bootstrap"
        bootstrap.create(destination, "offline-password-for-test", "online-password-for-test")
        from cryptography.hazmat.primitives.serialization import load_pem_private_key
        old_key = CryptoSigner(load_pem_private_key((destination / "PRIVATE-KEYS" / "root.pem").read_bytes(), b"offline-password-for-test"))
        root = Metadata.from_bytes((destination / "bootstrap.json").read_bytes())
        root.signed.expires = datetime.now(timezone.utc) - timedelta(days=1)
        root.sign(old_key)
        expired = root.to_bytes()
        (destination / "bootstrap.json").write_bytes(expired)
        metadata = destination / "repository" / "metadata"
        (metadata / "root.json").write_bytes(expired)
        (metadata / "1.root.json").write_bytes(expired)
        with self.assertRaises(ValueError):
            publisher.verify_roots(destination / "repository", destination / "bootstrap.json")
        rotated = self.path / "recovery"
        rotate_root.rotate(destination / "repository", destination / "bootstrap.json", destination / "PRIVATE-KEYS" / "root.pem",
                           "offline-password-for-test", rotated, "new-offline-password", "new-online-password")
        (metadata / "root.json").write_bytes((rotated / "PUBLIC-ROOT" / "root.json").read_bytes())
        (metadata / "2.root.json").write_bytes((rotated / "PUBLIC-ROOT" / "2.root.json").read_bytes())
        self.assertEqual([1, 2], sorted(publisher.verify_roots(destination / "repository", destination / "bootstrap.json")))


if __name__ == "__main__":
    unittest.main()
