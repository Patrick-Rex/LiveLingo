"""Owned no-model HTTP child used only to verify a shared startup deadline."""

import http.server
import json
import sys
import time


class Handler(http.server.BaseHTTPRequestHandler):
    """Return the known fixture digest and a deliberately delayed warm-up."""

    def do_GET(self):
        """Report already-present weights without loading any real model."""
        self.reply({"models": [{"name": "translategemma:12b", "digest":
                    "c2f9a9ca1ec7149f2422581c89937ab0363d9399d9355514d753ee012e99c252"}]})

    def do_POST(self):
        """Hold one request long enough to exceed the combined startup budget."""
        length = int(self.headers.get("Content-Length", "0"))
        if length > 65536:
            self.send_error(413)
            return
        self.rfile.read(length)
        time.sleep(1.6)
        self.reply({"model": "translategemma:12b", "done": True, "done_reason": "stop",
                    "message": {"content": "准备好了。"}})

    def reply(self, value):
        """Write one bounded JSON response."""
        data = json.dumps(value).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def log_message(self, *_args):
        """Keep the no-model fixture's diagnostics empty."""


if __name__ == "__main__":
    http.server.HTTPServer(("127.0.0.1", int(sys.argv[1])), Handler).serve_forever()
