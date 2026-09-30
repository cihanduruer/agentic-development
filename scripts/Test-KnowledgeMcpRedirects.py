#!/usr/bin/env python3
import importlib.util
import ssl
import subprocess
import tempfile
import threading
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path


SCRIPT = Path(__file__).with_name("Test-KnowledgeMcpEndpoint.py")
SPEC = importlib.util.spec_from_file_location("knowledge_mcp_smoke", SCRIPT)
SMOKE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(SMOKE)


class RedirectHandler(BaseHTTPRequestHandler):
    def do_GET(self):
        self.respond()

    def do_POST(self):
        self.respond()

    def respond(self):
        body = self.rfile.read(int(self.headers.get("Content-Length", "0")))
        self.server.received.append(
            (self.path, self.headers.get("Authorization"), self.command, body))
        if self.path == "/redirect":
            self.send_response(302)
            self.send_header("Location", self.server.redirect_location)
            self.end_headers()
            return

        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        self.wfile.write(b"{}")

    def log_message(self, format, *args):
        pass


def start_server(redirect_location, tls_context=None):
    server = ThreadingHTTPServer(("127.0.0.1", 0), RedirectHandler)
    server.daemon_threads = True
    server.received = []
    server.redirect_location = redirect_location
    if tls_context is not None:
        server.socket = tls_context.wrap_socket(server.socket, server_side=True)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    return server, thread


def stop_server(server, thread):
    server.shutdown()
    server.server_close()
    thread.join()


def assert_redirect_blocked(url, payload=None, opener=None):
    try:
        SMOKE.request(
            url,
            payload=payload,
            token="dummy-authorization-token",
            opener=opener,
        )
    except SMOKE.SmokeFailure as error:
        if "redirect" not in str(error).lower():
            raise AssertionError("Redirect rejection did not report a clear smoke failure.") from error
    else:
        raise AssertionError("The smoke client accepted an HTTP redirect.")


def main():
    with tempfile.TemporaryDirectory() as temporary_directory:
        certificate = Path(temporary_directory) / "certificate.pem"
        private_key = Path(temporary_directory) / "private-key.pem"
        subprocess.run(
            [
                "openssl", "req", "-x509", "-newkey", "rsa:2048",
                "-keyout", str(private_key), "-out", str(certificate),
                "-days", "1", "-nodes", "-subj", "/CN=localhost",
            ],
            check=True,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )

        target, target_thread = start_server("")
        try:
            target_url = f"http://127.0.0.1:{target.server_port}/target"

            same_origin, same_thread = start_server("/target")
            try:
                assert_redirect_blocked(
                    f"http://127.0.0.1:{same_origin.server_port}/redirect")
                if any(path == "/target" for path, _, _, _ in same_origin.received):
                    raise AssertionError("The same-origin redirect target received a request.")
            finally:
                stop_server(same_origin, same_thread)

            cross_host, cross_thread = start_server(target_url)
            try:
                assert_redirect_blocked(
                    f"http://localhost:{cross_host.server_port}/redirect",
                    payload={"probe": "redirect-test"})
                if target.received:
                    raise AssertionError("The cross-host redirect target received a request.")
            finally:
                stop_server(cross_host, cross_thread)

            tls_context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
            tls_context.load_cert_chain(certificate, private_key)
            downgrade, downgrade_thread = start_server(target_url, tls_context)
            try:
                test_opener = urllib.request.build_opener(
                    SMOKE.NoRedirectHandler(),
                    urllib.request.HTTPSHandler(context=ssl._create_unverified_context()),
                )
                assert_redirect_blocked(
                    f"https://localhost:{downgrade.server_port}/redirect",
                    payload={"probe": "redirect-test"},
                    opener=test_opener)
                if target.received:
                    raise AssertionError("The HTTPS-to-HTTP redirect target received a request.")
            finally:
                stop_server(downgrade, downgrade_thread)
        finally:
            stop_server(target, target_thread)

    print("Knowledge MCP redirect regressions passed.")


if __name__ == "__main__":
    main()
