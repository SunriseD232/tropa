import re
from common import *
from common import _if

def must_sub(src, old, new):
    assert old in src, old[:80]
    return src.replace(old, new)

# ================= Главная =================
src = open("project/Main.dc.html", encoding="utf-8").read()
m = re.search(r'(<main class="main">.*</main>)', src, re.S).group(1)
m = must_sub(m, '<span class="pill ok"><span class="dot"></span>Авто-выбор включён · резерв: Frankfurt-2</span>',
             '<span style="display: flex; align-items: center; gap: 8px"><span class="pill ok"><span class="dot"></span>Авто-выбор включён · резерв: Frankfurt-2</span>' + ib("autoSelect") + '</span>')
m = must_sub(m, '<button aria-label="Отключиться"', '<button aria-label="Отключиться"')
m = must_sub(m, '<p class="lbl">Что пускать через Тропу</p>',
             '<div style="display: flex; align-items: center; gap: 8px; margin-bottom: 10px"><p class="lbl" style="margin: 0">Что пускать через Тропу</p>' + ib("mode") + '</div>')
m = must_sub(m, '<div class="seg" role="radiogroup" aria-label="Режим перехвата">', '<div class="seg" role="group" aria-label="Режим перехвата">')
m = must_sub(m, '<button class="on" role="radio" aria-checked="true">Весь компьютер',
             f'<button class="{H("on.mode_tun")}" aria-pressed="{H("ap.mode_tun")}" onClick="{H("p.mode_tun")}">Весь компьютер')
m = must_sub(m, '<button role="radio" aria-checked="false">Только браузеры',
             f'<button class="{H("on.mode_proxy")}" aria-pressed="{H("ap.mode_proxy")}" onClick="{H("p.mode_proxy")}">Только браузеры')
m = re.sub(r'<label for="route" class="lbl" style="display: block; margin-top: 18px">Маршрут</label>\s*<select id="route">.*?</select>\s*<p class="sub" style="font-size: 13px">.*?</p>',
           '<div style="display: flex; align-items: center; gap: 8px; margin: 18px 0 10px"><label for="route" class="lbl" style="margin: 0">Маршрут</label>' + ib("route") + '</div>'
           + sel("route", [("except_ru", "Всё, кроме российского (рекомендуется)"), ("blocked", "Только заблокированное"), ("all", "Всё через прокси")])
           + f'<p class="sub" style="font-size: 13px">{H("routeDesc")}</p>', m, flags=re.S)
m = must_sub(m, '<p class="lbl">Сейчас через сервер</p>',
             '<div style="display: flex; align-items: center; gap: 8px; margin-bottom: 10px"><p class="lbl" style="margin: 0">Сейчас через сервер</p>' + ib("appRules") + '</div>')
apps = re.search(r'(<div class="check"><span class="cc" style="width: 32px">DC</span>.*?Telegram\.exe</span><span class="pill info">TCP</span></div>)', m, re.S).group(1)
m = must_sub(m, apps, _if("isTun", apps) + _if("notTun",
             '<p style="font-size: 14px; color: #F5C76A; margin: 4px 0 8px; line-height: 1.45">В режиме «Только браузеры» Тропа не видит, какая программа подключается, поэтому список и правила по приложениям недоступны.</p>'))
m = must_sub(m, '<span>DNS идёт через туннель</span></div>', '<span style="flex: 1">DNS идёт через туннель</span>' + ib("leaks") + '</div>')
m = must_sub(m, '<span>UDP работает (голос Discord)</span></div>', '<span style="flex: 1">UDP работает (голос Discord)</span>' + ib("udpTest") + '</div>')
m = must_sub(m, 'чтобы не утекал</span></div>', 'чтобы не утекал</span>' + ib("ipv6Block") + '</div>')
m = must_sub(m, '<button class="btn" style="margin-top: 10px">Проверить всё</button>', '<a class="btn" href="Diagnostics.dc.html" style="margin-top: 10px">Проверить всё</a>')
m = must_sub(m, '<span style="color: #9AA4AE; font-size: 13px">Сменить</span>', '<span style="color: #9AA4AE; font-size: 13px">Сменить</span>')
main_extra = """
    const RD = { except_ru: 'Госуслуги, банки и .ru-сайты идут напрямую, остальное через сервер.',
      blocked: 'Через сервер только сайты из списка блокировок. Новые блокировки заработают после обновления базы.',
      all: 'Через сервер идёт всё. Госуслуги и банки могут не пустить с зарубежного IP.' };
    x = { routeDesc: RD[e.route], isTun: e.mode === 'tun', notTun: e.mode !== 'tun', accent: this.props.accent ?? '#3DDC97' };
"""
app_page("Main", "Главная", "Главная", 1280, 820, m,
         script(1280, 820, {"mode": "tun", "route": "except_ru"}, "", main_extra, [], ["route"], {"mode": ["tun", "proxy"]},
                props={"accent": {"editor": "color", "default": "#3DDC97"}}))

# ================= Серверы =================
b = open("servers.body", encoding="utf-8").read()
b = must_sub(b, '<button class="btn" style="background: #3DDC97; color: #0B1A13; border-color: #3DDC97; font-weight: 600"><svg',
             '<a class="btn" href="Import.dc.html" style="background: #3DDC97; color: #0B1A13; border-color: #3DDC97; font-weight: 600"><svg')
b = must_sub(b, 'Добавить</button>', 'Добавить</a>')
for k, lbl in [("all", "Все · 14"), ("a", "[Подписка A] · 9"), ("b", "[Подписка B] · 4"), ("mine", "Мои · 1")]:
    b = re.sub(r'<button class="chip( on)?" role="tab" aria-selected="(true|false)">' + re.escape(lbl) + '</button>',
               f'<button class="chip {H("on.subTab_" + k)}" aria-pressed="{H("ap.subTab_" + k)}" onClick="{H("p.subTab_" + k)}">{lbl}</button>', b)
b = must_sub(b, 'role="tablist" aria-label="Подписки"', 'role="group" aria-label="Подписки"')
b = must_sub(b, 'Мои · 1</button>', 'Мои · 1</button>' + ib("subscription"))
b = must_sub(b, '<input type="checkbox" class="sw" checked>Авто-выбор лучшего</label>',
             f'<input type="checkbox" class="sw" checked="{H("v.autoSelect")}" onChange="{H("t.autoSelect")}">Авто-выбор лучшего</label>' + ib("autoSelect"))
for th, k in [("Протокол", "protocol"), ("Транспорт", "transport"), ("Задержка", "realDelay"), ("Скорость", "speedTest"), ("Статус", "status")]:
    b = must_sub(b, f"<th>{th}</th>", f'<th><span style="display: inline-flex; align-items: center; gap: 6px">{th} {ib(k)}</span></th>')
for dt, k in [("TCP-пинг", "tcping"), ("Реальная", "realDelay"), ("Скорость", "speedTest"), ("UDP", "udpTest"), ("Стабильность", "stability"),
              ("Безопасность", "security"), ("Отпечаток", "utls"), ("Flow", "flow"), ("Ядро", "coreChoice")]:
    b = must_sub(b, f"<dt>{dt}</dt>", f"<dt>{dt} {ib(k)}</dt>")
b = must_sub(b, '<button class="btn">Изменить</button>', '<a class="btn" href="ServerEdit.dc.html">Изменить</a>')
app_page("Servers", "Серверы", "Серверы", 1280, 820, b,
         script(1280, 820, {"autoSelect": True, "subTab": "all"}, "", "", ["autoSelect"], [], {"subTab": ["all", "a", "b", "mine"]}))

# ================= Правила =================
grip = '<svg width="14" height="14" viewBox="0 0 24 24" fill="#5C6670" aria-hidden="true"><circle cx="9" cy="6" r="1.6"/><circle cx="15" cy="6" r="1.6"/><circle cx="9" cy="12" r="1.6"/><circle cx="15" cy="12" r="1.6"/><circle cx="9" cy="18" r="1.6"/><circle cx="15" cy="18" r="1.6"/></svg>'
ACT = [("proxy", "Прокси"), ("direct", "Напрямую"), ("block", "Блок")]
def rsel(k, label):
    o = "".join(f'<option value="{a}">{l}</option>' for a, l in ACT)
    return f'<select aria-label="{label}" value="{H("v." + k)}" disabled="{H("d." + k)}" onChange="{H("sv." + k)}" style="height: 36px; width: auto; font-size: 13px; padding: 0 8px">{o}</select>'
apps = [("DC", "Discord.exe", "Голос и чат", "proxy", "proxy"),
        ("VL", "VALORANT.exe", "Игра: TCP через сервер, UDP напрямую ради пинга", "proxy", "direct"),
        ("ST", "steam.exe", "Загрузки игр не тратят трафик сервера", "direct", "direct"),
        ("QB", "qbittorrent.exe", "Торренты через чужой сервер = жалобы хостеру", "block", "block")]
sites = [("youtube.com", "домен и поддомены", "proxy", "proxy", None),
         ("Российские сайты", "база geosite:category-ru", "direct", "direct", "geoBases"),
         ("gosuslugi.ru, банки", "свой список · 24 домена", "direct", "direct", "ruServices"),
         ("Российские IP", "база geoip:ru", "direct", "direct", "geoBases"),
         ("Заблокированное в РФ", "база runetfreedom", "proxy", "proxy", "geoBases"),
         ("Всё остальное", "финальное правило", "proxy", "proxy", "finalRule")]
rinit = {"blockQuic": True, "udpProxy": True, "fragment": False, "preset": "except_ru", "routeTestInput": ""}
ar = ""
for n, (cc, name, desc, tcp, udp) in enumerate(apps):
    rinit.update({f"at{n}": tcp, f"au{n}": udp, f"aon{n}": True})
    ar += (f'<tr><td style="width: 20px">{grip}</td><td><span style="display: flex; align-items: center; gap: 10px"><span class="cc" style="width: 32px">{cc}</span><span><span style="display: block; font-weight: 600">{name}</span><span style="display: block; font-size: 13px; color: #9AA4AE">{desc}</span></span></span></td>'
           f'<td>{rsel(f"at{n}", "TCP для " + name)}</td><td>{rsel(f"au{n}", "UDP для " + name)}</td>'
           f'<td><input type="checkbox" class="sw" aria-label="Включить правило {name}" checked="{H(f"v.aon{n}")}" onChange="{H(f"t.aon{n}")}"></td></tr>')
sr = ""
for n, (name, desc, tcp, udp, inf) in enumerate(sites):
    rinit.update({f"st{n}": tcp, f"su{n}": udp, f"son{n}": True})
    i = ib(inf) if inf else ""
    sr += (f'<tr><td style="width: 20px">{grip}</td><td><span style="display: flex; align-items: center; gap: 8px"><span><span style="display: block; font-weight: 600">{name}</span><span style="display: block; font-size: 13px; color: #9AA4AE">{desc}</span></span>{i}</span></td>'
           f'<td>{rsel(f"st{n}", "TCP для " + name)}</td><td>{rsel(f"su{n}", "UDP для " + name)}</td>'
           f'<td><input type="checkbox" class="sw" aria-label="Включить правило {name}" checked="{H(f"v.son{n}")}" onChange="{H(f"t.son{n}")}"></td></tr>')
thead = (f'<thead><tr><th></th><th>{{L}}</th><th><span style="display: inline-flex; align-items: center; gap: 6px">TCP {ib("tcpUdpSplit")}</span></th>'
         f'<th><span style="display: inline-flex; align-items: center; gap: 6px">UDP {ib("tcpUdpSplit")}</span></th><th>Вкл</th></tr></thead>')
plus = '<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M12 5v14M5 12h14"/></svg>'
rules_main = f'''<main class="main">
  <div style="display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 12px">
    <div style="display: flex; flex-direction: column"><div style="display: flex; align-items: center; gap: 10px"><h1 class="h1">Правила</h1>{ib("ruleOrder")}</div>
      <p class="sub">Проверяются сверху вниз, срабатывает первое подходящее. Порядок меняется перетаскиванием.</p></div>
    <div style="display: flex; align-items: center; gap: 8px; flex-wrap: wrap">
      <label for="preset" style="font-size: 14px; color: #9AA4AE">Основа</label>
      {sel("preset", [("except_ru", "Всё, кроме российского"), ("blocked", "Только заблокированное"), ("all", "Всё через прокси")], "width: auto; height: 40px")}{ib("route")}
      {btn("Импорт / экспорт")}{ib("ruleExport")}
    </div>
  </div>
  <section class="card" style="padding: 8px 20px">
    <div class="cols" style="display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 0 28px">
      {tog("blockQuic", "Блокировать QUIC (UDP 443)", "Браузер сразу уходит на TCP, сайты не «думают»")}
      {tog("udpProxy", "UDP через сервер", "Нужно для голоса Discord и звонков")}
      {tog("fragment", "Фрагментация прямых сайтов", "Режет имя сайта на куски, чтобы DPI его не прочитал")}
    </div>
  </section>
  <section class="card scroll" style="padding: 16px 12px 6px">
    <div style="display: flex; align-items: center; justify-content: space-between; padding: 0 8px 12px; flex-wrap: wrap; gap: 8px">
      <div style="display: flex; align-items: center; gap: 10px"><h2 style="margin: 0; font-size: 18px">Приложения</h2>{ib("appRules")}<span class="pill info">только в режиме «Весь компьютер»</span></div>
      {btn(plus + "Приложение")}
    </div>
    <table class="tbl">{thead.replace("{L}", "Приложение")}<tbody>{ar}</tbody></table>
  </section>
  <section class="card scroll" style="padding: 16px 12px 6px">
    <div style="display: flex; align-items: center; justify-content: space-between; padding: 0 8px 12px; flex-wrap: wrap; gap: 8px">
      <h2 style="margin: 0; font-size: 18px">Сайты и IP</h2>
      <div style="display: flex; align-items: center; gap: 8px; flex-wrap: wrap">
        <input type="text" aria-label="Проверить адрес" placeholder="Проверить: куда пойдёт example.com?" value="{H("v.routeTestInput")}" onChange="{H("sv.routeTestInput")}" style="width: 300px">{ib("routeTest")}
        {btn(plus + "Сайт или список")}
      </div>
    </div>
    <table class="tbl">{thead.replace("{L}", "Условие")}<tbody>{sr}</tbody></table>
  </section>
</main>'''
rb = [k for k, v in rinit.items() if isinstance(v, bool)]
rs = [k for k, v in rinit.items() if isinstance(v, str)]
app_page("Rules", "Правила", "Правила", 1280, 1180, rules_main, script(1280, 1180, rinit, "", "", rb, rs, {}))

# ================= Диагностика =================
steps = [("ok", "ОК", "Интернет без прокси", "Провайдер на связи, ya.ru открывается напрямую", None, "netDirect"),
 ("ok", "ОК", "Часы Windows", "Расхождение 1 с. Для VMess важно: больше 90 с, и подключения нет", None, "clock"),
 ("ok", "ОК", "Сервер отвечает", "TCP-рукопожатие с nl1.[домен]:443 за 38 мс", None, "tcping"),
 ("ok", "ОК", "Reality-рукопожатие", "Ключ сервера совпал, маскировка под [сайт-маска]", None, "realityHs"),
 ("ok", "ОК", "Данные идут", "Скачано 10 МБ через сервер, 86 Мбит/с, без зависаний", None, "dataFlow"),
 ("warn", "!", "Провайдер подменяет DNS", "discord.com у провайдера резолвится в заглушку. Тропа уже ходит в DNS через туннель, сайты работают", None, "dnsPoison"),
 ("ok", "ОК", "UDP через сервер", "STUN-ответ получен, голос в Discord будет работать", None, "udpTest"),
 ("ok", "ОК", "Утечки", "DNS и IPv6 не выходят мимо туннеля", None, "leaks"),
 ("bad", "×", "Другой VPN в системе", "Найден активный адаптер «[имя адаптера]». Он может перехватывать трафик раньше Тропы", "Как отключить", "otherVpn")]
rows = ""
for cls, mark, t_, d_, a, k in steps:
    bt = f'<button class="btn" style="height: 36px; font-size: 13px">{a}</button>' if a else ""
    rows += (f'<div class="check" style="padding: 14px 0; align-items: flex-start"><span class="pill {cls}" style="min-width: 28px; justify-content: center">{mark}</span>'
             f'<div style="flex: 1"><div style="font-weight: 600">{t_}</div><div style="font-size: 13px; color: #9AA4AE; margin-top: 3px">{d_}</div></div>{bt}{ib(k)}</div>')
log = '''<div class="mono" style="font-size: 12px; line-height: 1.7; color: #C9D1D8; background: #0F1215; border: 1px solid #242A31; border-radius: 10px; padding: 12px; height: 220px; overflow: auto; white-space: pre">14:31:02 INFO  sing-box запущен, TUN «Тропа» поднят
14:31:02 INFO  DNS: remote = DoH 1.1.1.1 (через сервер)
14:31:03 INFO  outbound Amsterdam-1: Reality OK
14:31:09 WARN  DNS провайдера: discord.com → 0.0.0.0
14:31:40 INFO  Discord.exe → udp 162.159.x.x:50007 → прокси
<span style="color: #FF9C9C">14:32:11 ERROR Warsaw: 16 КБ получено, затем тишина 15 с</span>
14:32:11 INFO  авто-выбор: Warsaw исключён до 15:32</div>'''
diag_main = f'''<main class="main">
  <div style="display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 12px">
    <div><h1 class="h1">Диагностика</h1><p class="sub">Проверка всей цепочки от компьютера до сайта. Каждый шаг объясняет, что не так.</p></div>
    <button class="btn" {PRIMARY}>Проверить всё заново</button>
  </div>
  <div class="cols" style="display: grid; grid-template-columns: minmax(0, 1fr) 400px; gap: 20px; align-items: start">
    <section class="card" style="padding: 8px 20px">{rows}</section>
    <div style="display: flex; flex-direction: column; gap: 20px">
      {card("Отчёт для помощи", '<p style="font-size: 14px; margin: 6px 0">Результаты проверок, версии ядер, правила и последние 200 строк журнала.</p><p style="font-size: 13px; color: #9AA4AE; margin: 0 0 14px">UUID, пароли и ключи вырезаются. Адреса серверов заменяются на «сервер-1», «сервер-2».</p><div style="display: flex; gap: 8px; flex-wrap: wrap; padding-bottom: 12px">' + btn("Скопировать") + btn("Сохранить файл") + "</div>", info="diagReport")}
      {card("Журнал", '<div style="display: flex; justify-content: flex-end; margin: -34px 0 10px"><select aria-label="Фильтр журнала" style="width: auto; height: 36px; font-size: 13px"><option>Всё</option><option>Предупреждения</option><option>Ошибки</option></select></div>' + log + '<div style="height: 12px"></div>', info="logLevel")}
    </div>
  </div>
</main>'''
app_page("Diagnostics", "Диагностика", "Диагностика", 1280, 860, diag_main, script(1280, 860, {}, "", "", [], [], {}))

# ================= Окна: общий каркас =================
def dialog_page(name, title, w, h, inner, scr):
    body = (f'<div style="width: 100%; min-height: {h}px; box-sizing: border-box; padding: 40px 16px; background: #080A0C; '
            f'font-family: \'Onest\', system-ui, sans-serif; color: #E6EAEE; display: flex; align-items: flex-start; justify-content: center">{inner}{MODAL}</div>')
    page(name, title, 0, body, scr)

def f(label, ctrl, hint=None, i="", info=None):
    h = f'<small style="display: block; color: #9AA4AE; font-size: 13px; line-height: 1.4">{hint}</small>' if hint else ""
    ii = ib(info) if info else ""
    return (f'<div style="display: flex; flex-direction: column; gap: 6px; min-width: 0"><div style="display: flex; align-items: center; gap: 8px; min-height: 26px">'
            f'<label for="{i}" style="font-size: 14px; font-weight: 500">{label}</label>{ii}</div>{ctrl}{h}</div>')

def seg(k, opts, label):
    b = "".join(f'<button class="{H("on." + k + "_" + val)}" aria-pressed="{H("ap." + k + "_" + val)}" disabled="{H("d." + k + "_" + val)}" onClick="{H("p." + k + "_" + val)}">{lbl}</button>'
                for val, lbl in opts)
    return f'<div class="seg" role="group" aria-label="{label}">{b}</div>' + _if("segWhy." + k, f'<small style="display: block; color: #F5C76A; font-size: 13px; line-height: 1.4">{H("segWhy." + k)}</small>')

close = f'<button class="btn" aria-label="Закрыть" style="width: 40px; padding: 0; justify-content: center">{CLOSE_SVG}</button>'

# ================= Добавить серверы =================
srv = [("NL", "Amsterdam-1", "VLESS · Reality", "ok", "готов"), ("DE", "Frankfurt-2", "VLESS · XHTTP", "ok", "готов, нужен Xray"),
       ("FI", "Helsinki", "Trojan · TLS", "ok", "готов"), ("SE", "Stockholm", "VMess · WS · TLS", "ok", "готов"),
       ("LV", "Riga-old", "VMess · TCP", "warn", "без шифрования транспорта"), ("US", "New York", "VLESS · gRPC", "ok", "готов")]
srows = "".join(f'<div class="check" style="padding: 10px 0"><span class="cc">{c}</span><span style="flex: 1"><b style="font-weight: 600">{n}</b><span style="display: block; font-size: 13px; color: #9AA4AE">{p}</span></span><span class="pill {s}">{t_}</span></div>' for c, n, p, s, t_ in srv)
tab_url = f'''<div style="display: flex; flex-direction: column; gap: 16px">
  {f("Адрес подписки", '<div style="display: flex; gap: 8px">' + inp("subUrl", style="flex: 1; min-width: 0") + btn("Загрузить") + "</div>", "Ссылка от того, кто выдал вам доступ. Ctrl+V в любом месте окна тоже работает", "subUrl", "subUrl")}
  <div class="cols" style="display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 16px">
    {f("Название", inp("subName", mono=False), None, "subName")}
    {f("Обновлять", sel("subUpdate", [("6h", "каждые 6 ч"), ("12h", "каждые 12 ч"), ("24h", "раз в сутки"), ("manual", "вручную")]), None, "subUpdate", "subUpdate")}
  </div>
  {f("Представляться как", sel("subUA", [("tropa", "Тропа"), ("v2rayn", "как v2rayN"), ("singbox", "как sing-box")]), "Если подписка пришла пустой, выберите «как v2rayN»", "subUA", "subUA")}
  <div style="display: flex; align-items: center; gap: 10px; font-size: 14px"><input type="checkbox" class="sw" id="subViaProxy" checked="{H("v.subViaProxy")}" onChange="{H("t.subViaProxy")}"><label for="subViaProxy" style="flex: 1">Загружать через прокси, если адрес заблокирован</label>{ib("subViaProxy")}</div>
  <div style="border: 1px solid #242A31; border-radius: 14px; padding: 14px 16px">
    <div style="display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 8px; margin-bottom: 4px">
      <b style="font-weight: 600">Найдено 6 серверов</b>
      <span style="display: flex; align-items: center; gap: 8px; font-size: 13px; color: #9AA4AE">Трафик 38 из 100 ГБ · до 12 ноября {ib("subInfo")}</span>
    </div>
    <div style="height: 6px; background: #0F1215; border-radius: 999px; margin: 8px 0 6px"><div style="width: 38%; height: 6px; background: #3DDC97; border-radius: 999px"></div></div>
    {srows}
  </div>
</div>'''
tab_links = f'''<div style="display: flex; flex-direction: column; gap: 12px">
  {f("Ссылки на серверы", area("linksText", 200), "По одной на строку: vless://, vmess://, trojan://", "linksText", "protocol")}
  <p style="margin: 0; font-size: 14px; color: #9AA4AE">Распознано: 2 сервера · 1 строка с ошибкой (vmess: неверный base64)</p>
</div>'''
tab_qr = '''<div style="display: flex; flex-direction: column; align-items: center; gap: 14px; padding: 30px 0; text-align: center">
  <svg width="56" height="56" viewBox="0 0 24 24" fill="none" stroke="#9AA4AE" stroke-width="1.6" stroke-linecap="round"><rect x="3" y="3" width="7" height="7" rx="1"/><rect x="14" y="3" width="7" height="7" rx="1"/><rect x="3" y="14" width="7" height="7" rx="1"/><path d="M14 14h3v3M21 14v7h-7"/></svg>
  <p style="margin: 0; font-size: 15px">Откройте QR-код на экране и нажмите «Сканировать экран»</p>
  <div style="display: flex; gap: 8px"><button class="btn">Сканировать экран</button><button class="btn">Из картинки…</button></div>
</div>'''
tab_file = '''<div style="display: flex; flex-direction: column; align-items: center; gap: 14px; padding: 30px 0; border: 1px dashed #39414A; border-radius: 14px; text-align: center">
  <p style="margin: 0; font-size: 15px">Перетащите файл сюда</p>
  <p style="margin: 0; font-size: 13px; color: #9AA4AE">JSON-конфиг sing-box или Xray, текстовый файл со ссылками, резервная копия Тропы</p>
  <button class="btn">Выбрать файл…</button>
</div>'''
imp = f'''<div role="dialog" aria-modal="true" aria-labelledby="dlg1" style="width: 100%; max-width: 760px; background: #171B20; border: 1px solid #2A3038; border-radius: 18px; box-shadow: 0 24px 60px rgba(0,0,0,.6)">
  <div style="display: flex; align-items: center; justify-content: space-between; padding: 20px 24px 0"><div style="display: flex; align-items: center; gap: 10px"><h2 id="dlg1" style="margin: 0; font-size: 20px">Добавить серверы</h2>{ib("importMethods")}</div>{close}</div>
  <div style="padding: 16px 24px 0">{seg("tab", [("url", "Подписка по URL"), ("links", "Вставить ссылки"), ("qr", "QR-код"), ("file", "Файл")], "Способ добавления")}</div>
  <div style="padding: 20px 24px">{_if("tabs.url", tab_url)}{_if("tabs.links", tab_links)}{_if("tabs.qr", tab_qr)}{_if("tabs.file", tab_file)}</div>
  <div style="display: flex; justify-content: flex-end; gap: 8px; padding: 16px 24px 22px; border-top: 1px solid #242A31">
    <a class="btn" href="Servers.dc.html">Отмена</a>
    <a class="btn" href="Servers.dc.html" {PRIMARY}>Добавить</a>
  </div>
</div>'''
iinit = {"tab": "url", "subUrl": "https://[адрес подписки]/sub/••••••", "subName": "[Подписка A]", "subUpdate": "12h", "subUA": "tropa",
         "subViaProxy": True, "linksText": "vless://••••@nl1.[домен]:443?security=reality…#Amsterdam-1\ntrojan://••••@fi.[домен]:443?security=tls…#Helsinki\nvmess://eyJ2Ijoi…сломано"}
iextra = """
    const tabs = { url: e.tab === 'url', links: e.tab === 'links', qr: e.tab === 'qr', file: e.tab === 'file' };
    x = { tabs, segWhy: {} };
"""
dialog_page("Import", "Добавить серверы", 1280, 1100, imp,
            script(1280, 1100, iinit, "", iextra, ["subViaProxy"], ["subUrl", "subName", "subUpdate", "subUA", "linksText"], {"tab": ["url", "links", "qr", "file"]}))

# ================= Редактирование сервера =================
two_ = lambda a, b_: f'<div style="display: grid; grid-template-columns: minmax(0, 1fr) 120px; gap: 12px">{a}{b_}</div>'
reality_block = f'''<div style="display: flex; flex-direction: column; gap: 16px">
  {f("SNI (сайт-маска)", inp("sni"), "Имя настоящего сайта, под который маскируется сервер. Его видит DPI", "sni", "sni")}
  {f("Публичный ключ (pbk)", inp("pbk"), None, "pbk", "pbk")}
  <div style="display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 12px">{f("Short ID (sid)", inp("sid"), None, "sid", "sid")}{f("Отпечаток (fp)", sel("fp", [("chrome", "chrome"), ("firefox", "firefox"), ("edge", "edge"), ("random", "random")]), None, "fp", "utls")}</div>
  {f("SpiderX (spx)", inp("spx"), None, "spx", "spx")}
</div>'''
tls_block = f'''<div style="display: flex; flex-direction: column; gap: 16px">
  {f("SNI (домен сервера)", inp("sni"), "Должен совпадать с сертификатом сервера", "sni", "sni")}
  <div style="display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 12px">{f("ALPN", inp("alpn"), None, "alpn", "alpn")}{f("Отпечаток (fp)", sel("fp", [("chrome", "chrome"), ("firefox", "firefox"), ("edge", "edge"), ("random", "random")]), None, "fp", "utls")}</div>
  <div style="display: flex; align-items: center; gap: 10px; font-size: 14px"><input type="checkbox" class="sw" id="allowInsecure" checked="{H("v.allowInsecure")}" onChange="{H("t.allowInsecure")}"><label for="allowInsecure" style="flex: 1">Не проверять сертификат</label>{ib("allowInsecure")}</div>
  {_if("v.allowInsecure", '<div style="background: #2D1719; color: #FF9C9C; border-radius: 12px; padding: 10px 14px; font-size: 13px; line-height: 1.45">Трафик может прочитать любой посредник. Сервер будет подсвечен красным.</div>')}
</div>'''
none_block = '<div style="background: #2A2314; color: #F5C76A; border-radius: 12px; padding: 12px 14px; font-size: 14px; line-height: 1.45">Транспорт не шифруется. VLESS в таком виде ничего не скрывает от провайдера, VMess легко распознаётся.</div>'
path_block = f'''<div style="display: flex; flex-direction: column; gap: 16px">
  {f(H("pathLabel"), inp("path"), None, "path", "path")}
  {_if("isXhttp", f("Режим XHTTP", sel("xhttpMode", [("auto", "auto"), ("packet-up", "packet-up"), ("stream-up", "stream-up")]), None, "xhttpMode", "xhttpMode"))}
</div>'''
edit = f'''<div role="dialog" aria-modal="true" aria-labelledby="dlg2" style="width: 100%; max-width: 1000px; background: #171B20; border: 1px solid #2A3038; border-radius: 18px; box-shadow: 0 24px 60px rgba(0,0,0,.6)">
  <div style="display: flex; align-items: center; justify-content: space-between; padding: 20px 24px 0"><h2 id="dlg2" style="margin: 0; font-size: 20px">Сервер · {H("v.name")}</h2>{close}</div>
  <div class="cols" style="display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); padding-top: 8px">
    <div style="padding: 16px 24px; display: flex; flex-direction: column; gap: 18px; border-right: 1px solid #242A31; min-width: 0">
      <p class="lbl" style="margin: 0">Основное</p>
      {f("Название", inp("name", mono=False), None, "name")}
      {f("Протокол", seg("protocol", [("vless", "VLESS"), ("vmess", "VMess"), ("trojan", "Trojan")], "Протокол"), None, "", "protocol")}
      {two_(f("Адрес", inp("addr"), None, "addr"), f("Порт", inp("port"), None, "port"))}
      {f(H("idLabel"), inp("uuid"), "Ваш личный ключ на сервере. Никому не пересылайте", "uuid", "uuid")}
      {f("Flow", sel("flow", [("vision", "xtls-rprx-vision"), ("none", "нет")]) + _if("d.flow", f'<small style="display: block; color: #F5C76A; font-size: 13px">{H("r.flow")}</small>'), None, "flow", "flow")}
      {f("Транспорт", seg("transport", [("tcp", "TCP"), ("xhttp", "XHTTP"), ("ws", "WS"), ("grpc", "gRPC")], "Транспорт"), None, "", "transport")}
      {_if("showPath", path_block)}
      <p class="lbl" style="margin: 6px 0 0">Цепочка</p>
      {f("Подключаться через", sel("chain", [("direct", "Напрямую"), ("relay", "Relay RU-VPS"), ("other", "Другой сервер…")]), None, "chain", "chain")}
    </div>
    <div style="padding: 16px 24px; display: flex; flex-direction: column; gap: 18px; min-width: 0">
      <p class="lbl" style="margin: 0">Безопасность</p>
      {f("Тип", seg("security", [("none", "Нет"), ("tls", "TLS"), ("reality", "Reality")], "Безопасность"), None, "", "security")}
      {_if("isReality", reality_block)}{_if("isTls", tls_block)}{_if("isNone", none_block)}
      <div style="display: flex; align-items: center; gap: 8px; background: #152233; color: #9CC4FF; border-radius: 12px; padding: 10px 14px; font-size: 13px; line-height: 1.45"><span style="flex: 1">Ядро: <b>{H("core")}</b></span>{ib("coreChoice")}</div>
    </div>
  </div>
  <div style="padding: 14px 24px; border-top: 1px solid #242A31">
    <div style="display: flex; align-items: center; gap: 8px; margin-bottom: 8px"><label for="elink" class="lbl" style="margin: 0">Ссылка</label>{ib("link")}</div>
    <div style="display: flex; gap: 8px"><input type="text" id="elink" class="mono" readOnly="{{{{ true }}}}" value="{H("link")}" style="flex: 1; min-width: 0; color: #9AA4AE">{btn("Копировать")}{btn("QR")}</div>
  </div>
  <div style="display: flex; justify-content: space-between; gap: 8px; padding: 16px 24px 22px; border-top: 1px solid #242A31; flex-wrap: wrap">
    {btn("Проверить")}
    <div style="display: flex; gap: 8px"><a class="btn" href="Servers.dc.html">Отмена</a><a class="btn" href="Servers.dc.html" {PRIMARY}>Сохранить</a></div>
  </div>
</div>'''
einit = {"name": "Amsterdam-1", "protocol": "vless", "transport": "tcp", "security": "reality", "flow": "vision",
         "addr": "nl1.[домен]", "port": "443", "uuid": "••••••••-••••-••••-••••-••••••••7f3a", "sni": "[сайт-маска]",
         "pbk": "Hx3k••••••••••••Qw", "sid": "a1b2c3", "fp": "chrome", "spx": "/", "alpn": "h2,http/1.1",
         "allowInsecure": False, "path": "/", "xhttpMode": "auto", "chain": "direct"}
erules = """
    const SE = e.security, PR = e.protocol, TR = e.transport;
    rule('protocol_vmess', SE === 'reality', 'VMess не работает с Reality — сначала выберите TLS');
    rule('protocol_trojan', SE === 'reality', 'в Тропе Reality только для VLESS — сначала выберите TLS');
    rule('protocol_trojan', SE === 'none', 'Trojan всегда работает поверх TLS — сначала выберите TLS');
    rule('security_reality', PR !== 'vless', 'Reality поддерживается только для VLESS');
    rule('security_reality', TR === 'ws', 'Reality не работает с WebSocket');
    rule('security_none', PR === 'trojan', 'Trojan без TLS не бывает');
    rule('transport_ws', SE === 'reality', 'WebSocket несовместим с Reality');
    rule('flow', PR !== 'vless', 'XTLS Vision есть только у VLESS', 'none');
    rule('flow', TR !== 'tcp', 'Vision работает только поверх транспорта TCP', 'none');
    rule('flow', SE === 'none', 'Vision требует TLS или Reality', 'none');
"""
eextra = """
    const LBL = { protocol_vless: 'VLESS', protocol_vmess: 'VMess', protocol_trojan: 'Trojan', transport_tcp: 'TCP', transport_xhttp: 'XHTTP',
      transport_ws: 'WS', transport_grpc: 'gRPC', security_none: 'Нет', security_tls: 'TLS', security_reality: 'Reality' };
    const OPTS = { protocol: ['vless', 'vmess', 'trojan'], transport: ['tcp', 'xhttp', 'ws', 'grpc'], security: ['none', 'tls', 'reality'] };
    const segWhy = {};
    Object.keys(OPTS).forEach((k) => {
      segWhy[k] = OPTS[k].filter((o) => why[k + '_' + o]).map((o) => '«' + LBL[k + '_' + o] + '» недоступен: ' + why[k + '_' + o]).join('. ');
    });
    const q = ['type=' + (e.transport === 'tcp' ? 'tcp' : e.transport), 'security=' + e.security];
    if (e.security === 'reality') q.push('sni=' + e.sni, 'pbk=' + e.pbk, 'sid=' + e.sid, 'fp=' + e.fp, 'spx=' + encodeURIComponent(e.spx));
    if (e.security === 'tls') q.push('sni=' + e.sni, 'alpn=' + encodeURIComponent(e.alpn), 'fp=' + e.fp);
    if (e.flow === 'vision') q.push('flow=xtls-rprx-vision');
    if (e.transport !== 'tcp') q.push((e.transport === 'grpc' ? 'serviceName=' : 'path=') + encodeURIComponent(e.path));
    const link = e.protocol === 'vmess'
      ? 'vmess://' + '(base64 JSON с теми же полями)'
      : e.protocol + '://••••@' + e.addr + ':' + e.port + '?' + q.join('&') + '#' + encodeURIComponent(e.name);
    x = { segWhy, link,
      isReality: e.security === 'reality', isTls: e.security === 'tls', isNone: e.security === 'none',
      idLabel: e.protocol === 'trojan' ? 'Пароль' : 'UUID',
      core: e.transport === 'xhttp' ? 'Xray (XHTTP есть только в Xray)' : 'sing-box (авто)',
      showPath: e.transport !== 'tcp', isXhttp: e.transport === 'xhttp',
      pathLabel: e.transport === 'grpc' ? 'Имя сервиса (serviceName)' : 'Путь (path)' };
"""
es = [k for k, v in einit.items() if isinstance(v, str) and k not in ("protocol", "transport", "security")]
dialog_page("ServerEdit", "Сервер", 1280, 1180, edit,
            script(1280, 1180, einit, erules, eextra, ["allowInsecure"], es,
                   {"protocol": ["vless", "vmess", "trojan"], "transport": ["tcp", "xhttp", "ws", "grpc"], "security": ["none", "tls", "reality"]}))

# ================= Первый запуск =================
def orc(val, title, desc):
    return (f'<button class="rcard {H("on.mode_" + val)}" aria-pressed="{H("ap.mode_" + val)}" onClick="{H("p.mode_" + val)}">'
            f'<span class="rd"></span><span><b style="font-weight: 600">{title}</b><small style="display: block; color: #9AA4AE; font-size: 13px; margin-top: 3px; line-height: 1.4">{desc}</small></span></button>')
onb = f'''<div style="width: 100%; max-width: 620px; display: flex; flex-direction: column; gap: 22px; padding-top: 10px">
  <div style="display: flex; align-items: center; gap: 12px">
    <svg width="40" height="40" viewBox="0 0 28 28" fill="none" stroke="#3DDC97" stroke-width="2.2" stroke-linecap="round"><path d="M5 23c4-1 6-4 7-9s3-8 11-9"/><circle cx="5" cy="23" r="2" fill="#3DDC97"/><circle cx="23" cy="5" r="2" fill="#3DDC97"/></svg>
    <div><h1 class="h1" style="font-size: 30px">Добро пожаловать в Тропу</h1><p class="sub">Два шага, и всё заработает.</p></div>
  </div>
  <section class="card" style="display: flex; flex-direction: column; gap: 14px">
    <div style="display: flex; align-items: center; gap: 10px"><span class="pill info">1</span><h2 style="margin: 0; font-size: 17px; flex: 1">Вставьте ссылку на подписку</h2>{ib("subUrl")}</div>
    <div style="display: flex; gap: 8px">{inp("subUrl", style="flex: 1; min-width: 0; height: 48px; font-size: 15px")}<button class="btn" style="height: 48px">Вставить</button></div>
    <p style="margin: 0; font-size: 13px; color: #9AA4AE">Её выдаёт тот, кто дал вам доступ. Ещё можно: <a href="Import.dc.html">QR-код</a>, <a href="Import.dc.html">ссылки vless://</a>, <a href="Import.dc.html">файл</a>.</p>
  </section>
  <section class="card" style="display: flex; flex-direction: column; gap: 10px">
    <div style="display: flex; align-items: center; gap: 10px"><span class="pill info">2</span><h2 style="margin: 0; font-size: 17px; flex: 1">Что пускать через Тропу</h2>{ib("mode")}</div>
    {orc("tun", "Весь компьютер (рекомендуется)", "Работает всё: браузер, Discord с голосом, игры. Windows один раз спросит права администратора")}
    {orc("proxy", "Только браузеры", "Без прав администратора. Discord-голос и игры работать не будут")}
    <div style="display: flex; align-items: center; gap: 12px; padding-top: 8px"><input type="checkbox" class="sw" id="autostart" checked="{H("v.autostart")}" onChange="{H("t.autostart")}"><label for="autostart" style="flex: 1; font-size: 15px">Запускать вместе с Windows и сразу подключаться</label>{ib("autostart")}</div>
  </section>
  <a class="btn" href="Main.dc.html" {PRIMARY.replace('style="', 'style="height: 52px; font-size: 16px; justify-content: center; ')}>Подключиться</a>
</div>'''
dialog_page("Onboarding", "Первый запуск", 1280, 900, onb,
            script(1280, 900, {"subUrl": "", "mode": "tun", "autostart": True}, "", "", ["autostart"], ["subUrl"], {"mode": ["tun", "proxy"]}))
print("pages ok")
