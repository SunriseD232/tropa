# 05. Генерация конфигов ядер

> Схемы ядер меняются между версиями. Ниже — ориентиры, написанные по известным версиям (sing-box 1.12, Xray 25.x). **Закреплены сейчас sing-box 1.14.2 и Xray 26.9.30** (`tools/cores.lock.json`). **Перед реализацией каждого блока сверяться с официальной документацией закреплённой версии** и фиксировать её в golden-тестах.

## 1. Выбор ядра (`CoreSelector`)

```
если coreChoice = xray            → Xray целиком
если coreChoice = singbox         → sing-box целиком; профили с XHTTP недоступны; noise выключен
если auto:
   нужен Xray-only функционал? (transport = xhttp у активного профиля или цепочки, noise = on)
      да  → sing-box (фронт: inbound-ы, DNS, правила) + Xray (только нужные outbound-ы за SOCKS)
      нет → только sing-box
```

## 2. sing-box: общая структура

```jsonc
{
  "log": { "level": "warn", "timestamp": true },          // без "output": логи читает служба из stdout
  "dns": {
    "servers": [
      { "type": "https", "tag": "remote", "server": "1.1.1.1", "detour": "proxy" },
      { "type": "udp",   "tag": "local",  "server": "77.88.8.8" },
      { "type": "fakeip","tag": "fake",   "inet4_range": "198.18.0.0/15" }
    ],
    "rules": [
      { "rule_set": ["ru-services", "geosite-category-ru"], "server": "local" },
      { "query_type": ["A", "AAAA"], "server": "fake" }   // только при fakeip и mode=tun
    ],
    "final": "remote",
    "strategy": "ipv4_only"                                // при ipv6Block
  },
  "inbounds": [
    // mode=tun:
    { "type": "tun", "tag": "tun-in", "address": ["172.19.0.1/30"], "mtu": 9000,
      "auto_route": true, "strict_route": true, "stack": "mixed",
      "route_exclude_address_set": ["geoip-private"] },     // при lanBypass
    // всегда (для программ, настроенных вручную, и для загрузок самой Тропы через прокси):
    { "type": "mixed", "tag": "mixed-in", "listen": "127.0.0.1", "listen_port": 10808,
      "users": [{ "username": "<random>", "password": "<random>" }] }
  ],
  "outbounds": [
    { "type": "vless", "tag": "proxy", ... },              // активный профиль (или urltest-группа)
    { "type": "direct", "tag": "direct" }
  ],
  "route": {
    "rule_set": [ /* локальные .srs из %LOCALAPPDATA%\Tropa\geo, проверенные по хэшу */ ],
    "rules": [
      { "action": "sniff" },                               // при sniffing
      { "protocol": "dns", "action": "hijack-dns" },       // при dnsHijack
      { "network": "udp", "port": 443, "action": "reject" },// при blockQuic
      { "ip_is_private": true, "outbound": "direct" },
      /* пользовательские правила, развёрнутые по TCP/UDP */
      /* правила пресета */
    ],
    "final": "proxy",
    "auto_detect_interface": true,
    "find_process": true                                    // при правилах по процессам и mode=tun
  },
  "experimental": {
    "clash_api": { "external_controller": "127.0.0.1:<random>", "secret": "<random>" }  // статистика
  }
}
```

Важно:
- В sing-box 1.12 гео-базы `geosite.db`/`geoip.db` убраны. Используются только rule-set `.srs`, локальные файлы, никаких `remote` rule-set (загрузку делает Тропа сама, с проверкой).
- `users` на mixed-inbound включается всегда (localPass), кроме случая, когда пользователь сознательно выключил пароль и LAN закрыт.
- При `lanAllow`: `listen: "0.0.0.0"` и пароль обязателен.

### Outbound-ы sing-box по протоколам

```jsonc
// VLESS + Reality + Vision
{ "type": "vless", "tag": "proxy", "server": "nl1.example", "server_port": 443,
  "uuid": "<uuid>", "flow": "xtls-rprx-vision",
  "tls": { "enabled": true, "server_name": "<sni>",
           "utls": { "enabled": true, "fingerprint": "chrome" },
           "reality": { "enabled": true, "public_key": "<pbk>", "short_id": "<sid>" } },
  "packet_encoding": "xudp" }

// VMess + WS + TLS
{ "type": "vmess", "server": "...", "server_port": 443, "uuid": "<uuid>", "security": "auto", "alter_id": 0,
  "tls": { "enabled": true, "server_name": "<sni>", "utls": { "enabled": true, "fingerprint": "chrome" } },
  "transport": { "type": "ws", "path": "/path", "headers": { "Host": "<host>" } } }

// Trojan + TLS
{ "type": "trojan", "server": "...", "server_port": 443, "password": "<pwd>",
  "tls": { "enabled": true, "server_name": "<sni>", "alpn": ["h2", "http/1.1"], "utls": { "enabled": true, "fingerprint": "chrome" } } }

// gRPC: "transport": { "type": "grpc", "service_name": "<name>" }
// HTTPUpgrade: "transport": { "type": "httpupgrade", "path": "...", "host": "..." }
// Цепочка: "detour": "<tag промежуточного outbound>"
// Mux: "multiplex": { "enabled": true, "protocol": "h2mux", "max_streams": 8 }
```

### Авто-выбор
```jsonc
{ "type": "urltest", "tag": "proxy", "outbounds": ["p1", "p2", "p3"],
  "url": "https://www.gstatic.com/generate_204", "interval": "3m", "tolerance": 50 }
```
Сам urltest не ловит «заморозку». Поэтому служба дополнительно гоняет тест скорости по расписанию (`07-testing-diagnostics.md`) и исключает «замёрзшие» серверы из группы перегенерацией конфига.

### Фрагментация прямого трафика (без zapret/WinDivert)
Работает только для трафика, который проходит через ядро, то есть в режиме TUN или через mixed-inbound. В sing-box 1.12 это опции маршрута `tls_fragment` / `tls_record_fragment` в правиле с `action: route` и `outbound: direct`. **Сверить точные имена полей.** Правило ставится на домены из списка `fragScope` (или на всё прямое при `all`).

Если нужна точная настройка размера и паузы, которой нет в sing-box, прямой трафик из списка отправляется в Xray `freedom` с `fragment` (гибридная схема).

## 3. Xray

### Режим «только outbound за SOCKS» (гибрид)
```jsonc
{
  "log": { "loglevel": "warning" },
  "inbounds": [ { "tag": "in", "listen": "127.0.0.1", "port": <random>, "protocol": "socks",
                  "settings": { "auth": "password", "accounts": [{ "user": "<r>", "pass": "<r>" }], "udp": true } } ],
  "outbounds": [ { "tag": "proxy", "protocol": "vless", "settings": { "vnext": [ { "address": "...", "port": 443,
        "users": [ { "id": "<uuid>", "encryption": "none", "flow": "" } ] } ] },
      "streamSettings": { "network": "xhttp", "security": "reality",
        "xhttpSettings": { "path": "/x", "mode": "auto" },
        "realitySettings": { "serverName": "<sni>", "fingerprint": "chrome", "publicKey": "<pbk>", "shortId": "<sid>", "spiderX": "/" } } } ]
}
```
В sing-box этот профиль описывается так: `{ "type": "socks", "tag": "proxy", "server": "127.0.0.1", "server_port": <random>, "username": "<r>", "password": "<r>", "version": "5" }`.

### Фрагментация и шум (Xray `freedom`)
```jsonc
{ "tag": "direct-frag", "protocol": "freedom", "settings": {
    "fragment": { "packets": "tlshello", "length": "100-200", "interval": "10-20" },
    "noises": [ { "type": "rand", "packet": "10-20", "delay": "10-16" } ] } }
```

## 4. Golden-тесты

`tests/Tropa.Core.Tests/Golden/<сценарий>.singbox.json` и `.xray.json`. Сценарии минимум такие:
1. VLESS Reality Vision, TUN, except_ru, FakeIP.
2. То же в режиме proxy (без TUN, без process-правил).
3. VMess WS TLS.
4. Trojan TLS + правило по процессу с разными TCP/UDP.
5. XHTTP → гибрид sing-box + Xray.
6. Цепочка relay → основной.
7. Авто-выбор из 3 серверов.
8. Фрагментация `soft` и шум `hard`.

Каждый golden-конфиг дополнительно проверяется настоящим ядром (`sing-box check -c`, `xray -test -c`) в интеграционных тестах.

## 5. ConfigGuard (последний рубеж в службе)

Перед запуском служба проверяет конфиг и отклоняет его, если есть хотя бы одно из:
- inbound с listen ≠ 127.0.0.1/::1, кроме разрешённого `lanAllow` mixed с паролем;
- любые пути к файлам вне `%ProgramData%\Tropa` и `%LOCALAPPDATA%\Tropa\geo`;
- `log.output`, неизвестные поля в `experimental`, `external_ui`, `external_controller` вне loopback;
- типы outbound/inbound вне белого списка.
