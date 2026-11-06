"""Loopback-only development console. Never expose this identity-bearing gateway publicly."""
import json
import mimetypes
import os
import re
import urllib.error
import urllib.parse
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1] / 'src' / 'NotificationHub.Api' / 'wwwroot'
PORT = int(os.environ.get('CONSOLE_PORT', '8090'))
BASE = os.environ.get('HUB_URL', 'http://127.0.0.1:5055').rstrip('/')
SUPPLIER = os.environ.get('SUPPLIER_URL', 'http://127.0.0.1:8081/json')
ALLOWED_PATH = re.compile(r'/v1/(?:providers|connections|notifications|deliveries|tickets|metrics|outbox|subscriptions)(?:/[A-Za-z0-9._-]+)*')


def validate_request(value):
    if not isinstance(value, dict):
        raise ValueError('Invalid request.')
    path = value.get('path', '')
    parsed = urllib.parse.urlsplit(path)
    if parsed.scheme or parsed.netloc or parsed.fragment or not ALLOWED_PATH.fullmatch(parsed.path):
        raise ValueError('Only notification API paths are allowed.')
    if value.get('method', 'GET') not in ('GET', 'POST', 'PUT', 'PATCH', 'DELETE'):
        raise ValueError('Unsupported method.')
    if value.get('role', 'business') not in ('business', 'provider'):
        raise ValueError('Unsupported role.')
    return value


class Handler(BaseHTTPRequestHandler):
    def log_message(self, format_string, *args):
        # Keep the local helper independent of transient terminal streams.
        pass

    def valid_host(self):
        return self.headers.get('Host') in (f'127.0.0.1:{PORT}', f'localhost:{PORT}')

    def do_GET(self):
        if not self.valid_host():
            self.reply(403, {'error': 'Invalid host.'})
            return
        if self.path == '/local-demo-config':
            self.reply(200, {'demo': True, 'supplierUrl': SUPPLIER})
            return
        path = urllib.parse.urlsplit(self.path).path
        asset = (ROOT / ('index.html' if path == '/' else path.lstrip('/'))).resolve()
        if not asset.is_relative_to(ROOT.resolve()) or not asset.is_file():
            self.reply(404, {'error': 'Not found.'})
            return
        content_type = 'text/javascript' if asset.suffix in ('.js', '.mjs') else mimetypes.guess_type(str(asset))[0] or 'application/octet-stream'
        self.reply(200, asset.read_bytes(), content_type + '; charset=utf-8')

    def do_POST(self):
        if not self.valid_host() or self.headers.get('Origin') not in (f'http://127.0.0.1:{PORT}', f'http://localhost:{PORT}') or self.headers.get('X-Console-Request') != '1':
            self.reply(403, {'error': 'Invalid console origin.'})
            return
        if self.path != '/local-api':
            self.reply(404, {'error': 'Not found.'})
            return
        try:
            size = int(self.headers.get('Content-Length', '0'))
            if not 0 < size <= 262144:
                self.reply(413, {'error': 'Invalid request size.'})
                return
            value = validate_request(json.loads(self.rfile.read(size)))
            identity = os.environ['PROVIDER_API_KEY' if value.get('role') == 'provider' else 'BUSINESS_API_KEY']
            headers = {'X-Api-Key': identity, 'Accept-Language': value.get('language', 'en')}
            body = None
            if 'body' in value:
                body = json.dumps(value['body']).encode()
                headers['Content-Type'] = 'application/json'
            if value.get('key'):
                headers['Idempotency-Key'] = value['key']
            request = urllib.request.Request(BASE + value['path'], data=body, headers=headers, method=value.get('method', 'GET'))
            with urllib.request.urlopen(request, timeout=15) as response:
                self.reply(response.status, response.read(), response.headers.get('Content-Type', 'application/json'))
        except urllib.error.HTTPError as error:
            self.reply(error.code, error.read(), 'application/json')
        except (ValueError, TypeError, KeyError) as error:
            self.reply(400, {'error': str(error)})
        except (urllib.error.URLError, TimeoutError):
            self.reply(502, {'error': 'The notification API is not reachable.'})

    def reply(self, status, value, content_type='application/json'):
        body = value if isinstance(value, bytes) else json.dumps(value).encode()
        self.send_response(status)
        self.send_header('Content-Type', content_type)
        self.send_header('Content-Length', str(len(body)))
        self.send_header('Cache-Control', 'no-store')
        self.send_header('X-Content-Type-Options', 'nosniff')
        self.send_header('Content-Security-Policy', "default-src 'self'; style-src 'self' 'unsafe-inline'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'")
        self.end_headers()
        try:
            self.wfile.write(body)
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
            pass


class ConsoleServer(ThreadingHTTPServer):
    request_queue_size = 64


if __name__ == '__main__':
    if not os.environ.get('BUSINESS_API_KEY') or not os.environ.get('PROVIDER_API_KEY'):
        raise SystemExit('BUSINESS_API_KEY and PROVIDER_API_KEY are required.')
    ConsoleServer(('127.0.0.1', PORT), Handler).serve_forever()
