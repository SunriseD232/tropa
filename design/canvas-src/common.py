import json, re
from info import INFO

def H(p):
    return "{{" + p + "}}"

BASE_HELMET = open("helmet.part", encoding="utf-8").read()

EXTRA_CSS = """
a.nav,a.btn{text-decoration:none}
a.btn{color:#E6EAEE}
.kv{grid-template-columns:150px 1fr !important}
.kv dt{display:flex;align-items:center;gap:6px}
.ib{width:26px;height:26px;border-radius:50%;border:1px solid #3A424B;background:none;color:#A3ADB7;font:600 13px/1 'Onest',system-ui,sans-serif;font-style:italic;text-transform:none;letter-spacing:0;cursor:pointer;flex:none;position:relative;display:inline-flex;align-items:center;justify-content:center;vertical-align:middle;padding:0}
.ib::before{content:"";position:absolute;inset:-9px}
.ib:hover{color:#E6EAEE;border-color:#6A747E;background:#232A31}
.ib:focus-visible,.btn:focus-visible,.nav:focus-visible,.chip:focus-visible,.rcard:focus-visible{outline:2px solid #6AA9FF;outline-offset:2px}
.srow{display:flex;align-items:center;gap:14px;padding:14px 0}
.srow + .srow{border-top:1px solid #222830}
.sl{flex:1;min-width:0}
.sl label{font-size:15px;display:block}
.sl small{display:block;color:#9AA4AE;font-size:13px;margin-top:3px;line-height:1.4}
.sl small.why{color:#F5C76A}
.srow.dis .sl label{color:#7C8792}
.sc{flex:none;display:flex;align-items:center;gap:8px}
.sc select,.sc input[type=text]{width:190px;height:40px}
.srow.stack{flex-wrap:wrap}
.srow.stack .sc{order:3;flex:1 1 100%}
.srow.stack .sc input[type=text],.srow.stack .sc textarea{width:100%}
textarea{border-radius:10px;border:1px solid #2C333B;background:#0F1215;color:#E6EAEE;font-size:13px;padding:10px 12px;box-sizing:border-box;resize:vertical;font-family:'JetBrains Mono',ui-monospace,monospace}
.sw:disabled,select:disabled,input:disabled,textarea:disabled,.btn:disabled,.chip:disabled,.rcard:disabled{opacity:.4;cursor:not-allowed}
.seg button:disabled{opacity:.35;cursor:not-allowed}
.rcard{display:flex;align-items:flex-start;gap:12px;width:100%;text-align:left;padding:12px 14px;border:1px solid #2C333B;border-radius:12px;background:none;color:#E6EAEE;font-family:inherit;font-size:14px;cursor:pointer}
.rcard .rd{width:18px;height:18px;border-radius:50%;border:2px solid #5C6670;flex:none;margin-top:1px;box-sizing:border-box}
.rcard.on{border-color:#3DDC97;background:#15211C}
.rcard.on .rd{border:5px solid #3DDC97}
.ctitle{display:flex;align-items:center;gap:10px;margin:0 0 4px}
.ctitle h2{margin:0;font-size:17px}
.mdl-bg{position:fixed;inset:0;background:rgba(5,7,9,.74);display:flex;align-items:center;justify-content:center;padding:16px;z-index:50}
.mdl{width:100%;max-width:580px;max-height:calc(100vh - 32px);overflow:auto;box-sizing:border-box;background:#171B20;border:1px solid #2C333B;border-radius:18px;padding:22px 24px;box-shadow:0 24px 60px rgba(0,0,0,.6);font-family:'Onest',system-ui,sans-serif;color:#E6EAEE}
.mdl h3{font-size:12px;text-transform:uppercase;letter-spacing:.06em;color:#8E99A4;margin:18px 0 6px;font-weight:600}
.mdl p{margin:0;font-size:15px;line-height:1.55;color:#D5DBE0}
.mdpi{background:#132A20;border-radius:12px;padding:2px 14px 14px;margin-top:16px}
.mdpi h3{color:#7BE8B5}
.mconf{background:#152233;border-radius:12px;padding:2px 14px 14px;margin-top:16px}
.mconf h3{color:#9CC4FF}
.mnow{margin-top:14px;background:#2A2314;color:#F5C76A;border-radius:12px;padding:10px 14px;font-size:14px;line-height:1.45}
"""

def helmet(minh):
    return (BASE_HELMET.replace("min-height:820px", f"min-height:{minh}px")
            .replace("</style>", EXTRA_CSS + "</style>"))

def ib(k):
    assert k in INFO, k
    return f'<button class="ib" aria-label="Подробнее: {INFO[k]["t"]}" onClick="{H("i." + k)}">i</button>'

CLOSE_SVG = '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M6 6l12 12M18 6L6 18"/></svg>'

def _if(cond, inner):
    return f'<sc-if value="{H(cond)}" hint-placeholder-val="{{{{ false }}}}">{inner}</sc-if>'

MODAL = (
    _if("showInfo",
        f'<div class="mdl-bg" onClick="{H("closeInfo")}">'
        f'<div class="mdl" role="dialog" aria-modal="true" aria-labelledby="mdl-t" onClick="{H("stop")}">'
        f'<div style="display: flex; justify-content: space-between; align-items: flex-start; gap: 16px"><h2 id="mdl-t" style="margin: 0; font-size: 21px; line-height: 1.3">{H("inf.t")}</h2>'
        f'<button class="btn" aria-label="Закрыть" onClick="{H("closeInfo")}" style="width: 40px; padding: 0; justify-content: center; flex: none">{CLOSE_SVG}</button></div>'
        + _if("inf.now", f'<div class="mnow">{H("inf.now")}</div>')
        + _if("inf.what", f'<h3>Что это</h3><p>{H("inf.what")}</p>')
        + _if("inf.why", f'<h3>Зачем нужно</h3><p>{H("inf.why")}</p>')
        + _if("inf.dpi", f'<div class="mdpi"><h3>Как помогает против блокировок</h3><p>{H("inf.dpi")}</p></div>')
        + _if("inf.risk", f'<h3>Подводные камни</h3><p>{H("inf.risk")}</p>')
        + _if("inf.conf", f'<div class="mconf"><h3>Совместимость</h3><p>{H("inf.conf")}</p></div>')
        + f'<div style="margin-top: 22px; display: flex; justify-content: flex-end"><button class="btn" onClick="{H("closeInfo")}">Понятно</button></div>'
        '</div></div>'))

# ---------- Sidebar ----------
_navsrc = open("nav.part", encoding="utf-8").read()
_icons = re.findall(r'(<svg width="20".*?</svg>)', _navsrc)
BRAND = re.search(r'(<div class="brand">.*?</div>)', _navsrc, re.S).group(1)
FOOT = re.search(r'(<div class="side-foot">.*?</div>\s*</div>)', _navsrc, re.S).group(1)
NAV_ITEMS = [("Main.dc.html", "Главная"), ("Servers.dc.html", "Серверы"), ("Rules.dc.html", "Правила"),
             ("Diagnostics.dc.html", "Диагностика"), ("Settings.dc.html", "Настройки")]

def sidebar(active):
    items = "".join(
        f'<a class="nav{" on" if lbl == active else ""}" href="{href}"{" aria-current=\"page\"" if lbl == active else ""}>{_icons[k]}{lbl}</a>'
        for k, (href, lbl) in enumerate(NAV_ITEMS))
    return f'<nav class="side" aria-label="Разделы">{BRAND}{items}{FOOT}</nav>'

# ---------- Script ----------
JS_TEMPLATE = """class Component extends DCLogic {
  constructor(props) {
    super(props);
    this.state = Object.assign({ info: null }, __INIT__);
  }
  renderVals() {
    const s = this.state;
    const set = (o) => this.setState(o);
    const INFO = __INFO__;
    const e = Object.assign({}, s);
    const why = {};
    const rule = (k, cond, msg, force) => {
      if (!why[k] && cond) { why[k] = msg; if (force !== undefined) e[k] = force; }
    };
__RULES__
    const v = e, d = {}, r = {}, c = {}, t = {}, sv = {}, i = {}, p = {}, on = {}, ap = {};
    Object.keys(why).forEach((k) => { d[k] = true; r[k] = 'Недоступно: ' + why[k]; c[k] = 'dis'; });
    __BOOLS__.forEach((k) => { t[k] = () => set({ [k]: !s[k] }); });
    __SELECTS__.forEach((k) => { sv[k] = (ev) => set({ [k]: ev.target.value }); });
    const PICKS = __PICKS__;
    Object.keys(PICKS).forEach((k) => PICKS[k].forEach((val) => {
      const id = k + '_' + val;
      p[id] = () => set({ [k]: val });
      on[id] = e[k] === val ? 'on' : '';
      ap[id] = e[k] === val ? 'true' : 'false';
    }));
    Object.keys(INFO).forEach((k) => { i[k] = () => set({ info: k }); });
    const inf = s.info ? Object.assign({}, INFO[s.info], { now: why[s.info] ? 'Сейчас недоступно: ' + why[s.info] : '' }) : {};
    let x = {};
__EXTRA__
    return Object.assign({ v, d, r, c, t, sv, i, p, on, ap, inf, showInfo: !!s.info,
      closeInfo: () => set({ info: null }), stop: (ev) => ev.stopPropagation() }, x);
  }
}"""

def script(w, h, init, rules="", extra="", bools=(), selects=(), picks=None, props=None):
    js = (JS_TEMPLATE.replace("__INIT__", json.dumps(init, ensure_ascii=False))
          .replace("__INFO__", json.dumps(INFO, ensure_ascii=False))
          .replace("__RULES__", rules).replace("__EXTRA__", extra)
          .replace("__BOOLS__", json.dumps(list(bools)))
          .replace("__SELECTS__", json.dumps(list(selects), ensure_ascii=False))
          .replace("__PICKS__", json.dumps(picks or {}, ensure_ascii=False)))
    dp = dict(props or {})
    dp["$preview"] = {"width": w, "height": h}
    return f"<script type=\"text/x-dc\" data-dc-script data-props='{json.dumps(dp, ensure_ascii=False)}'>\n{js}\n</script>"

def page(name, title, minh, inner, scr):
    html = f"""<!doctype html>
<html lang="ru">
<head>
<meta charset="utf-8">
<title>{title}</title>
<script src="./support.js"></script>
</head>
<body>
<x-dc>
{helmet(minh)}{inner}
</x-dc>
{scr}
</body>
</html>
"""
    open(f"project/{name}.dc.html", "w", encoding="utf-8").write(html)

def app_page(name, title, active, w, h, main_html, scr):
    inner = f'<div class="app">{sidebar(active)}{main_html}{MODAL}</div>'
    page(name, title, h, inner, scr)

# ---------- Controls ----------
def row(k, title, desc, ctrl, info=None, stack=False):
    info = info or k
    reason = _if("d." + k, f'<small class="why">{H("r." + k)}</small>')
    d = f"<small>{desc}</small>" if desc else ""
    cls = "srow stack" if stack else "srow"
    return (f'<div class="{cls} {H("c." + k)}"><div class="sl"><label for="{k}">{title}</label>{d}{reason}</div>'
            f'<div class="sc">{ctrl}</div>{ib(info)}</div>')

def sw(k):
    return f'<input type="checkbox" class="sw" id="{k}" checked="{H("v." + k)}" disabled="{H("d." + k)}" onChange="{H("t." + k)}">'

def tog(k, title, desc, info=None):
    return row(k, title, desc, sw(k), info)

def sel(k, opts, style=""):
    o = "".join(f'<option value="{val}">{lbl}</option>' for val, lbl in opts)
    st = f' style="{style}"' if style else ""
    return f'<select id="{k}" value="{H("v." + k)}" disabled="{H("d." + k)}" onChange="{H("sv." + k)}"{st}>{o}</select>'

def inp(k, mono=True, style=""):
    m = ' class="mono"' if mono else ""
    st = f' style="{style}"' if style else ""
    return f'<input type="text" id="{k}"{m} value="{H("v." + k)}" disabled="{H("d." + k)}" onChange="{H("sv." + k)}"{st}>'

def area(k, height=80):
    return f'<textarea id="{k}" value="{H("v." + k)}" disabled="{H("d." + k)}" onChange="{H("sv." + k)}" style="height: {height}px"></textarea>'

def card(title, inner, info=None, desc=None):
    i = ib(info) if info else ""
    dsc = f'<p class="sub" style="margin: 0 0 6px; font-size: 13px">{desc}</p>' if desc else ""
    return f'<section class="card" style="padding: 18px 20px 8px"><div class="ctitle"><h2>{title}</h2>{i}</div>{dsc}{inner}</section>'

def btn(label, extra=""):
    return f'<button class="btn"{extra}>{label}</button>'

PRIMARY = 'style="background: #3DDC97; color: #0B1A13; border-color: #3DDC97; font-weight: 600"'
