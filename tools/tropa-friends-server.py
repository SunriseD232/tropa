#!/usr/bin/env python3
"""Сервер публикации «Для друзей» (ADR-048) — один файл, только стандартная библиотека Python 3.8+.

Тропа администратора отправляет документ PUT-запросом с ключом (Authorization: Bearer <ключ>),
Тропа друзей скачивает его GET-запросом по тому же адресу, как обычную подписку.

Адрес секретный: в нём случайная часть пути. Ключ нужен только для записи.
Сервер слушает 127.0.0.1 — HTTPS снаружи даёт nginx (или Caddy) перед ним.

Запуск:
    TROPA_FRIENDS_KEY=<ключ из Тропы> python3 tropa-friends-server.py \
        --path /tropa/<случайная-строка> --file /var/lib/tropa-friends/doc.json --port 8790

nginx (внутри server { listen 443 ssl; ... }):
    location /tropa/ { proxy_pass http://127.0.0.1:8790; client_max_body_size 6m; }
"""

import argparse
import hmac
import json
import os
import sys
import tempfile
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

MAX_BYTES = 5 * 1024 * 1024


def make_handler(path, file, key):
    class Handler(BaseHTTPRequestHandler):
        server_version = "tropa-friends"
        sys_version = ""

        def _send(self, code, body=b"", ctype="text/plain; charset=utf-8"):
            self.send_response(code)
            self.send_header("Content-Type", ctype)
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            if body and self.command != "HEAD":
                self.wfile.write(body)

        def do_GET(self):
            if self.path.split("?", 1)[0] != path:
                return self._send(404)
            try:
                with open(file, "rb") as f:
                    data = f.read()
            except FileNotFoundError:
                return self._send(404, "Ещё ничего не опубликовано.".encode())
            self._send(200, data, "application/json; charset=utf-8")

        do_HEAD = do_GET

        def do_PUT(self):
            if self.path.split("?", 1)[0] != path:
                return self._send(404)
            auth = self.headers.get("Authorization", "")
            given = auth[7:] if auth.startswith("Bearer ") else ""
            if not hmac.compare_digest(given.encode(), key.encode()):
                return self._send(401)
            length = int(self.headers.get("Content-Length") or 0)
            if length <= 0 or length > MAX_BYTES:
                return self._send(413)
            body = self.rfile.read(length)
            try:
                doc = json.loads(body.decode("utf-8"))
            except (UnicodeDecodeError, json.JSONDecodeError):
                return self._send(400, "Не JSON.".encode())
            if not isinstance(doc, dict) or doc.get("kind") != "tropa-admin":
                return self._send(400, "Это не документ Тропы.".encode())
            directory = os.path.dirname(os.path.abspath(file))
            os.makedirs(directory, exist_ok=True)
            fd, tmp = tempfile.mkstemp(dir=directory, prefix=".doc-")
            try:
                with os.fdopen(fd, "wb") as f:
                    f.write(body)
                    f.flush()
                    os.fsync(f.fileno())
                os.chmod(tmp, 0o600)
                os.replace(tmp, file)
            except OSError:
                if os.path.exists(tmp):
                    os.remove(tmp)
                return self._send(500)
            self._send(204)

        def log_message(self, fmt, *args):
            # Путь — секрет: в журнал пишем только метод и код ответа.
            sys.stderr.write("%s %s\n" % (self.command, args[1] if len(args) > 1 else ""))

    return Handler


def main():
    ap = argparse.ArgumentParser(description="Сервер публикации «Для друзей» для Тропы")
    ap.add_argument("--path", required=True, help="секретный путь, например /tropa/3f9c...")
    ap.add_argument("--file", required=True, help="где хранить документ")
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=8790)
    args = ap.parse_args()
    key = os.environ.get("TROPA_FRIENDS_KEY", "")
    if len(key) < 16:
        sys.exit("Задайте TROPA_FRIENDS_KEY — ключ публикации из Тропы («Скопировать ключ»).")
    if not args.path.startswith("/") or len(args.path) < 16:
        sys.exit("--path должен начинаться с / и содержать длинную случайную часть.")
    ThreadingHTTPServer((args.host, args.port), make_handler(args.path, args.file, key)).serve_forever()


if __name__ == "__main__":
    main()
