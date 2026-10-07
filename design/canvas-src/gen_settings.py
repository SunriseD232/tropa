from common import *
from common import _if

SUB = ["Общие", "Подключение", "DNS", "Обход DPI", "Ядра и обновления", "Для экспертов"]

def two(c1, c2):
    col = lambda cs: f'<div style="display: flex; flex-direction: column; gap: 20px; min-width: 0">{"".join(cs)}</div>'
    return f'<div class="cols" style="display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 20px; align-items: start">{col(c1)}{col(c2)}</div>'

# ---------- 0. Общие ----------
s0 = two(
 [card("Запуск",
   tog("autostart", "Запускать вместе с Windows", "Служба стартует сама, окно UAC не появляется")
   + tog("autoconnect", "Подключаться сразу после запуска", "К последнему серверу, если он не отвечает — к лучшему")
   + tog("startMin", "Запускать свёрнутым в трей", "Окно не открывается, только иконка у часов")
   + tog("waitNet", "Ждать сеть перед подключением", "До 30 с, пока не поднимется Wi‑Fi")
   + tog("reconnect", "Переподключаться после сна и смены сети", "Сам восстановит туннель")),
  card("Подписки",
   row("subUpdate", "Обновлять подписки", "Новые серверы и ключи от провайдера",
       sel("subUpdate", [("6h", "каждые 6 ч"), ("12h", "каждые 12 ч"), ("24h", "раз в сутки"), ("manual", "вручную")]))
   + tog("subViaProxy", "Обновлять через прокси", "Если адрес подписки заблокирован")
   + row("subUA", "Представляться как", "Если подписка пришла пустой, выберите «как v2rayN»",
       sel("subUA", [("tropa", "Тропа"), ("v2rayn", "как v2rayN"), ("singbox", "как sing-box"), ("custom", "Свой")]))
   + row("subUACustom", "Свой User-Agent", None, inp("subUACustom"), info="subUA")
   + tog("hwid", "Отправлять идентификатор устройства", "Нужно, если провайдер ограничивает число устройств"))],
 [card("Защита",
   tog("killSwitch", "Аварийная блокировка (kill switch)", "Если Тропа упала, интернет блокируется, чтобы трафик не ушёл напрямую")
   + tog("ipv6Block", "Блокировать IPv6", "Чтобы трафик не утекал мимо туннеля")
   + tog("localPass", "Пароль на локальный прокси", "Чужие программы не смогут пользоваться портами")),
  card("Интерфейс",
   row("hotkey", "Вкл/выкл горячей клавишей", "Работает при свёрнутом окне", inp("hotkey"))
   + tog("notifySwitch", "Уведомлять о смене сервера", "Когда авто-выбор переключил на резервный")
   + row("theme", "Тема", None, sel("theme", [("dark", "Тёмная"), ("light", "Светлая"), ("system", "Как в Windows")]))
   + row("lang", "Язык", None, sel("lang", [("ru", "Русский"), ("en", "English")]))),
  card("Резервная копия",
   row("backup", "Настройки, правила и серверы", "Удобно отправить другу готовые правила",
       btn("Экспорт") + btn("Импорт")))])

# ---------- 1. Подключение ----------
def rcard(val, title, desc):
    return (f'<button class="rcard {H("on.mode_" + val)}" aria-pressed="{H("ap.mode_" + val)}" onClick="{H("p.mode_" + val)}">'
            f'<span class="rd"></span><span><b style="font-weight: 600">{title}</b>'
            f'<small style="display: block; color: #9AA4AE; font-size: 13px; margin-top: 3px; line-height: 1.4">{desc}</small></span></button>')
mode_card = card("Режим перехвата",
  '<div role="group" aria-label="Режим перехвата" style="display: flex; flex-direction: column; gap: 8px; padding: 8px 0 14px">'
  + rcard("tun", "Весь компьютер (TUN)", "Через виртуальную сетевую карту идёт трафик всех программ. Нужен для игр, голоса Discord, UDP и правил по приложениям")
  + rcard("proxy", "Только браузеры (системный прокси)", "Прокси прописывается в настройки Windows. Права администратора не нужны, но многие программы его игнорируют")
  + rcard("ports", "Только порты", "Ничего не перехватывает. Программы сами подключаются к SOCKS/HTTP-порту")
  + "</div>", info="mode")
s1 = two(
 [mode_card,
  card("Локальные порты",
   row("socksPort", "SOCKS5", "127.0.0.1", inp("socksPort", style="width: 110px"), info="ports")
   + row("httpPort", "HTTP", "127.0.0.1", inp("httpPort", style="width: 110px"), info="ports")
   + tog("lanAllow", "Разрешить подключения из локальной сети", "Раздать прокси телефону или ТВ. Включит обязательный пароль")),
  card("Тестирование",
   row("speedUrl", "Файл для теста скорости", "Должен быть больше 1 МБ, иначе «заморозку» не поймать", inp("speedUrl"), info="speedTest", stack=True)
   + row("speedSize", "Размер скачивания", None, sel("speedSize", [("1", "1 МБ"), ("10", "10 МБ"), ("25", "25 МБ")]), info="speedTest")
   + tog("udpTestOn", "Проверять UDP", "STUN-запрос через сервер", info="udpTest")
   + row("stunServer", "STUN-сервер", None, inp("stunServer"), info="udpTest")
   + row("parallel", "Тестировать одновременно", None, sel("parallel", [("2", "2 сервера"), ("5", "5 серверов"), ("10", "10 серверов")])))],
 [card("TUN",
   row("tunStack", "Сетевой стек", "Mixed: TCP через систему, UDP через gVisor",
       sel("tunStack", [("mixed", "Mixed"), ("gvisor", "gVisor"), ("system", "System")]))
   + row("mtu", "MTU", "Если сайты открываются наполовину, поставьте 1400", inp("mtu", style="width: 110px"))
   + tog("strictRoute", "Строгая маршрутизация", "Не даёт трафику обойти туннель через другие сетевые карты")
   + tog("lanBypass", "Локальная сеть мимо туннеля", "Принтеры, роутер, NAS доступны как обычно")),
  card("Системный прокси",
   row("sysBypass", "Не проксировать", "Адреса через точку с запятой", area("sysBypass", 64), stack=True)
   + row("uwpLoopback", "Приложения из Microsoft Store", "Снять ограничение loopback для выбранных",
         f'<button class="btn" disabled="{H("d.uwpLoopback")}">Выбрать…</button>')),
  card("Авто-выбор и резерв",
   tog("autoSelect", "Авто-выбор лучшего сервера", "Тестирует серверы подписки и переключает при сбое")
   + row("autoInterval", "Проверять каждые", None, sel("autoInterval", [("3", "3 минуты"), ("10", "10 минут"), ("30", "30 минут")]))
   + row("autoTol", "Не переключать, если разница меньше", None, inp("autoTol", style="width: 110px"))
   + row("testUrl", "Адрес проверки задержки", None, inp("testUrl"), stack=True))])

# ---------- 2. DNS ----------
flow = '''<svg viewBox="0 0 460 150" width="100%" height="150" role="img" aria-label="Схема: запрос имени проверяется правилами и уходит в удалённый или локальный DNS" style="display: block; margin: 8px 0 4px; font-family: Onest, sans-serif">
<rect x="0" y="55" width="110" height="40" rx="10" fill="#1F242A" stroke="#2C333B"/><text x="55" y="80" fill="#E6EAEE" font-size="13" text-anchor="middle">discord.com ?</text>
<rect x="160" y="55" width="110" height="40" rx="10" fill="#1F242A" stroke="#2C333B"/><text x="215" y="80" fill="#E6EAEE" font-size="13" text-anchor="middle">правила DNS</text>
<rect x="320" y="10" width="140" height="44" rx="10" fill="#132A20" stroke="#24563F"/><text x="390" y="29" fill="#7BE8B5" font-size="12" text-anchor="middle">Удалённый (DoH)</text><text x="390" y="45" fill="#9AA4AE" font-size="11" text-anchor="middle">через сервер</text>
<rect x="320" y="96" width="140" height="44" rx="10" fill="#152233" stroke="#24405F"/><text x="390" y="115" fill="#9CC4FF" font-size="12" text-anchor="middle">Локальный</text><text x="390" y="131" fill="#9AA4AE" font-size="11" text-anchor="middle">.ru и сервисы РФ</text>
<path d="M110 75h46M270 70l46-36M270 80l46 36" stroke="#5C6670" stroke-width="1.5" fill="none"/>
</svg>'''
s2 = two(
 [card("Серверы DNS", flow
   + row("remoteDns", "Удалённый DNS", "Для заблокированных и зарубежных доменов, через сервер", inp("remoteDns"), stack=True)
   + row("localDns", "Локальный DNS", "Для российских доменов, напрямую", inp("localDns"), stack=True)
   + row("localDnsRule", "Какие домены в локальный", None,
         sel("localDnsRule", [("ru", "Российские"), ("direct", "Из правил «Напрямую»"), ("none", "Никакие")]))),
  card("Проверить домен",
   '<div style="display: flex; gap: 8px; padding: 8px 0 12px; flex-wrap: wrap">' + inp("dnsCheckInput", style="flex: 1; min-width: 160px") + btn("Проверить") + "</div>"
   + '<dl class="kv" style="padding-bottom: 12px; margin: 0"><dt>Провайдер</dt><dd><span class="pill bad">заглушка</span> <span class="mono">[IP заглушки]</span></dd><dt>Через Тропу</dt><dd><span class="pill ok">настоящий</span> <span class="mono">162.159.x.x</span></dd><dt>Маршрут</dt><dd>удалённый DNS → прокси</dd></dl>',
   info="dnsCheck", desc="Сравнивает ответ провайдера и ответ через Тропу.")],
 [card("FakeIP и определение домена",
   tog("fakeip", "FakeIP", "Мгновенный фиктивный адрес, настоящий узнаёт сервер")
   + tog("sniffing", "Определять домен по трафику", "Читает имя сайта из TLS/HTTP/QUIC")
   + tog("routeOnly", "Домен только для правил", "Соединение идёт на исходный IP")),
  card("Защита от утечек",
   tog("dnsHijack", "Перехватывать все DNS-запросы", "Даже если программа спрашивает свой DNS")
   + tog("smartNameRes", "Отключить «умное» разрешение имён Windows", "Иначе запрос уходит и к провайдеру")
   + tog("dnsCache", "Кэшировать ответы", "Повторные запросы мгновенно")),
  card("Свои записи (hosts)", row("hosts", "Домен → IP", "По одной записи на строку", area("hosts", 70), stack=True))])

# ---------- 3. Обход DPI ----------
def chip(val, lbl):
    return f'<button class="chip {H("on.dpiPreset_" + val)}" aria-pressed="{H("ap.dpiPreset_" + val)}" onClick="{H("p.dpiPreset_" + val)}">{lbl}</button>'
s3 = two(
 [card("Готовые наборы",
   '<div style="display: flex; gap: 8px; flex-wrap: wrap; padding: 8px 0 12px">' + chip("off", "Выключено") + chip("soft", "Мягко") + chip("hard", "Агрессивно") + chip("custom", "Свои") + "</div>"
   + '<p class="sub" style="font-size: 13px; margin: 0 0 14px">Наборы меняют параметры ниже. Любая ручная правка переключает на «Свои».</p>',
   info="dpiPreset", desc="Только для трафика, который идёт напрямую, без сервера."),
  card("Фрагментация",
   tog("fragment", "Фрагментация TLS-приветствия", "Режет имя сайта на куски, чтобы DPI его не прочитал")
   + row("fragPackets", "Что резать", None, sel("fragPackets", [("tlshello", "tlshello"), ("1-3", "пакеты 1–3")]), info="fragment")
   + row("fragLen", "Длина куска, байт", None, inp("fragLen", style="width: 110px"), info="fragment")
   + row("fragInt", "Пауза, мс", None, inp("fragInt", style="width: 110px"), info="fragment")
   + row("fragScope", "Применять к", "youtube.com, googlevideo.com, ytimg.com…",
         sel("fragScope", [("list", "Сайтам из списка"), ("all", "Всем прямым")])))],
 [card("Маскировка под браузер",
   row("utls", "Отпечаток по умолчанию", "Для серверов, где он не указан",
       sel("utls", [("chrome", "chrome"), ("firefox", "firefox"), ("edge", "edge"), ("random", "random")]))
   + tog("allowInsecureWarn", "Предупреждать об allowInsecure", "Серверы без проверки сертификата подсвечиваются красным")),
  card("Шум",
   tog("noise", "UDP-шум перед соединением", "Сбивает сигнатуру QUIC и WireGuard-подобных протоколов")
   + row("noiseType", "Тип", None, sel("noiseType", [("rand", "rand"), ("str", "str"), ("base64", "base64")]), info="noise")
   + row("noiseLen", "Размер, байт", None, inp("noiseLen", style="width: 110px"), info="noise")
   + row("noiseDelay", "Задержка, мс", None, inp("noiseDelay", style="width: 110px"), info="noise")),
  card("Mux",
   tog("mux", "Мультиплексирование", "Несколько соединений в одном")
   + row("muxConc", "Потоков на соединение", None, inp("muxConc", style="width: 110px")))])

# ---------- 4. Ядра ----------
cores_tbl = ('<div class="scroll"><table class="tbl" style="margin: 6px 0 4px"><thead><tr><th>Ядро</th><th>Версия</th><th>Доступна</th><th></th></tr></thead><tbody>'
  '<tr><td><b style="font-weight: 600">sing-box</b><span style="display: block; font-size: 13px; color: #9AA4AE">TUN, DNS, правила</span></td><td class="mono">1.12.4</td><td class="mono">1.12.4</td><td><span class="pill ok">свежая</span></td></tr>'
  '<tr><td><b style="font-weight: 600">Xray</b><span style="display: block; font-size: 13px; color: #9AA4AE">XHTTP и новинки VLESS</span></td><td class="mono">25.9.1</td><td class="mono">25.10.2</td><td><button class="btn" style="height: 36px">Обновить</button></td></tr>'
  '</tbody></table></div>')
mx = [("VLESS Reality Vision", "✓", "✓"), ("XHTTP", "×", "✓"), ("TUN и правила по процессам", "✓", "частично"),
      ("FakeIP / FakeDNS", "✓", "✓"), ("Hysteria2, TUIC", "✓", "×"), ("UDP-шум", "×", "✓"), ("Фрагментация TLS", "✓", "✓")]
colr = lambda x: "#7BE8B5" if x == "✓" else "#FF9C9C" if x == "×" else "#F5C76A"
matrix = ('<div class="scroll"><table class="tbl" style="margin: 6px 0 10px"><thead><tr><th>Возможность</th><th>sing-box</th><th>Xray</th></tr></thead><tbody>'
  + "".join(f'<tr><td>{a}</td><td style="color: {colr(b)}">{b}</td><td style="color: {colr(c)}">{c}</td></tr>' for a, b, c in mx)
  + '</tbody></table></div><p class="sub" style="font-size: 13px; margin: 0 0 12px">Заполнено по известным версиям. Перед разработкой сверить с актуальными релизами.</p>')
s4 = two(
 [card("Ядра", cores_tbl
   + row("coreChoice", "Какое ядро использовать", None,
         sel("coreChoice", [("auto", "Автоматически"), ("singbox", "Всегда sing-box"), ("xray", "Всегда Xray")]))
   + _if("coreWarn", f'<div style="background: #2A2314; color: #F5C76A; border-radius: 12px; padding: 10px 14px; font-size: 14px; margin: 0 0 14px">{H("coreWarn")}</div>'),
   info="cores"),
  card("Что умеет каждое ядро", matrix, info="coreMatrix")],
 [card("Гео-базы и списки",
   '<dl class="kv" style="padding: 8px 0 6px; margin: 0"><dt>geosite</dt><dd>обновлено вчера</dd><dt>geoip</dt><dd>обновлено вчера</dd><dt>Блокировки РФ</dt><dd>runetfreedom · 6 ч назад</dd></dl>'
   + tog("geoUpdate", "Обновлять базы каждые сутки", "Иначе новые блокировки не обходятся")
   + tog("geoViaProxy", "Скачивать обновления через прокси", "GitHub из России иногда тормозит")
   + '<div style="padding: 10px 0 12px">' + btn("Обновить сейчас") + "</div>", info="geoBases"),
  card("Обновление Тропы",
   row("appChannel", "Канал", None, sel("appChannel", [("stable", "Стабильный"), ("beta", "Бета")]), info="appUpdate")
   + tog("appCheck", "Проверять при запуске", "Только уведомление, ставится по кнопке", info="appUpdate"))])

# ---------- 5. Для экспертов ----------
cfg = '''<pre class="mono" style="margin: 8px 0 12px; font-size: 12px; line-height: 1.6; background: #0F1215; border: 1px solid #242A31; border-radius: 10px; padding: 12px; color: #C9D1D8; height: 300px; overflow: auto">{
  "dns": { "servers": [
    { "tag": "remote", "address": "https://1.1.1.1/dns-query", "detour": "proxy" },
    { "tag": "local",  "address": "77.88.8.8", "detour": "direct" },
    { "tag": "fake",   "address": "fakeip" } ] },
  "inbounds": [ { "type": "tun", "stack": "mixed", "strict_route": true } ],
  "outbounds": [ { "type": "vless", "tag": "proxy",
      "server": "nl1.[домен]", "server_port": 443,
      "uuid": "••••", "flow": "xtls-rprx-vision",
      "tls": { "reality": { "public_key": "••••" } } } ],
  "route": { "rules": [ { "process_name": "qbittorrent.exe", "action": "reject" } ] }
}</pre>'''
def cb(k, lbl):
    return (f'<label style="display: flex; align-items: center; gap: 8px"><input type="checkbox" checked="{H("v." + k)}" disabled="{H("d." + k)}" onChange="{H("t." + k)}" style="width: 18px; height: 18px; accent-color: #3DDC97">{lbl}</label>')
s5 = two(
 [card("Сгенерированный конфиг", cfg + '<div style="display: flex; gap: 8px; flex-wrap: wrap; padding-bottom: 12px">' + btn("Копировать") + btn("Открыть папку конфигов") + "</div>",
   info="genConfig", desc="Только чтение. Так Тропа объясняет ядру ваши настройки.")],
 [card("Маршрутизация",
   row("sniffHttp", "Протоколы для определения домена", None,
       '<div style="display: flex; gap: 14px; font-size: 14px">' + cb("sniffHttp", "http") + cb("sniffTls", "tls") + cb("sniffQuic", "quic") + "</div>",
       info="sniffProto")
   + row("domainStrategy", "Стратегия доменов", None, sel("domainStrategy", [("AsIs", "AsIs"), ("IPIfNonMatch", "IPIfNonMatch"), ("IPOnDemand", "IPOnDemand")]))
   + row("logLevel", "Уровень журнала", None, sel("logLevel", [("warning", "warning"), ("info", "info"), ("debug", "debug")]))
   + _if("dbgWarn", '<div style="background: #2A2314; color: #F5C76A; border-radius: 12px; padding: 10px 14px; font-size: 14px; margin: 0 0 14px">debug записывает адреса всех сайтов. Не забудьте выключить.</div>')),
  card("Шаблон",
   tog("template", "Свой шаблон поверх сгенерированного", "Для того, чего нет в интерфейсе")
   + row("templateText", "JSON", None, area("templateText", 100), info="template", stack=True)),
  '<button class="btn" style="align-self: flex-start; color: #FF9C9C">Сбросить экспертные настройки</button>'])

SECTIONS = [s0, s1, s2, s3, s4, s5]
INTROS = ["Запуск, защита, подписки и интерфейс.",
          "Как трафик попадает в Тропу и что происходит, если сервер упал.",
          "Половина проблем «VPN включён, а сайт не открывается» живёт здесь.",
          "Приёмы для трафика, который идёт напрямую. На трафик через сервер они не влияют.",
          "Два ядра работают вместе, обычно о них думать не нужно.",
          "Неправильное значение здесь ломает подключение. Кнопка «Сбросить» вернёт всё как было."]

subnav = "".join(
    f'<button class="nav {H("on.section_" + str(k))}" aria-pressed="{H("ap.section_" + str(k))}" onClick="{H("p.section_" + str(k))}" style="height: 40px; font-size: 14px">{lbl}</button>'
    for k, lbl in enumerate(SUB))
sections = "".join(
    _if(f"sec.s{k}", f'<div style="display: flex; flex-direction: column; gap: 20px"><div><h1 class="h1">Настройки · {SUB[k]}</h1><p class="sub">{INTROS[k]}</p></div>{SECTIONS[k]}</div>')
    for k in range(6))
main_html = (f'<main class="main"><div class="cols" style="display: grid; grid-template-columns: 200px minmax(0, 1fr); gap: 24px; align-items: start">'
             f'<nav aria-label="Разделы настроек" style="display: flex; flex-direction: column; gap: 2px; padding-top: 4px">{subnav}</nav>'
             f'<div style="min-width: 0">{sections}</div></div></main>')

INIT = dict(section="0",
  autostart=True, autoconnect=True, startMin=True, waitNet=True, reconnect=True,
  killSwitch=False, ipv6Block=True, localPass=True,
  subUpdate="12h", subViaProxy=True, subUA="tropa", subUACustom="", hwid=False,
  hotkey="Ctrl+Alt+P", notifySwitch=True, theme="dark", lang="ru",
  mode="tun", tunStack="mixed", mtu="9000", strictRoute=True, lanBypass=True,
  sysBypass="localhost;127.*;10.*;172.16.*;192.168.*;*.local",
  socksPort="10808", httpPort="10809", lanAllow=False,
  autoSelect=True, autoInterval="3", autoTol="50 мс", testUrl="https://www.gstatic.com/generate_204",
  speedUrl="https://speed.cloudflare.com/__down?bytes=10000000", speedSize="10", udpTestOn=True,
  stunServer="stun.l.google.com:19302", parallel="5",
  remoteDns="https://1.1.1.1/dns-query", localDns="77.88.8.8", localDnsRule="ru",
  fakeip=True, sniffing=True, routeOnly=False, dnsHijack=True, smartNameRes=True, dnsCache=True,
  hosts="router.local  192.168.1.1", dnsCheckInput="discord.com",
  dpiPreset="off", fragment=False, fragPackets="tlshello", fragLen="100-200", fragInt="10-20", fragScope="list",
  utls="chrome", allowInsecureWarn=True, noise=False, noiseType="rand", noiseLen="10-20", noiseDelay="10-16",
  mux=False, muxConc="8", serverVision=True,
  coreChoice="auto", geoUpdate=True, geoViaProxy=True, appChannel="stable", appCheck=True,
  sniffHttp=True, sniffTls=True, sniffQuic=False, domainStrategy="IPIfNonMatch", logLevel="warning",
  template=False, templateText='{ "experimental": { "cache_file": { "enabled": true } } }')

BOOLS = [k for k, v in INIT.items() if isinstance(v, bool)]
SELECTS = [k for k, v in INIT.items() if isinstance(v, str) and k not in ("section", "mode", "dpiPreset")]
PICKS = {"section": [str(k) for k in range(6)], "mode": ["tun", "proxy", "ports"], "dpiPreset": ["off", "soft", "hard", "custom"]}

RULES = """
    const TUN = 'работает только в режиме «Весь компьютер» (раздел «Подключение»)';
    const notTun = e.mode !== 'tun';
    rule('killSwitch', notTun, TUN, false);
    rule('fakeip', notTun, TUN, false);
    rule('dnsHijack', notTun, TUN, false);
    ['tunStack', 'mtu', 'strictRoute', 'lanBypass'].forEach((k) => rule(k, notTun, TUN));
    ['sysBypass', 'uwpLoopback'].forEach((k) => rule(k, e.mode !== 'proxy', 'нужно только в режиме «Только браузеры»'));
    rule('localPass', e.lanAllow, 'пароль обязателен, пока разрешены подключения из локальной сети', true);
    ['autoInterval', 'autoTol'].forEach((k) => rule(k, !e.autoSelect, 'включите авто-выбор'));
    rule('stunServer', !e.udpTestOn, 'включите проверку UDP');
    rule('routeOnly', !e.sniffing, 'включите определение домена', false);
    ['sniffHttp', 'sniffTls', 'sniffQuic'].forEach((k) => rule(k, !e.sniffing, 'определение домена выключено в разделе DNS', false));
    ['fragPackets', 'fragLen', 'fragInt', 'fragScope'].forEach((k) => rule(k, !e.fragment, 'включите фрагментацию'));
    rule('noise', e.coreChoice === 'singbox', 'шум есть только в ядре Xray, а в разделе «Ядра» выбрано «Всегда sing-box»', false);
    ['noiseType', 'noiseLen', 'noiseDelay'].forEach((k) => rule(k, !e.noise, 'включите шум'));
    rule('mux', e.serverVision, 'активный сервер Amsterdam-1 использует XTLS Vision, а Vision с Mux не работает', false);
    rule('muxConc', !e.mux, 'Mux выключен');
    rule('templateText', !e.template, 'включите свой шаблон');
    rule('subUACustom', e.subUA !== 'custom', 'выберите «Свой» в поле выше');
"""

EXTRA = """
    const sec = {};
    ['0', '1', '2', '3', '4', '5'].forEach((k) => { sec['s' + k] = e.section === k; });
    p.dpiPreset_off = () => set({ dpiPreset: 'off', fragment: false, noise: false });
    p.dpiPreset_soft = () => set({ dpiPreset: 'soft', fragment: true, fragScope: 'list', noise: false });
    p.dpiPreset_hard = () => set({ dpiPreset: 'hard', fragment: true, fragScope: 'all', noise: true });
    ['fragment', 'noise'].forEach((k) => { t[k] = () => set({ [k]: !s[k], dpiPreset: 'custom' }); });
    ['fragScope', 'fragPackets', 'fragLen', 'fragInt', 'noiseType', 'noiseLen', 'noiseDelay'].forEach((k) => {
      sv[k] = (ev) => set({ [k]: ev.target.value, dpiPreset: 'custom' });
    });
    const coreWarn = e.coreChoice === 'singbox'
      ? 'Серверы с XHTTP (Frankfurt-2) станут недоступны, UDP-шум отключится.'
      : e.coreChoice === 'xray' ? 'TUN и правила по приложениям будут работать в урезанном виде.' : '';
    x = { sec, coreWarn, dbgWarn: e.logLevel === 'debug' };
"""

FILES = [("Settings", "0"), ("SetConnection", "1"), ("SetDNS", "2"), ("SetDPI", "3"), ("SetCores", "4"), ("SetExpert", "5")]
HEIGHTS = {"Settings": 1060, "SetConnection": 1300, "SetDNS": 1180, "SetDPI": 1120, "SetCores": 1000, "SetExpert": 1000}
for name, secn in FILES:
    init = dict(INIT, section=secn)
    scr = script(1280, HEIGHTS[name], init, RULES, EXTRA, BOOLS, SELECTS, PICKS)
    app_page(name, "Настройки · " + SUB[int(secn)], "Настройки", 1280, HEIGHTS[name], main_html, scr)
print("settings ok")
