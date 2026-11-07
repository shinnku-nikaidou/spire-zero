#!/usr/bin/env python3
"""dist.py STORE PORT CERT KEY KEY-HASH: the hub's private service for the user's own machines (pool/README.md).
HTTPS, a pinned self-signed certificate; every request needs the header x-api-key whose SHA-256 is in the file KEY-HASH.
The hub holds only that hash.

  GET /NAME               the file STORE/dist/NAME: a sim bundle, inputs, pool.json, worker.py (dist-admin.py, box.py)
  PUT /chunk/JOB/SHARD/INDEX   a tar of one chunk of a pool job (worker.py), its done- maybe empty (no run finished:
                          cut at the deadline, stopped, failed). Its files go into STORE/JOB, done-SHARD-INDEX.txt last,
                          so a chunk is there whole or not at all. Sending it again answers 200 and changes nothing: a
                          client that lost the answer just sends it again. 409: the dealer took the chunk back from SHARD
                          (deal.py: JOB/deal/back/SHARD-INDEX), so it is played elsewhere and this copy is not wanted.

The hub has other traffic, and that comes first: each large download is limited to RATE, and at most SLOTS
run at once.
"""

import fcntl
import hashlib
import hmac
import io
import os
import re
import shutil
import ssl
import sys
import tarfile
import tempfile
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

RATE = 3_000_000  # bytes a second, per large download
SLOTS = 2
LARGE = 1_000_000
BLOCK = 64 * 1024
MAX_CHUNK = 512 * 1024 * 1024
NAME = re.compile(r"/([A-Za-z0-9][A-Za-z0-9._-]*)")
CHUNK = re.compile(r"/chunk/([A-Za-z0-9._-]+(?:/[A-Za-z0-9._-]+)*)/([A-Za-z0-9._-]+)/([0-9]+)")


class Handler(BaseHTTPRequestHandler):
    timeout = 60
    store = Path(".")
    key_hash = ""
    slots = threading.BoundedSemaphore(SLOTS)

    def answer(self, code):
        self.send_response(code)
        self.send_header("Content-Length", "0")
        self.end_headers()

    def allowed(self):
        key = self.headers.get("x-api-key", "")
        return hmac.compare_digest(hashlib.sha256(key.encode()).hexdigest(), self.key_hash)

    def do_GET(self):
        if not self.allowed():
            return self.answer(401)
        match = NAME.fullmatch(self.path)
        path = self.store / "dist" / match[1] if match else None
        if path is None or not path.is_file():
            return self.answer(404)
        size = path.stat().st_size
        large = size > LARGE
        if large and not self.slots.acquire(blocking=False):
            return self.answer(503)
        try:
            self.send_response(200)
            self.send_header("Content-Length", str(size))
            self.end_headers()
            with path.open("rb") as f:
                while block := f.read(BLOCK):
                    self.wfile.write(block)
                    if large:
                        time.sleep(len(block) / RATE)
        finally:
            if large:
                self.slots.release()

    def do_PUT(self):
        if not self.allowed():
            return self.answer(401)
        match = CHUNK.fullmatch(self.path)
        if not match or ".." in match[1] or ".." in match[2]:
            return self.answer(404)
        job, shard, index = self.store / match[1], match[2], match[3]
        if not (job / "deal" / "job.json").is_file():
            return self.answer(404)
        length = int(self.headers.get("Content-Length", -1))
        if not 0 < length <= MAX_CHUNK:
            return self.answer(413)
        body = self.rfile.read(length)
        done = f"done-{shard}-{index}.txt"
        if (job / done).exists():
            return self.answer(200)
        # every member is a plain file named for this chunk, in the job's own folder
        allowed = re.compile(rf"(rec|log|out)-{re.escape(shard)}-{index}\.[a-z.]+|(counts|tally)-{re.escape(shard)}-{index}\.txt|{re.escape(done)}")
        try:
            with tarfile.open(fileobj=io.BytesIO(body)) as tar:
                members = tar.getmembers()
                if done not in [m.name for m in members]:
                    return self.answer(422)
                if any(not m.isreg() or not allowed.fullmatch(m.name) for m in members):
                    return self.answer(422)
                with tempfile.TemporaryDirectory(dir=job, prefix=".chunk-") as stage:
                    for member in members:
                        with tar.extractfile(member) as source, open(os.path.join(stage, member.name), "wb") as target:
                            shutil.copyfileobj(source, target)
                    # the dealer's lock: a chunk is taken back or taken in, never both
                    with open(job / "deal" / "lock", "a") as lock:
                        fcntl.flock(lock, fcntl.LOCK_EX)
                        if (job / "deal" / "back" / f"{shard}-{index}").exists():
                            return self.answer(409)
                        if not (job / done).exists():
                            for member in sorted(members, key=lambda m: m.name == done):
                                os.replace(os.path.join(stage, member.name), job / member.name)
        except tarfile.TarError:
            return self.answer(422)
        self.answer(200)

    def log_message(self, format, *args):
        print(f"{self.client_address[0]} {format % args}", flush=True)


class Server(ThreadingHTTPServer):
    daemon_threads = True
    request_queue_size = 256  # every worker may send at once

    def handle_error(self, request, client_address):
        print(f"{client_address[0]} {sys.exc_info()[1]!r}", flush=True)


if __name__ == "__main__":
    store, port, cert, key, key_hash = sys.argv[1:]
    Handler.store, Handler.key_hash = Path(store), Path(key_hash).read_text().strip()
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.load_cert_chain(cert, key)
    server = Server(("", int(port)), Handler)
    # the handshake runs in the request's thread, so a client that stalls in it holds up nobody
    server.socket = context.wrap_socket(server.socket, server_side=True, do_handshake_on_connect=False)
    server.serve_forever()
