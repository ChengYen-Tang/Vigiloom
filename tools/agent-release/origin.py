"""Serve the published store privately behind a TLS-terminating reverse proxy.

The process only needs read access to the store and one read credential. It never
loads publisher signing keys and cannot change the current generation.
"""
from __future__ import annotations

import argparse
import hmac
import json
import os
import re
from pathlib import Path
from typing import BinaryIO

import repository_store
import publisher
from shared_read import open_readonly
from waitress import serve

TOKEN = re.compile(r"^[A-Za-z0-9._~+/-]{32,4096}={0,2}$")
ROOT = re.compile(r"^[1-9][0-9]{0,9}\.root\.json$")
MANIFEST = re.compile(r"^[0-9a-f]{64}\.json$")
PACKAGE = re.compile(r"^[0-9a-f]{64}$")


class FileSlice:
    def __init__(self, file: BinaryIO, remaining: int):
        self.file, self.remaining = file, remaining

    def __iter__(self):
        return self

    def __next__(self):
        if self.remaining <= 0:
            self.close()
            raise StopIteration
        try:
            data = self.file.read(min(1024 * 1024, self.remaining))
        except OSError:
            self.close()
            raise
        if not data:
            self.close()
            raise StopIteration
        self.remaining -= len(data)
        return data

    def close(self):
        self.file.close()


def requested_range(value: str, size: int) -> tuple[int, int] | None:
    if len(value) > 128:
        return None
    match = re.fullmatch(r"bytes=([0-9]*)-([0-9]*)", value)
    if not match or not any(match.groups()) or size <= 0:
        return None
    first, last = match.groups()
    if not first:
        suffix = int(last)
        return (max(0, size - suffix), size - 1) if suffix > 0 else None
    start = int(first)
    end = min(int(last), size - 1) if last else size - 1
    return (start, end) if start < size and start <= end else None


class Origin:
    def __init__(self, store: Path, token: str):
        if len(token) > 4096 or not TOKEN.fullmatch(token):
            raise ValueError("The origin read token must contain 32–4096 valid bearer characters.")
        self.store = store.resolve(strict=True)
        self.expected = ("Bearer " + token).encode("ascii")

    def __call__(self, environ, start_response):
        auth = environ.get("HTTP_AUTHORIZATION", "")
        if len(auth) > 4105 or not hmac.compare_digest(auth.encode("utf-8"), self.expected):
            start_response("401 Unauthorized", [("WWW-Authenticate", "Bearer"), ("Cache-Control", "no-store"), ("Content-Length", "0")])
            return []
        method = environ.get("REQUEST_METHOD", "")
        if method not in ("GET", "HEAD"):
            start_response("405 Method Not Allowed", [("Allow", "GET, HEAD"), ("Content-Length", "0")])
            return []
        path = environ.get("PATH_INFO", "").split("/")
        if len(path) != 3 or path[0] or path[1] not in ("metadata", "targets", "packages"):
            return self.not_found(start_response)
        category, name = path[1:]
        allowed = (category == "metadata" and (name in ("root.json", "timestamp.json", "snapshot.json", "targets.json") or ROOT.fullmatch(name))) or \
                  (category == "targets" and (name == "catalog.json" or MANIFEST.fullmatch(name))) or \
                  (category == "packages" and PACKAGE.fullmatch(name))
        if not allowed:
            return self.not_found(start_response)
        try:
            revision = repository_store.current(self.store)
            if revision is None:
                start_response("503 Service Unavailable", [("Cache-Control", "no-store"), ("Content-Length", "0")])
                return []
            path = self.store / "packages" / name if category == "packages" else self.store / "generations" / revision / category / name
            if category == "metadata" and ROOT.fullmatch(name):
                current_root = self.store / "generations" / revision / "metadata" / "root.json"
                root_version = json.loads(publisher.safe_file(current_root))["signed"]["version"]
                if type(root_version) is not int or root_version <= 0:
                    raise ValueError("Invalid current root version.")
                if int(name.split('.', 1)[0]) > root_version:
                    return self.not_found(start_response)
            if path.is_symlink() or not path.is_file():
                if category == "metadata":
                    if name in ("root.json", "timestamp.json", "snapshot.json", "targets.json"):
                        return self.unavailable(start_response)
                    return self.unavailable(start_response)
                return self.not_found(start_response)
            stream = open_readonly(path)
            size = os.fstat(stream.fileno()).st_size
        except (OSError, ValueError, UnicodeError, KeyError, TypeError):
            return self.unavailable(start_response)
        offset, end, status = 0, size - 1, "200 OK"
        headers = [("Cache-Control", "no-store"), ("X-Content-Type-Options", "nosniff"), ("Accept-Ranges", "bytes"),
                   ("Content-Type", "application/octet-stream" if category == "packages" else "application/json")]
        if value := environ.get("HTTP_RANGE"):
            selected = requested_range(value, size)
            if selected is None:
                stream.close()
                start_response("416 Range Not Satisfiable", headers + [("Content-Range", f"bytes */{size}"), ("Content-Length", "0")])
                return []
            offset, end = selected
            status = "206 Partial Content"
            headers.append(("Content-Range", f"bytes {offset}-{end}/{size}"))
        length = max(0, end - offset + 1)
        headers.append(("Content-Length", str(length)))
        if method == "HEAD":
            stream.close()
            start_response(status, headers)
            return []
        try:
            stream.seek(offset)
            start_response(status, headers)
        except Exception:
            stream.close()
            raise
        return FileSlice(stream, length)

    @staticmethod
    def not_found(start_response):
        start_response("404 Not Found", [("Cache-Control", "no-store"), ("Content-Length", "0")])
        return []

    @staticmethod
    def unavailable(start_response):
        start_response("503 Service Unavailable", [("Cache-Control", "no-store"), ("Content-Length", "0")])
        return []


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--store", required=True, type=Path)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8089)
    args = parser.parse_args()
    token = os.environ.get("VIGILOOM_ORIGIN_READ_TOKEN", "")
    serve(Origin(args.store, token), host=args.host, port=args.port, threads=4, channel_timeout=30,
          max_request_body_size=1024, max_request_header_size=16384)


if __name__ == "__main__":
    main()
