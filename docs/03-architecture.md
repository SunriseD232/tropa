# 03. Архитектура

## 1. Процессы

```
┌──────────────────────┐   named pipe (ACL: SYSTEM + пользователь)   ┌───────────────────────────┐
│ Tropa.App.exe        │ ─────────────────────────────────────────▶ │ Tropa.Service.exe         │
│ права пользователя   │ ◀───────────────────────────────────────── │ служба Windows, LocalSystem│
│ UI, трей, настройки, │        события: статус, трафик, логи       │ ядра, TUN, системные      │
│ подписки, генерация  │                                            │ изменения, тесты серверов │
│ конфигов, сист. прокси│                                           └──────┬──────────┬─────────┘
└──────────────────────┘                                                   │ Job Object│
                                                                           ▼          ▼
                                                                    sing-box.exe   xray.exe
                                                                    (основное)     (по требованию)
```

### Кто что делает

| Задача | App | Service |
|---|---|---|
| Интерфейс, трей, горячие клавиши, уведомления | ✓ | |
| Хранение настроек и секретов (DPAPI CurrentUser) | ✓ | |
| Загрузка подписок и гео-баз | ✓ (через прокси — через локальный порт ядра) | |
| Генерация конфигов ядер (`Tropa.Core`) | ✓ | |
| Проверка хэшей и запуск ядер | | ✓ |
| TUN (wintun), маршруты | | ✓ (через sing-box) |
| Системный прокси Windows (HKCU) | ✓ | |
| Политика DNS, брандмауэр/WFP, loopback-исключения | | ✓ |
| Тесты серверов (временные экземпляры ядра) | | ✓ |
| Журнал изменений и откат | ✓ (свои) | ✓ (свои) |

**Почему конфиг генерирует App, а не служба:** секреты расшифровываются через DPAPI CurrentUser, а у службы под SYSTEM нет к ним доступа. Служба повторно валидирует полученный конфиг (`ConfigGuard`): только loopback-inbound-ы, никаких путей к файлам вне `%ProgramData%\Tropa`, только известные типы outbound.

**Без службы (этап 2 дорожной карты):** в режиме «Только браузеры» App может сам запускать sing-box без прав администратора. Это MVP-режим. После появления службы ядра запускает только служба.

### Автозапуск
- Служба: тип запуска «Автоматически (отложенный запуск)». Её ставит установщик один раз.
- App: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` с ключом `--minimized`. Права администратора не нужны, потому что всё привилегированное делает служба.
- Тексты справки `autostart` приведены к этой схеме (ADR-005).

## 2. Проекты решения

```
src/Tropa.Core            net10.0, без IO и без зависимостей от Windows
  Model/                  Profile, Subscription, Settings, Rule, ...
  Parsing/                VlessUri, VmessUri, TrojanUri, SubscriptionParser, SingBoxImport, XrayImport
  Compatibility/          CompatRules — единый источник правил «что с чем несовместимо»
  Routing/                пресеты маршрутов, сборка правил
  Generation/             SingBoxConfigBuilder, XrayConfigBuilder, CoreSelector
  Security/               SecretScrubber, NameSanitizer, ConfigGuard
src/Tropa.Infrastructure  net10.0-windows
  Storage/                SettingsStore (атомарная запись, бэкапы, миграции), SecretStore (DPAPI)
  Net/                    SubscriptionFetcher, GeoUpdater, UpdateManifestClient (Ed25519)
  System/                 SystemProxy (HKCU), AutostartRegistry, UserJournal
src/Tropa.Ipc             контракты: команды, события, сериализация (System.Text.Json, source-gen)
src/Tropa.Service         net10.0-windows, Worker Service + WindowsServices
  PipeServer, ClientVerifier
  CoreSupervisor          запуск, хэш-проверка, Job Object, перезапуск, сбор stdout в лог
  SystemChanges/          DnsPolicy, Firewall/WFP (kill switch), LoopbackExemption, ServiceJournal
  Testing/                ServerTester (временные экземпляры ядра), Stun, SpeedTest
  Diagnostics/            шаги диагностики
src/Tropa.App             Avalonia 12, CommunityToolkit.Mvvm
  Views/ ViewModels/      экраны по макету
  Info/                   загрузка info.ru.json, InfoDialog
  Tray/, Hotkeys/, Notifications/
tests/Tropa.Core.Tests    xUnit: парсеры (корпус ссылок), CompatRules, golden-конфиги, SecretScrubber
tests/Tropa.Integration.Tests  поднимает Xray-сервер на loopback и проверяет клиентские связки
tools/                    fetch-cores.ps1 (скачать закреплённые версии и проверить хэш), sign-manifest
installer/                Inno Setup: файлы, служба, деинсталляция с откатом
```

Направление зависимостей: `App → Infrastructure → Core`, `App → Ipc`, `Service → Ipc, Core (Security, Generation.Guard)`. Core ни от кого не зависит.

## 3. IPC

- Канал: `\\.\pipe\Tropa.Service.v1`, сообщения JSON с префиксом длины, максимум 4 МБ.
- Команды (App → Service): `Hello{version}`, `Connect{coreConfigs, mode, killSwitch, ...}`, `Disconnect`, `TestServers{outboundConfigs[], kinds[]}`, `RunDiagnostics`, `ApplySystemSetting{kind, value}`, `GetStatus`, `RollbackAll`.
- События (Service → App): `Status{state, activeServer, since}`, `Traffic{up, down, perProcess[]}`, `Log{level, text}` (уже очищенный), `TestResult{...}`, `CoreCrashed{reason}`.
- Версионирование: `Hello` обменивается версиями протокола. При несовпадении App предлагает обновиться и не шлёт команды.

## 4. Хранение

| Путь | Что | Доступ |
|---|---|---|
| `%APPDATA%\Tropa\settings.json` | настройки, серверы без секретов, правила | пользователь |
| `%APPDATA%\Tropa\secrets.bin` | секреты, DPAPI CurrentUser | пользователь |
| `%APPDATA%\Tropa\backups\` | 5 последних копий настроек | пользователь |
| `%LOCALAPPDATA%\Tropa\logs\` | логи App (ротация 5×2 МБ) | пользователь |
| `%LOCALAPPDATA%\Tropa\geo\` | гео-базы | пользователь (служба получает путь и проверяет хэш) |
| `%ProgramData%\Tropa\run\` | временные конфиги ядер | только SYSTEM |
| `%ProgramData%\Tropa\journal.json` | журнал системных изменений службы | только SYSTEM |
| `%ProgramFiles%\Tropa\` | бинарники, ядра | администраторы |

Схема `settings.json` версионируется (`schemaVersion`), миграции в `SettingsStore.Migrations`.

## 5. Два ядра: схема работы

- `CoreSelector` по профилю и настройкам решает, кому что отдать (см. `05-config-generation.md`).
- **Обычный случай:** один процесс sing-box. Он обслуживает TUN или mixed-inbound, DNS, правила и outbound-ы.
- **Гибрид:** у активного профиля XHTTP или включён UDP-шум. Тогда запускается Xray с одним SOCKS-inbound на 127.0.0.1:{случайный порт} (с авторизацией), где outbound — нужный сервер. В sing-box этот профиль описывается как `socks`-outbound на этот порт. UDP проходит через SOCKS5 UDP ASSOCIATE.
- **«Всегда Xray»:** Xray обслуживает всё. Возможности TUN и правил по процессам зависят от версии Xray — сверить при реализации.
- Переключение сервера без отключения: генерируем новый конфиг и перезапускаем ядро. Горячую замену outbound-ов через API — позже, если понадобится.

## 6. Логи

- Все строки, включая stdout ядер, проходят через `SecretScrubber` до записи и до отправки в App.
- Уровень по умолчанию `warning`. `debug` автоматически выключается через 30 минут.
