"""Local-only supplier simulator. Do not expose its control endpoints publicly."""
import json
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs

lock = threading.Lock()
seen = {}
attempts = {}
events = []


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        with lock:
            body = {"processed": len(seen), "attempts": dict(attempts), "events": list(events)}
        self.reply(200, body)

    def do_POST(self):
        raw = self.rfile.read(int(self.headers.get("Content-Length", "0")))
        key = self.headers.get("Idempotency-Key", "")
        if not key:
            self.reply(400, {"error": "missing_idempotency_key"})
            return
        with lock:
            attempts[key] = attempts.get(key, 0) + 1
            attempt = attempts[key]
            previous = seen.get(key)
        if previous is not None:
            self.reply(200, previous)
            return
        if self.path == "/always-fail":
            self.reply(503, {"error": "unavailable"})
            return
        if self.path == "/flaky" and attempt <= 2:
            self.reply(503, {"error": "temporary_failure"})
            return
        payload = parse_qs(raw.decode()) if self.path == "/form" else json.loads(raw or b"{}")
        response = {"code": 0, "accepted": True}
        with lock:
            if key not in seen:
                seen[key] = response
                events.append({"id": key, "path": self.path, "data": payload})
        if self.path == "/timeout-after-commit" and attempt == 1:
            time.sleep(20)
        self.reply(200, response)

    def reply(self, status, body):
        encoded = json.dumps(body).encode()
        try:
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(encoded)))
            self.end_headers()
            self.wfile.write(encoded)
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
            pass


if __name__ == "__main__":
    ThreadingHTTPServer(("0.0.0.0", 8081), Handler).serve_forever()
