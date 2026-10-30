#!/usr/bin/env python3
"""Сервер журнала для разработчика (ADR-050) — один файл, только стандартная библиотека Python 3.8+.

Тропа пользователей отправляет пакеты событий POST-запросом (Authorization: Bearer <токен из
tools/telemetry.config.json>). Каждое событие — код классификатора («2.3») и короткие поля.
Расшифровка кодов — tropa-telemetry-codes.json рядом со скриптом (его держит в порядке тест Тропы).

Хранение: <dir>/<ГГГГ-ММ-ДД>/<id>.jsonl — одна строка на событие, плюс <dir>/users.json
(id → имя, версия, последний раз). Дни старше --keep-days удаляются сами.
Пользователь — случайный id установки (32 hex) и имя, если он сам его указал. Больше ничего.

Запуск (сервер слушает 127.0.0.1, HTTPS снаружи даёт nginx):
    TROPA_LOGS_TOKEN=<токен> python3 tropa-logs-server.py serve --path /tropa-logs/<случайная-строка> --dir /var/lib/tropa-logs

nginx (внутри server { listen 443 ssl; ... }):
    location /tropa-logs/ { proxy_pass http://127.0.0.1:8791; client_max_body_size 2m; }

Просмотр (на сервере, по ssh):
    python3 tropa-logs-server.py users --dir /var/lib/tropa-logs
    python3 tropa-logs-server.py show  --dir /var/lib/tropa-logs --user Вася --since 6h
    python3 tropa-logs-server.py show  --dir /var/lib/tropa-logs --user 3f9c --code 2. --code 9.
    python3 tropa-logs-server.py net   --dir /var/lib/tropa-logs --since 24h      # сетевые ошибки по классам
    python3 tropa-logs-server.py drops --dir /var/lib/tropa-logs --since 7d       # обрывы и неудачные подключения
"""

import argparse
import datetime as dt
import hmac
import json
import os
import re
import shutil
import sys
import tempfile
import threading
import time
from collections import defaultdict
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

MAX_BYTES = 1024 * 1024
MAX_EVENTS = 500
MAX_FIELD = 2000
MAX_FIELDS = 300
ID_RE = re.compile(r"^[0-9a-f]{32}$")
CODE_RE = re.compile(r"^\d{1,2}\.\d{1,3}$")
RATE_PER_MIN = 30

HERE = os.path.dirname(os.path.abspath(__file__))


# ---------------- Хранилище ----------------

def day_dir(root, ts_ms):
    day = dt.datetime.fromtimestamp(ts_ms / 1000, tz=dt.timezone.utc).strftime("%Y-%m-%d")
    return os.path.join(root, day)


def atomic_write(path, data):
    directory = os.path.dirname(path)
    fd, tmp = tempfile.mkstemp(dir=directory, prefix=".tmp-")
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as f:
            f.write(data)
            f.flush()
            os.fsync(f.fileno())
        os.chmod(tmp, 0o600)
        os.replace(tmp, path)
    except OSError:
        if os.path.exists(tmp):
            os.remove(tmp)
        raise


def load_users(root):
    try:
        with open(os.path.join(root, "users.json"), encoding="utf-8") as f:
            return json.load(f)
    except (FileNotFoundError, json.JSONDecodeError):
        return {}


def clean_text(value, limit):
    if not isinstance(value, str):
        value = str(value)
    value = "".join(ch for ch in value if ch >= " " or ch == "\t")
    return value[:limit]


def validate_batch(doc):
    """Проверенный пакет или текст ошибки. Всё из запроса — недоверенные данные."""
    if not isinstance(doc, dict) or doc.get("v") != 1:
        return None, "Неизвестная версия."
    uid = doc.get("id")
    if not isinstance(uid, str) or not ID_RE.match(uid):
        return None, "Неверный id."
    events = doc.get("events")
    if not isinstance(events, list) or not 0 < len(events) <= MAX_EVENTS:
        return None, "Нет событий."
    now_ms = int(time.time() * 1000)
    clean = []
    for e in events:
        if not isinstance(e, dict):
            continue
        code, t = e.get("c"), e.get("t")
        if not isinstance(code, str) or not CODE_RE.match(code) or not isinstance(t, int):
            continue
        # Часы у пользователей бывают неверными: дальше суток в будущее — ставим время приёма.
        if t > now_ms + 86400000 or t < now_ms - 30 * 86400000:
            t = now_ms
        data = e.get("d")
        fields = {}
        if isinstance(data, dict):
            for k, v in list(data.items())[:MAX_FIELDS]:
                fields[clean_text(k, 80)] = clean_text(v, MAX_FIELD)
        clean.append({"t": t, "c": code, "d": fields})
    if not clean:
        return None, "Нет верных событий."
    return {
        "id": uid,
        "name": clean_text(doc.get("name", ""), 40),
        "app": clean_text(doc.get("app", ""), 20),
        "events": clean,
    }, None


class Store:
    def __init__(self, root, keep_days):
        self.root = root
        self.keep_days = keep_days
        self.lock = threading.Lock()
        self.rate = defaultdict(list)
        self.last_cleanup = 0
        os.makedirs(root, exist_ok=True)

    def allowed(self, uid):
        now = time.time()
        with self.lock:
            hits = [h for h in self.rate[uid] if now - h < 60]
            if len(hits) >= RATE_PER_MIN:
                self.rate[uid] = hits
                return False
            hits.append(now)
            self.rate[uid] = hits
            return True

    def save(self, batch):
        received = int(time.time() * 1000)
        by_day = defaultdict(list)
        for e in batch["events"]:
            e["rt"] = received
            by_day[day_dir(self.root, e["t"])].append(e)
        with self.lock:
            for directory, events in by_day.items():
                os.makedirs(directory, exist_ok=True)
                path = os.path.join(directory, batch["id"] + ".jsonl")
                with open(path, "a", encoding="utf-8") as f:
                    for e in events:
                        f.write(json.dumps(e, ensure_ascii=False, separators=(",", ":")) + "\n")
                os.chmod(path, 0o600)
            users = load_users(self.root)
            u = users.get(batch["id"], {})
            if batch["name"]:
                u["name"] = batch["name"]
            u["app"] = batch["app"]
            u["last"] = received
            u.setdefault("first", received)
            users[batch["id"]] = u
            atomic_write(os.path.join(self.root, "users.json"), json.dumps(users, ensure_ascii=False, indent=1))
            self.cleanup()

    def cleanup(self):
        if time.time() - self.last_cleanup < 3600:
            return
        self.last_cleanup = time.time()
        border = (dt.datetime.now(dt.timezone.utc) - dt.timedelta(days=self.keep_days)).strftime("%Y-%m-%d")
        for name in os.listdir(self.root):
            if re.match(r"^\d{4}-\d{2}-\d{2}$", name) and name < border:
                shutil.rmtree(os.path.join(self.root, name), ignore_errors=True)


def make_handler(path, token, store):
    class Handler(BaseHTTPRequestHandler):
        server_version = "tropa-logs"
        sys_version = ""

        def _send(self, code, body=b""):
            self.send_response(code)
            self.send_header("Content-Type", "text/plain; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            if body:
                self.wfile.write(body)

        def do_GET(self):
            self._send(404)

        def do_POST(self):
            if self.path.split("?", 1)[0] != path:
                return self._send(404)
            auth = self.headers.get("Authorization", "")
            given = auth[7:] if auth.startswith("Bearer ") else ""
            if not hmac.compare_digest(given.encode(), token.encode()):
                return self._send(401)
            length = int(self.headers.get("Content-Length") or 0)
            if length <= 0 or length > MAX_BYTES:
                return self._send(413)
            try:
                doc = json.loads(self.rfile.read(length).decode("utf-8"))
            except (UnicodeDecodeError, json.JSONDecodeError):
                return self._send(400, "Не JSON.".encode())
            batch, error = validate_batch(doc)
            if error:
                return self._send(400, error.encode())
            if not store.allowed(batch["id"]):
                return self._send(429)
            try:
                store.save(batch)
            except OSError:
                return self._send(500)
            self._send(204)

        def log_message(self, fmt, *args):
            # Путь — секрет: в журнал только метод и код ответа.
            sys.stderr.write("%s %s\n" % (self.command, args[1] if len(args) > 1 else ""))

    return Handler


# ---------------- Просмотр ----------------

def load_codes():
    try:
        with open(os.path.join(HERE, "tropa-telemetry-codes.json"), encoding="utf-8") as f:
            return json.load(f).get("codes", {})
    except (FileNotFoundError, json.JSONDecodeError):
        return {}


def parse_since(text):
    m = re.match(r"^(\d+)([mhd])$", text or "")
    if not m:
        sys.exit("--since: например 30m, 6h, 7d")
    n, unit = int(m.group(1)), m.group(2)
    seconds = n * {"m": 60, "h": 3600, "d": 86400}[unit]
    return int((time.time() - seconds) * 1000)


def find_users(root, who):
    users = load_users(root)
    if not who:
        return list(users.keys()), users
    who_l = who.lower()
    found = [uid for uid, u in users.items() if uid.startswith(who_l) or u.get("name", "").lower() == who_l]
    if not found:
        sys.exit("Нет такого пользователя: " + who)
    return found, users


def read_events(root, uids, since_ms):
    since_day = dt.datetime.fromtimestamp(since_ms / 1000, tz=dt.timezone.utc).strftime("%Y-%m-%d")
    out = []
    for name in sorted(os.listdir(root)):
        if not re.match(r"^\d{4}-\d{2}-\d{2}$", name) or name < since_day:
            continue
        for uid in uids:
            path = os.path.join(root, name, uid + ".jsonl")
            if not os.path.exists(path):
                continue
            with open(path, encoding="utf-8") as f:
                for line in f:
                    try:
                        e = json.loads(line)
                    except json.JSONDecodeError:
                        continue
                    if e.get("t", 0) >= since_ms:
                        e["uid"] = uid
                        out.append(e)
    out.sort(key=lambda e: e["t"])
    return out


def who(users, uid):
    name = users.get(uid, {}).get("name")
    return f"{name} ({uid[:6]})" if name else uid[:8]


def fmt_time(ms):
    return dt.datetime.fromtimestamp(ms / 1000).strftime("%m-%d %H:%M:%S")


def cmd_users(args):
    users = load_users(args.dir)
    for uid, u in sorted(users.items(), key=lambda kv: -kv[1].get("last", 0)):
        print(f"{uid[:8]}  {u.get('name', '') or '—':<16} {u.get('app', ''):<8} последний раз {fmt_time(u.get('last', 0))}")


def cmd_show(args):
    codes = load_codes()
    uids, users = find_users(args.dir, args.user)
    for e in read_events(args.dir, uids, parse_since(args.since)):
        if args.code and not any(e["c"] == c or (c.endswith(".") and e["c"].startswith(c)) for c in args.code):
            continue
        fields = " ".join(f"{k}={v}" for k, v in e.get("d", {}).items())
        prefix = f"{fmt_time(e['t'])} {who(users, e['uid']):<20} " if len(uids) > 1 else f"{fmt_time(e['t'])} "
        text = codes.get(e["c"], "?")
        if args.raw:
            print(prefix + e["c"] + " " + fields)
        else:
            print(f"{prefix}{e['c']:<5} {text}: {fields}")


def cmd_net(args):
    """Классы сетевых ошибок (9.x): сколько раз, у скольких людей, самые частые шаблоны."""
    codes = load_codes()
    uids, users = find_users(args.dir, args.user)
    by_class = defaultdict(lambda: {"n": 0, "users": set(), "tpl": defaultdict(lambda: [0, set()])})
    for e in read_events(args.dir, uids, parse_since(args.since)):
        if not e["c"].startswith("9."):
            continue
        d = e.get("d", {})
        try:
            n = int(d.get("n", "1"))
        except ValueError:
            n = 1
        c = by_class[e["c"]]
        c["n"] += n
        c["users"].add(e["uid"])
        t = c["tpl"][(d.get("src", "?"), d.get("tpl", ""))]
        t[0] += n
        t[1].add(e["uid"])
    for code, c in sorted(by_class.items(), key=lambda kv: -kv[1]["n"]):
        print(f"\n{code:<5} {codes.get(code, '?')}: {c['n']} раз, людей: {len(c['users'])} ({', '.join(who(users, u) for u in c['users'])})")
        for (src, tpl), (n, us) in sorted(c["tpl"].items(), key=lambda kv: -kv[1][0])[: args.top]:
            print(f"   {n:>6} × {len(us)} чел. [{src}] {tpl}")


def cmd_drops(args):
    """Обрывы: неудачные подключения, падения ядра, потеря службы, переподключения — по людям."""
    codes = load_codes()
    watch = {"2.3", "2.6", "2.7", "2.8", "2.9", "1.5", "2.5", "3.1", "1.3"}
    uids, users = find_users(args.dir, args.user)
    per_user = defaultdict(lambda: defaultdict(int))
    for e in read_events(args.dir, uids, parse_since(args.since)):
        if e["c"] in watch:
            per_user[e["uid"]][e["c"]] += 1
    for uid, counts in sorted(per_user.items(), key=lambda kv: -sum(kv[1].values())):
        print(f"\n{who(users, uid)}")
        for code, n in sorted(counts.items(), key=lambda kv: -kv[1]):
            print(f"   {n:>5}  {code:<5} {codes.get(code, '?')}")


def main():
    ap = argparse.ArgumentParser(description="Сервер журнала Тропы для разработчика")
    sub = ap.add_subparsers(dest="cmd", required=True)

    s = sub.add_parser("serve", help="принимать журналы")
    s.add_argument("--path", required=True, help="секретный путь, например /tropa-logs/3f9c...")
    s.add_argument("--dir", required=True)
    s.add_argument("--host", default="127.0.0.1")
    s.add_argument("--port", type=int, default=8791)
    s.add_argument("--keep-days", type=int, default=30)

    for name, func in (("users", cmd_users), ("show", cmd_show), ("net", cmd_net), ("drops", cmd_drops)):
        p = sub.add_parser(name)
        p.add_argument("--dir", required=True)
        p.set_defaults(func=func)
        if name != "users":
            p.add_argument("--user", help="имя или начало id; без него — все")
            p.add_argument("--since", default="24h")
        if name == "show":
            p.add_argument("--code", action="append", help="код или раздел с точкой: 2.3, 9.")
            p.add_argument("--raw", action="store_true")
        if name == "net":
            p.add_argument("--top", type=int, default=5)

    args = ap.parse_args()
    if args.cmd != "serve":
        return args.func(args)
    token = os.environ.get("TROPA_LOGS_TOKEN", "")
    if len(token) < 16:
        sys.exit("Задайте TROPA_LOGS_TOKEN (тот же, что token в tools/telemetry.config.json).")
    if not args.path.startswith("/") or len(args.path) < 16:
        sys.exit("--path должен начинаться с / и содержать длинную случайную часть.")
    store = Store(args.dir, args.keep_days)
    ThreadingHTTPServer((args.host, args.port), make_handler(args.path, token, store)).serve_forever()


if __name__ == "__main__":
    main()
