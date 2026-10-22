"""Local read-only dashboard + JSON API (stdlib http.server, no dependencies)."""
from __future__ import annotations

import json
import mimetypes
import os
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

from .service import SignalService

WEB_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "web")


class Handler(BaseHTTPRequestHandler):
    service: SignalService = None
    server_version = "QuantLOLSignals/1.0"
    protocol_version = "HTTP/1.1"

    # ------------------------------------------------------------------ helpers
    def _send(self, code, body, ctype="application/json; charset=utf-8"):
        if isinstance(body, str):
            body = body.encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.end_headers()
        if self.command != "HEAD":
            self.wfile.write(body)

    def _json(self, obj, code=200):
        self._send(code, json.dumps(obj, default=str), "application/json; charset=utf-8")

    def log_message(self, fmt, *args):        # keep the console readable
        pass

    # --------------------------------------------------------------------- GET
    def do_GET(self):
        u = urlparse(self.path)
        path = u.path
        if path in ("/", "/index.html"):
            return self._static("index.html")
        if path == "/api/state":
            return self._json(self.service.state())
        if path == "/api/health":
            st = self.service.state()
            return self._json({"ok": st["health"]["ok"], "issues": st["health"]["issues"],
                               "generated_utc": st["generated_utc"],
                               "data": st["data"], "stats": st["stats"]})
        if path == "/api/signals":
            q = parse_qs(u.query)
            try:
                limit = max(1, min(500, int(q.get("limit", ["50"])[0])))
            except ValueError:
                limit = 50
            return self._json({"signals": self.service.store.tail_signals(limit)})
        if path == "/api/config":
            return self._json(self.service.cfg.summary())
        if path.startswith("/static/"):
            return self._static(os.path.basename(path))
        return self._json({"error": "not found", "path": path}, 404)

    def do_HEAD(self):
        return self.do_GET()

    # -------------------------------------------------------------------- POST
    def do_POST(self):
        u = urlparse(self.path)
        if u.path == "/api/evaluate":
            try:
                st = self.service.poll_once()
                return self._json({"ok": True, "state": st})
            except Exception as e:
                return self._json({"ok": False, "error": "%s: %s" % (type(e).__name__, e)}, 500)
        return self._json({"error": "not found"}, 404)

    # ------------------------------------------------------------------ static
    def _static(self, name):
        path = os.path.join(WEB_DIR, name)
        if not os.path.isfile(path):
            return self._json({"error": "missing asset", "name": name}, 404)
        ctype = mimetypes.guess_type(path)[0] or "application/octet-stream"
        with open(path, "rb") as f:
            self._send(200, f.read(), ctype + ("; charset=utf-8" if ctype.startswith("text") or ctype.endswith("html") else ""))


def make_server(service: SignalService, host=None, port=None):
    handler = type("BoundHandler", (Handler,), {"service": service})
    bind_host = service.cfg.host if host is None else host
    bind_port = service.cfg.port if port is None else port      # port 0 means "pick a free port"
    httpd = ThreadingHTTPServer((bind_host, bind_port), handler)
    httpd.daemon_threads = True
    return httpd


def serve(service: SignalService, host=None, port=None, background_loop=True):
    if background_loop:
        service.start_background()
    httpd = make_server(service, host, port)
    host, port = httpd.server_address[0], httpd.server_address[1]
    print("QuantLOL Signals dashboard: http://%s:%d  (advisory only, no orders)" % (host, port))
    try:
        httpd.serve_forever(poll_interval=0.5)
    except KeyboardInterrupt:
        print("\nstopping")
    finally:
        service.stop()
        httpd.server_close()
    return httpd


def serve_in_thread(service: SignalService, host="127.0.0.1", port=0):
    """Used by the tests: returns (httpd, thread, port)."""
    httpd = make_server(service, host, port)
    t = threading.Thread(target=httpd.serve_forever, kwargs={"poll_interval": 0.2}, daemon=True)
    t.start()
    return httpd, t, httpd.server_address[1]
