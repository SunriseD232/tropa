# 04. Модель данных, форматы, совместимость

## 1. Профиль сервера

```csharp
record Profile(
    Guid Id,
    string Name,                     // очищено NameSanitizer, ≤ 64 символов
    Guid? SubscriptionId,            // null = добавлен вручную
    Protocol Protocol,               // Vless | Vmess | Trojan  (позже Shadowsocks, Hysteria2)
    string Address, int Port,
    SecretRef Credential,            // UUID или пароль; хранится в SecretStore
    VlessOptions? Vless,             // Flow: None | XtlsRprxVision; Encryption = "none"
    VmessOptions? Vmess,             // Security: auto|aes-128-gcm|chacha20-poly1305|none; AlterId = 0
    Transport Transport,             // Type: Tcp | Xhttp | Ws | Grpc | HttpUpgrade; Path, Host, ServiceName, XhttpMode
    Security Security,               // Type: None | Tls | Reality; Sni, Alpn[], Fingerprint, AllowInsecure;
                                     // Reality: PublicKey, ShortId, SpiderX
    Guid? ChainVia,                  // цепочка: подключаться через другой профиль
    ProfileUserOverrides Overrides,  // правки пользователя, которые переживают обновление подписки
    TestHistory LastTests);
```

**Идентичность при обновлении подписки:** ключ = (протокол, адрес, порт, хэш credential). Совпал — обновляем поля и сохраняем Overrides, избранное и порядок. Пропал — помечаем «удалён провайдером» и убираем через 7 дней.

## 2. Подписка

```csharp
record Subscription(Guid Id, string Name, SecretRef Url, UpdateInterval Interval, UserAgentMode UA,
    string? CustomUA, bool SendHwid, bool FetchViaProxy, SubscriptionInfo? Info, DateTimeOffset? LastUpdated, string? LastError);
record SubscriptionInfo(long Upload, long Download, long Total, DateTimeOffset? Expire, string? SupportUrl, string? WebPageUrl, string? Announce);
```

### Получение
- GET по HTTPS, лимиты из `02-security.md`.
- User-Agent: `Tropa/x.y`, `v2rayN/7.x` (совместимость) или `sing-box 1.12` (тогда может прийти JSON).
- HWID (если включено): заголовки `x-hwid` (хэш SHA-256 от MachineGuid с солью приложения, первые 32 символа), `x-device-os: Windows`, `x-ver-os`, `x-device-model`. **Сверить с документацией Remnawave при реализации.**
- Заголовки ответа:
  - `subscription-userinfo: upload=…; download=…; total=…; expire=<unix>`;
  - `profile-update-interval: <часы>`;
  - `profile-title` (может быть `base64:…`);
  - `support-url`, `profile-web-page-url`, `announce`.

### Формат тела (определять по порядку)
1. JSON → импорт sing-box (`outbounds`) или Xray (`outbounds`); только outbound-ы, см. `02-security.md` §3.2.
2. Иначе: если после удаления пробелов это base64 (стандартный или URL-safe, паддинг необязателен) → декодировать.
3. Текст: по строке на ссылку. Строки с неизвестной схемой пропускаются и учитываются в отчёте импорта («распознано N, ошибок M»).
4. Clash YAML — позже (этап 8), только секция `proxies`.

## 3. Форматы ссылок

### vless://
```
vless://<uuid>@<host>:<port>?encryption=none&type=<tcp|xhttp|ws|grpc|httpupgrade>&security=<none|tls|reality>
  &sni=&fp=&alpn=&allowInsecure=0|1&pbk=&sid=&spx=&flow=xtls-rprx-vision
  &path=&host=&serviceName=&mode=<auto|packet-up|stream-up>&headerType=none#<name>
```
- `type=raw` — синоним `tcp` (новые версии Xray).
- IPv6-хост в квадратных скобках.
- Параметры URL-decode, имя после `#` тоже.

### vmess://
`vmess://<base64(JSON)>`, JSON: `{v:"2", ps, add, port, id, aid, scy, net, type, host, path, tls, sni, alpn, fp}`. `port`/`aid` бывают строками и числами. `aid ≠ 0` — предупреждение (устаревший не-AEAD режим, поддерживать не будем).

### trojan://
`trojan://<password>@<host>:<port>?security=tls&sni=&fp=&alpn=&type=&path=&host=&serviceName=#<name>`. По умолчанию security=tls.

### Корпус тестов
`tests/Tropa.Core.Tests/Corpus/` — реальные ссылки из популярных панелей (секреты заменены): с IPv6, без паддинга, с `type=raw`, с лишними параметрами, битые. Для каждого файла — ожидаемый результат. Сломанная ссылка не роняет импорт всей подписки.

## 4. Правила маршрутизации

```csharp
record Rule(Guid Id, bool Enabled, string? Label, RuleMatch Match, RuleAction Tcp, RuleAction Udp);
record RuleMatch(string[] Processes, string[] ProcessPaths, string[] Domains, string[] DomainSuffixes,
    string[] DomainKeywords, string[] DomainRegexes, string[] GeoSite, string[] GeoIp, string[] IpCidrs, PortRange[] Ports);
enum RuleAction { Proxy, Direct, Block, Server /* + Guid конкретного профиля */ }
```

- Семантика условий (как в sing-box): внутри группы «ИЛИ», между группами «И». Группы: процесс; адрес (домены, geosite, geoip, CIDR); порт. Пример: «game.exe И порт 443».
- Порядок: системные правила (DNS, LAN, блок QUIC) → пользовательские сверху вниз → финальное правило пресета.
- Раздельные TCP/UDP: при генерации правило разворачивается в два правила ядра с `network: tcp` и `network: udp`. Если действия одинаковые — в одно без `network`.
- Правила с `Processes` действуют только в TUN. В других режимах они не генерируются, а в UI помечаются.

### Пресеты
| Пресет | Правила |
|---|---|
| `except_ru` | ruServices → direct; **заблокированное в РФ (geosite/geoip ru-blocked) → proxy, даже если это .ru**; geosite:category-ru → direct; .ru/.su/.рф → direct; geoip:ru → direct; final → proxy |
| `blocked` | ruServices → direct; списки блокировок РФ (geosite/geoip) → proxy; final → direct |
| `all` | geoip:private → direct; final → proxy |

`ruServices` — наш список доменов госуслуг, банков, налоговой, маркетплейсов: `src/Tropa.Core/Routing/ru-services.txt`.

## 5. Правила совместимости (единый источник: `Tropa.Core/Compatibility/CompatRules.cs`)

Каждое правило: `(цель, условие, причина, принудительное значение?)`. Пока условие истинно, цель неактивна, а её **эффективное** значение равно принудительному (если задано). Сохранённое значение пользователя не трогается и возвращается, когда условие снимается. Первое сработавшее правило для цели побеждает.

### Настройки
| Цель | Условие | Принудительно | Причина |
|---|---|---|---|
| killSwitch | mode ≠ tun | false | работает только в режиме «Весь компьютер» |
| fakeip | mode ≠ tun | false | то же |
| dnsHijack | mode ≠ tun | false | то же |
| tunStack, mtu, strictRoute, lanBypass | mode ≠ tun | — | то же |
| sysBypass, uwpLoopback | mode ≠ proxy | — | нужно только в режиме «Только браузеры» |
| localPass | lanAllow | true | пароль обязателен при доступе из LAN |
| autoInterval, autoTol | !autoSelect | — | включите авто-выбор |
| stunServer | !udpTestOn | — | включите проверку UDP |
| routeOnly | !sniffing | false | включите определение домена |
| sniffHttp/Tls/Quic | !sniffing | false | определение домена выключено |
| fragPackets/Len/Int/Scope | !fragment | — | включите фрагментацию |
| noise | coreChoice = singbox | false | шум есть только в Xray |
| noiseType/Len/Delay | !noise | — | включите шум |
| mux | у активного профиля flow = vision **или** transport = xhttp | false | Vision с Mux не работает / у XHTTP своё мультиплексирование |
| muxConc | !mux | — | Mux выключен |
| templateText | !template | — | включите шаблон |
| subUACustom | subUA ≠ custom | — | выберите «Свой» |

### Профиль сервера
| Цель | Условие | Причина |
|---|---|---|
| protocol=vmess | security = reality | VMess не работает с Reality |
| protocol=trojan | security = reality | Reality в Тропе только для VLESS |
| protocol=trojan | security = none | Trojan всегда поверх TLS |
| security=reality | protocol ≠ vless | Reality только для VLESS |
| security=reality | transport = ws | Reality не работает с WebSocket |
| security=none | protocol = trojan | Trojan без TLS не бывает |
| transport=ws | security = reality | WebSocket несовместим с Reality |
| flow (→ none) | protocol ≠ vless / transport ≠ tcp / security = none | Vision только VLESS + TCP + TLS/Reality |

Валидатор перед запуском ядра прогоняет те же правила. Профиль из подписки, нарушающий правила, импортируется с пометкой «несовместимые параметры» и не запускается.

**Сверить при реализации:** Reality для Trojan (Xray поддерживает, sing-box — проверить), Reality + gRPC/XHTTP (поддерживается в Xray).
