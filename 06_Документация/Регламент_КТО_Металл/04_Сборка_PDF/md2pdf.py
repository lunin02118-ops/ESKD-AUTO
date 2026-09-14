"""Сборка PDF регламента КТО «Металл» из Markdown.

Запуск из любой папки:
    python md2pdf.py                 # все документы
    python md2pdf.py regl21 audit    # выбранные
Нужны: Microsoft Edge, Python-пакеты markdown и pymdown-extensions
    python -m pip install --user markdown pymdown-extensions
"""
import re, subprocess, sys, pathlib, html as H, markdown

EDGE = r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
NBH = "\u2011"  # non-breaking hyphen

CSS = r"""
:root { --navy:#12355b; --blue:#1f5f99; --sky:#e8f1fa; --ink:#1d232b; --muted:#5d6978; --line:#d5dde6;
        --red:#b3261e; --redbg:#fcebea; --amber:#a15c00; --amberbg:#fff4e0; --green:#1e7b45; --greenbg:#e7f5ec;
        --violet:#6a3fa0; --violetbg:#f1eaf9; }
@page { size:A4; margin:18mm 16mm 17mm 18mm;
  @top-right { content:"__RUN__"; font:7.5pt 'Segoe UI',Arial,sans-serif; color:#8a95a3; }
  @bottom-left { content:"ТОО «Троя» · КТО · __DATE__"; font:7.5pt 'Segoe UI',Arial,sans-serif; color:#8a95a3; }
  @bottom-right { content:counter(page) " / " counter(pages); font:600 8pt 'Segoe UI',Arial,sans-serif; color:var(--navy); }
}
@page :first { margin:0; @top-right{content:none} @bottom-left{content:none} @bottom-right{content:none} }
html { -webkit-print-color-adjust:exact; print-color-adjust:exact; }
body { font-family:'Segoe UI',Arial,sans-serif; font-size:10.2pt; line-height:1.55; color:var(--ink); margin:0; hyphens:auto; }

/* ---------- cover ---------- */
.cover { height:297mm; box-sizing:border-box; position:relative; break-after:page; background:#fff; }
.cover .band { background:linear-gradient(135deg,#0d2a4a 0%,#12355b 55%,#1f5f99 100%); color:#fff; padding:20mm 20mm 14mm; }
.cover .org { font-size:10.5pt; letter-spacing:.06em; text-transform:uppercase; opacity:.85; }
.cover .kicker { display:inline-block; margin-top:11mm; font-size:9pt; font-weight:700; letter-spacing:.12em; text-transform:uppercase;
                 background:rgba(255,255,255,.16); padding:3pt 9pt; border-radius:10pt; }
.cover h1 { font-size:26pt; line-height:1.12; margin:6mm 0 5mm; font-weight:700; color:#fff; }
.cover .sub { font-size:12.5pt; line-height:1.4; opacity:.92; max-width:160mm; }
.cover .meta { margin:9mm 20mm 0; border-collapse:collapse; width:auto; font-size:9.6pt; }
.cover .meta td { border:none; border-bottom:.6pt solid var(--line); padding:5pt 12pt 5pt 0; background:none !important; vertical-align:top; }
.cover .meta td.k { color:var(--muted); white-space:nowrap; width:38mm; font-weight:600; }
.cover .chips { margin:7mm 20mm 0; }
.cover .cards { margin:4mm 20mm 0; display:grid; grid-template-columns:1fr 1fr; gap:8pt; }
.cover .card { border:.6pt solid var(--line); border-top:3pt solid var(--blue); border-radius:4pt; padding:7pt 10pt; font-size:9pt; line-height:1.4; }
.cover .card b { display:block; color:var(--navy); font-size:9.6pt; margin-bottom:2pt; }
.cover .foot { position:absolute; left:20mm; right:20mm; bottom:14mm; border-top:2pt solid var(--navy); padding-top:4mm;
               font-size:9pt; color:var(--muted); display:flex; justify-content:space-between; }

/* ---------- toc ---------- */
.toc { break-after:page; }
.toc h2 { border:none; margin-top:0; }
.toc ol { list-style:none; margin:0; padding:0; }
.toc > ol > li { margin:0 0 5pt; padding:6pt 10pt; background:var(--sky); border-radius:4pt; font-weight:600; color:var(--navy); break-inside:avoid; }
.toc > ol > li > ol { margin:4pt 0 0 0; }
.toc > ol > li > ol > li { font-weight:400; color:var(--ink); font-size:9.3pt; padding:1pt 0 1pt 14pt; position:relative; }
.toc > ol > li > ol > li::before { content:"›"; position:absolute; left:3pt; color:var(--blue); }
.toc a { color:inherit; }

/* ---------- headings ---------- */
h2 { font-size:15pt; color:var(--navy); margin:20pt 0 8pt; padding-bottom:4pt; border-bottom:2pt solid var(--navy); break-after:avoid; line-height:1.25; }
h2.part { break-before:page; margin-top:0; border:none; background:linear-gradient(90deg,#12355b,#1f5f99); color:#fff;
          padding:10pt 14pt; border-radius:5pt; font-size:15.5pt; }
h3 { font-size:12pt; color:var(--blue); margin:16pt 0 6pt; break-after:avoid; line-height:1.3; }
h3::before { content:""; display:inline-block; width:4pt; height:11pt; background:var(--blue); border-radius:1pt; margin-right:6pt; vertical-align:-1pt; }
h4 { font-size:10.5pt; margin:12pt 0 4pt; break-after:avoid; }
p { margin:4pt 0 7pt; orphans:3; widows:3; }
ul, ol { margin:3pt 0 8pt 17pt; padding:0; }
li { margin:2pt 0; }
strong { color:#0b2440; }
hr { display:none; }
a { color:var(--blue); text-decoration:none; }

/* ---------- callouts ---------- */
.co { padding:7pt 11pt; border-radius:4pt; margin:7pt 0; border-left:3.5pt solid; break-inside:avoid; }
.co > strong:first-child { display:block; font-size:8.3pt; text-transform:uppercase; letter-spacing:.07em; margin-bottom:1pt; }
.co-fact { background:#f3f5f8; border-color:#8a97a8; } .co-fact > strong:first-child { color:#4b5767; }
.co-bad  { background:var(--redbg); border-color:var(--red); } .co-bad > strong:first-child { color:var(--red); }
.co-good { background:var(--greenbg); border-color:var(--green); } .co-good > strong:first-child { color:var(--green); }
.co-note { background:var(--sky); border-color:var(--blue); } .co-note > strong:first-child { color:var(--blue); }
blockquote { margin:8pt 0; padding:8pt 12pt; background:var(--sky); border-left:3.5pt solid var(--blue); border-radius:4pt; }
blockquote p { margin:2pt 0; }

/* ---------- badges ---------- */
.b { display:inline-block; font-size:.8em; font-weight:700; padding:0 4pt; border-radius:6pt; line-height:1.45; white-space:nowrap; vertical-align:.5pt; }
.bK { background:var(--redbg); color:var(--red); } .bF { background:var(--amberbg); color:var(--amber); }
.bP { background:var(--violetbg); color:var(--violet); } .bD { background:#e6eef7; color:var(--blue); }
h3 .b { font-size:.78em; vertical-align:1.5pt; }
.chip { display:inline-block; margin:0 6pt 6pt 0; padding:5pt 10pt; border-radius:4pt; font-size:9.3pt; }
.chip b { font-size:15pt; margin-right:5pt; vertical-align:-1.5pt; }

/* ---------- steps ---------- */
ul.steps { list-style:none; margin:6pt 0 10pt; padding:0; counter-reset:none; }
ul.steps > li { position:relative; margin:0 0 7pt; padding:7pt 11pt 7pt 40pt; border:.6pt solid var(--line); border-radius:5pt; background:#fbfcfe; break-inside:avoid; }
ul.steps > li .num { position:absolute; left:10pt; top:7pt; width:21pt; height:21pt; border-radius:50%; background:var(--navy); color:#fff;
                     font-weight:700; font-size:9.5pt; text-align:center; line-height:21pt; }
ul.steps > li > strong:first-of-type { display:block; color:var(--navy); margin-bottom:1pt; }

/* ---------- tables ---------- */
table { border-collapse:separate; border-spacing:0; width:100%; margin:7pt 0 12pt; font-size:8.6pt; line-height:1.38;
        border:.6pt solid var(--line); border-radius:5pt; overflow:hidden; }
thead { display:table-header-group; }
tr { break-inside:avoid; }
th { background:var(--navy); color:#fff; font-weight:600; text-align:left; padding:5pt 6pt; border:none; white-space:nowrap; }
td { padding:4pt 6pt; border:none; border-top:.6pt solid var(--line); vertical-align:top; overflow-wrap:normal; word-break:normal; hyphens:manual; }
td code { overflow-wrap:anywhere; }
tbody tr:nth-child(even) td { background:#f6f8fb; }
td:first-child { font-weight:600; color:#2a3440; }

/* ---------- code ---------- */
code { font-family:Consolas,'Cascadia Mono',monospace; font-size:.88em; background:#edf1f5; color:#14304f; padding:0 1.2pt; border-radius:2.5pt; overflow-wrap:anywhere; }
pre { background:#0f2238; color:#e3ecf5; border-radius:5pt; padding:9pt 11pt; font-size:7.6pt; line-height:1.32; white-space:pre-wrap; overflow-wrap:anywhere; margin:7pt 0 11pt; }
pre code { background:none; color:inherit; padding:0; font-size:1em; }
h2.part code { background:rgba(255,255,255,.18); color:#fff; font-size:.85em; padding:0 4pt; }
pre.tree { background:#f4f7fb; color:#1d2f44; border:.6pt solid var(--line); }
"""

CALLOUT = {
    "co-bad":  r"Последстви[ея]|Риск|КАТАСТРОФА",
    "co-good": r"Рекомендаци[яи]",
    "co-fact": r"Факт|Почему|Что теряется|Оценка|Дополнительно|Источники",
}

def badge(m):
    letter, num = m.group(1), m.group(2)
    cls = {"К": "bK", "Ф": "bF", "П": "bP", "Д": "bD"}[letter]
    return f'<span class="b {cls}">{letter}{NBH}{num}</span>'

def enhance(body):
    # callouts: paragraph that begins with a bold lead "Xxx." / "Xxx:"
    def co(m):
        lead = re.sub(r"<[^>]+>", "", m.group(1))
        for cls, pat in CALLOUT.items():
            if re.match(pat, lead):
                return f'<p class="co {cls}"><strong>{m.group(1)}</strong>'
        return m.group(0)
    body = re.sub(r"<p><strong>(.{2,160}?)</strong>", co, body)
    # step cards: <ul> whose items start with "<strong>Шаг N."
    def steps(m):
        ul = m.group(0)
        if not re.search(r"<li>\s*<strong>Шаг\s+\d", ul):
            return ul
        ul = ul.replace("<ul>", '<ul class="steps">', 1)
        return re.sub(r"<li>\s*<strong>Шаг\s+(\d+)\.\s*", r'<li><span class="num">\1</span><strong>', ul)
    body = re.sub(r"<ul>(?:(?!</ul>).)*</ul>", steps, body, flags=re.S)
    # ASCII trees / diagrams → light code block
    body = re.sub(r'<div class="highlight"><pre>(?:<span></span>)?', '<pre>', body)
    body = re.sub(r'</pre></div>', '</pre>', body)
    body = re.sub(r'<pre class="[^"]*">', '<pre>', body)
    body = re.sub(r'<pre><code class="language-text">', '<pre class="tree"><code>', body)
    # fit wide ASCII diagrams to the text width (≈ 175 mm)
    def fit(m):
        inner = H.unescape(re.sub(r"<[^>]+>", "", m.group(2)))
        longest = max((len(l) for l in inner.split("\n")), default=1)
        size = max(5.6, min(7.6, 480 / (0.6 * longest)))
        return f'<pre class="{m.group(1)}" style="font-size:{size:.2f}pt"><code>{m.group(2)}</code></pre>'
    body = re.sub(r"<pre><code>((?:(?!</code>).)*[├└│┌▼─](?:(?!</code>).)*)</code></pre>",
                  r'<pre class="tree"><code>\1</code></pre>', body, flags=re.S)
    # "ЧАСТЬ N" headings start a new page
    body = re.sub(r"<h2 id=\"([^\"]*)\">(ЧАСТЬ|ПРИЛОЖЕНИЕ)", r'<h2 class="part" id="\1">\2', body)
    body = re.sub(r'<pre class="(tree)"><code>(.*?)</code></pre>', fit, body, flags=re.S)
    # finding ids → badges (only in text, not inside tags/code)
    parts = re.split(r"(<code>.*?</code>|<pre.*?</pre>|<[^>]+>)", body, flags=re.S)
    for i, p in enumerate(parts):
        if p and not p.startswith("<"):
            parts[i] = re.sub(r"(?<![\w\u2011-])([КФПД])[-\u2011](\d{1,2})(?!\d)", badge, p)
    return "".join(parts)

def build_toc(toc_tokens, depth):
    def walk(items, level):
        out = "<ol>"
        for t in items:
            name = re.sub(r"<[^>]+>", "", t["name"])
            out += f'<li><a href="#{t["id"]}">{name}</a>'
            if level < depth and t.get("children"):
                out += walk(t["children"], level + 1)
            out += "</li>"
        return out + "</ol>"
    items = []
    for t in toc_tokens:          # skip h1 levels, start from h2
        items += t["children"] if t["level"] == 1 else [t]
    return '<section class="toc"><h2>Содержание</h2>' + walk(items, 1) + "</section>"

def cover_html(cfg, meta_md):
    rows = ""
    for key, val in re.findall(r"\*\*([^*]+?):\*\*\s*(.*?)(?=\s*\*\*[^*]+?:\*\*|\n|$)", meta_md):
        val = markdown.markdown(val.strip()).removeprefix("<p>").removesuffix("</p>")
        rows += f'<tr><td class="k">{H.escape(key)}</td><td>{val}</td></tr>'
    chips = "".join(f'<span class="chip" style="background:{bg};color:{fg}"><b>{n}</b>{H.escape(t)}</span>'
                    for n, t, bg, fg in cfg.get("chips", []))
    return (f'<section class="cover"><div class="band"><div class="org">{cfg["org"]}</div>'
            f'<div class="kicker">{cfg["kicker"]}</div><h1>{cfg["title"]}</h1><div class="sub">{cfg["sub"]}</div></div>'
            f'<table class="meta">{rows}</table>'
            + (f'<div class="chips">{chips}</div>' if chips else "")
            + ('<div class="cards">' + "".join(f'<div class="card"><b>{H.escape(a)}</b>{H.escape(b)}</div>' for a, b in cfg.get("cards", [])) + '</div>' if cfg.get("cards") else "")
            + f'<div class="foot"><span>{cfg["foot_l"]}</span><span>{cfg["foot_r"]}</span></div></section>')

def convert(md_path, pdf_path, cfg):
    src = pathlib.Path(md_path).read_text(encoding="utf-8")
    head, body_md = src.split("\n---\n", 1)
    body_md = re.sub(r"(?m)^([^\n|>\-\s].*)\n(\|)", r"\1\n\n\2", body_md)
    body_md = re.sub(r"(?m)^(>\s.*?\S)[ \t]*$", r"\1  ", body_md)
    body_md = re.sub(r"(?m)(?<=\S)\n(\*\*[^*\n]{2,90}?[\.:]\*\*)", r"\n\n\1", body_md)
    md = markdown.Markdown(extensions=["tables", "pymdownx.superfences", "sane_lists", "toc"],
                           extension_configs={"toc": {"slugify": lambda v, s: "h-" + str(abs(hash(v)))}})
    body = enhance(md.convert(body_md))
    toc = build_toc(md.toc_tokens, cfg.get("toc_depth", 2)) if cfg.get("toc", True) else ""
    css = CSS.replace("__RUN__", cfg["run"]).replace("__DATE__", cfg["foot_r"].replace(" г.", ""))
    page = (f'<!doctype html><html lang="ru"><head><meta charset="utf-8"><title>{H.escape(cfg["title_plain"])}</title>'
            f'<style>{css}</style></head><body>{cover_html(cfg, head)}{toc}{body}</body></html>')
    html_path = pathlib.Path(pdf_path).with_suffix(".html")
    html_path.write_text(page, encoding="utf-8")
    subprocess.run([EDGE, "--headless=new", "--disable-gpu", "--no-pdf-header-footer",
                    f"--print-to-pdf={pdf_path}", html_path.as_uri()],
                   check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=240)
    html_path.unlink()

DOCS = {
    "audit": dict(
        dir="02_Аудит_и_заключения",
        md="АУДИТ_РЕГЛАМЕНТА_КТО_Synology_SolidWorks_2026-09-13.md",
        pdf="АУДИТ_РЕГЛАМЕНТА_КТО_Synology_SolidWorks_2026-09-13.pdf",
        org="ТОО «Троя» · Павлодар · Конструкторско-технологический отдел",
        kicker="Аудит · только чтение",
        title="Аудит сквозного регламента КТО «Металл»",
        title_plain="Аудит регламента КТО «Металл»",
        sub="Synology NAS + SolidWorks Local-First: соответствие рекомендациям вендоров, слабые места и пути оптимизации",
        run="Аудит регламента КТО «Металл»",
        chips=[(6, "критических", "#fcebea", "#b3261e"), (10, "фактических ошибок", "#fff4e0", "#a15c00"),
               (7, "противоречий", "#f1eaf9", "#6a3fa0"), (10, "пробелов", "#e8f1fa", "#1f5f99")],
        foot_l="Основание для редакции регламента v2.0", foot_r="13 сентября 2026 г.", toc_depth=2,
        cards=[("Главный вывод", "Синхронизация Synology Drive для файлов SolidWorks не поддерживается вендором: риск порчи файлов и потери ссылок."),
               ("Что сделать за неделю", "Убрать *.swp из фильтра, выключить On-Demand, вынести папку Backup SW, включить снимки и Hyper Backup."),
               ("Целевая архитектура", "PDM Standard при лицензии Professional/Premium, иначе прямой SMB со снимками Btrfs и архивом WriteOnce."),
               ("Как читать", "Метки К — критические, Ф — фактические ошибки, П — противоречия. Ссылки РЕГ:N — строки исходного регламента.")]),
    "regl": dict(
        dir="03_Архив_версий",
        md="РЕГЛАМЕНТ_КТО_v2.0_NAS_SolidWorks_ЕСКД_2026-09-13.md",
        pdf="РЕГЛАМЕНТ_КТО_v2.0_NAS_SolidWorks_ЕСКД_2026-09-13.pdf",
        org="ТОО «Троя» · Павлодар · Конструкторско-технологический отдел",
        kicker="Корпоративный стандарт · версия 2.0 · проект",
        title="Сквозной регламент организации данных, САПР и выпуска КД направления «Металл»",
        title_plain="Регламент КТО «Металл» v2.0",
        sub="Synology NAS по SMB · единая копия данных · инструментарий ЕСКД · исполнения по ГОСТ 2.113 · выдача в производство",
        run="Регламент КТО «Металл» · v2.0",
        chips=[(6, "частей", "#e8f1fa", "#1f5f99"), (3, "чек-листа", "#e7f5ec", "#1e7b45"), (3, "приложения", "#f3f5f8", "#4b5767")],
        foot_l="Утверждает: Начальник КТО / Главный конструктор Лунин В. В.", foot_r="13 сентября 2026 г.", toc_depth=2,
        cards=[("Одна копия данных", "Модели, чертежи и эталоны живут только на NAS. Работа напрямую с диска Z: по SMB."),
               ("Инструментарий ЕСКД", "Реквизиты, материал, масса и обозначения заполняются надстройкой при сохранении."),
               ("Исполнения вместо копий", "Типоразмеры серийных изделий — конфигурации по ГОСТ 2.113 в эталоне базы."),
               ("Кому какая часть", "Сисадмин — часть 2. Начальник КТО — часть 3. Конструктор — часть 4. Технолог — часть 5.")]),
    "regl21": dict(
        dir="03_Архив_версий",
        md="РЕГЛАМЕНТ_КТО_v2.1_NAS_SolidWorks_ЕСКД_2026-09-13.md",
        pdf="РЕГЛАМЕНТ_КТО_v2.1_NAS_SolidWorks_ЕСКД_2026-09-13.pdf",
        org="ТОО «Троя» · Павлодар · Конструкторско-технологический отдел",
        kicker="Корпоративный стандарт · версия 2.1 · проект",
        title="Сквозной регламент организации данных, САПР и выпуска КД направления «Металл»",
        title_plain="Регламент КТО «Металл» v2.1",
        sub="Synology NAS по SMB без PDM · защита от порчи файлов · инструментарий ЕСКД · исполнения по ГОСТ 2.113 · выдача в производство",
        run="Регламент КТО «Металл» · v2.1",
        chips=[(6, "частей", "#e8f1fa", "#1f5f99"), (4, "чек-листа", "#e7f5ec", "#1e7b45"), (4, "слоя защиты от порчи файлов", "#fcebea", "#b3261e")],
        foot_l="Утверждает: Начальник КТО / Главный конструктор Лунин В. В.", foot_r="13 сентября 2026 г.", toc_depth=2,
        cards=[("Одна копия данных", "Модели, чертежи и эталоны живут только на NAS. Работа напрямую с диска Z: по SMB."),
               ("Защита от порчи файлов", "Провод без энергосбережения, резервные копии SolidWorks, снимки NAS, процедура спасения файла."),
               ("Скелет и независимые детали", "Основной способ для серийных изделий. Детали отвязываются по мере изменения."),
               ("Кому какая часть", "Сисадмин — часть 2. Начальник КТО — часть 3. Конструктор — часть 4. Технолог — часть 5.")]),
    "regl22": dict(
        dir="01_Действующая_редакция",
        md="РЕГЛАМЕНТ_КТО_v2.2_NAS_SolidWorks_ЕСКД_2026-09-14.md",
        pdf="РЕГЛАМЕНТ_КТО_v2.2_NAS_SolidWorks_ЕСКД_2026-09-14.pdf",
        org="ТОО «Троя» · Павлодар · Конструкторско-технологический отдел",
        kicker="Корпоративный стандарт · версия 2.2 · проект",
        title="Сквозной регламент организации данных, САПР и выпуска КД направления «Металл»",
        title_plain="Регламент КТО «Металл» v2.2",
        sub="Synology NAS по SMB без PDM · права доступа · изменения и ревизии по ГОСТ 2.503 · инструментарий ЕСКД · выдача в производство",
        run="Регламент КТО «Металл» · v2.2",
        chips=[(7, "частей", "#e8f1fa", "#1f5f99"), (5, "процедур выдачи прав", "#e7f5ec", "#1e7b45"), (2, "порядка изменений: заказ и эталон", "#fcebea", "#b3261e")],
        foot_l="Утверждает: Начальник КТО / Главный конструктор Лунин В. В.", foot_r="14 сентября 2026 г.", toc_depth=2,
        cards=[("Одна копия данных", "Модели, чертежи и эталоны живут только на NAS. Работа напрямую с диска Z: по SMB, с защитой от порчи файлов."),
               ("Права доступа", "Все читают заказы, исполнитель получает запись на свой заказ. Выдача и снятие — по процедурам §2.8."),
               ("Изменения и ревизии", "Заказ — по журналу изменений. Эталон — по извещению об изменении. Взаимозаменяемость решает, нужно ли новое обозначение."),
               ("Кому какая часть", "Сисадмин и доступ — часть 2. Начальник КТО — 3. Конструктор — 4. Технолог — 5. Изменения — 6.")]),
}

ROLE_COMMON = dict(dir="05_Инструкции_по_ролям", org="ТОО «Троя» · Павлодар · Конструкторско-технологический отдел",
                   foot_l="Утверждает: Начальник КТО / Главный конструктор Лунин В. В.", foot_r="14 сентября 2026 г.", toc=False)
ROLES = [
    ("i01", "И-01_Начальник_КТО", "Начальник КТО / Главный конструктор",
     "Маршрут заказа, доступы, приёмка, решения по изменениям эталонов, закрытие и архив",
     [("Маршрут заказа", "8 шагов от служебной записки до архива"), ("Доступы", "Процедуры А–Г в File Station"),
      ("Изменения", "Решение: изменение или новое обозначение"), ("Контроль", "Ежедневно, еженедельно, ежемесячно")]),
    ("i02", "И-02_Системный_администратор", "Системный администратор",
     "Synology NAS, права, резервирование, рабочие места КТО, веб-доступ начальника производства",
     [("Настройка NAS", "12 пунктов первичной настройки DSM"), ("Права", "Базовая таблица прав и флажки DSM"),
      ("Веб-доступ", "Группа prod_view и привилегии приложений"), ("Контроль", "11 ежемесячных проверок")]),
    ("i03", "И-03_Куратор_баз", "Куратор баз",
     "Эталоны стандартных изделий, библиотека проектирования и материалов, изменения по извещениям",
     [("Новый эталон", "Структура, имена, исполнения, история"), ("Библиотеки", "Покупные изделия и материалы"),
      ("Изменение эталона", "8 шагов по извещению об изменении"), ("Нельзя", "Править эталон без извещения")]),
    ("i04", "И-04_Инженер-конструктор_металл", "Инженер-конструктор по металлу",
     "Разработка модификаций: копия эталона, отвязка деталей, материалы, файлы для станков, изменения после выдачи",
     [("Рабочее место", "Z:, кабель, вкладка ЕСКД, 9 кнопок SWplus"), ("Разработка", "8 шагов и проверка перед сдачей"),
      ("Ошибка сохранения", "Порядок спасения файла"), ("Изменения", "Журнал изменений заказа")]),
    ("i05", "И-05_Конструктор_корпусной_мебели", "Конструктор корпусной мебели",
     "Хранение файлов bCAD в заказе, доступ, выдача в производство и изменения",
     [("Где хранить", "01_Корпус папки заказа"), ("Доступ", "Запись выдаёт Начальник КТО"),
      ("Ревизии", "_Изм0, _Изм1 в именах выданных файлов"), ("Изменения", "Журнал изменений заказа")]),
    ("i06", "И-06_Технолог_и_нормоконтроль", "Технолог и нормоконтроль",
     "Нормоконтроль, техкарты и свод заявки, выдача в производство, матрица форматов ЧПУ, указания о заделе",
     [("Нормоконтроль", "6 пунктов проверки чертежа"), ("Заявка", "Техкарты, свод, выдача в цех"),
      ("Изменения", "Задел, пересвод, замена в 04_ПРОИЗВОДСТВО"), ("ЧПУ", "Пробный импорт и утверждённые настройки")]),
    ("i07", "И-07_Мастер_участка_и_оператор_ЧПУ", "Мастер участка и оператор ЧПУ",
     "Где брать документы, как читать заявку и имена файлов, что делать при ошибке и извещении",
     [("Главное правило", "Только 04_ПРОИЗВОДСТВО и последняя ревизия"), ("Имена файлов", "_ИзмN, толщина, марка, длина"),
      ("Ошибка", "Остановить, сообщить, ждать исправления"), ("Нельзя", "Файлы из почты и _Аннулировано")]),
    ("i08", "И-08_ОМТС_и_склад", "ОМТС и склад",
     "Закупка и списание по заявке на производство, ревизии заявки, корректировка по извещениям",
     [("Заявка", "Листы 3, 4, 5"), ("Работа", "Копия заявки у себя, файл на Z: не менять"),
      ("Ревизии", "Действует заявка с наибольшим _ИзмN"), ("Изменения", "Сравнить листы 4 и 5, учесть задел")]),
    ("i09", "И-09_Начальник_производства", "Начальник производства",
     "Просмотр эталонов стандартных изделий и выданных заявок через браузер, только чтение",
     [("Вход", "https://nas-kto:5001 → File Station"), ("Что видно", "02_БАЗА и 04_ПРОИЗВОДСТВО"),
      ("Действующий документ", "Наибольший _ИзмN; _Аннулировано — нет"), ("Замечания", "Начальнику КТО и технологу")]),
]
for key, stem, role, sub, cards in ROLES:
    DOCS[key] = dict(ROLE_COMMON, md=stem + ".md", pdf=stem + ".pdf", kicker="Инструкция по роли · к регламенту v2.2",
                     title=role, title_plain=stem.replace("_", " "), sub=sub, run=stem.replace("_", " "), cards=cards)

DOCS["sa_task"] = dict(ROLE_COMMON, md="Задание_сисадмину_права_КТО.md", pdf="Задание_сисадмину_права_КТО.pdf",
    kicker="Задание системному администратору", title="Права КТО на Synology",
    title_plain="Задание сисадмину: права КТО", sub="Начальник КТО сам выдаёт права на папки отдела — без полных прав администратора NAS",
    run="Задание сисадмину: права КТО",
    cards=[("Сделать", "7 пунктов в DSM, около 30 минут"), ("Не делать", "Не включать в administrators"),
           ("Проверить", "Тестовая подпапка и «Инспектор разрешений»"), ("Ответить", "Таблица из 10 вопросов")])

DOCS["audit22"] = dict(
    dir="02_Аудит_и_заключения",
    md="АУДИТ_РЕГЛАМЕНТА_v2.2_И_ИНСТРУКЦИЙ_2026-09-14.md", pdf="АУДИТ_РЕГЛАМЕНТА_v2.2_И_ИНСТРУКЦИЙ_2026-09-14.pdf",
    org="ТОО «Троя» · Павлодар · Конструкторско-технологический отдел",
    kicker="Аудит · только чтение", title="Аудит регламента v2.2 и инструкций по ролям",
    title_plain="Аудит регламента v2.2 и инструкций",
    sub="Качество, слабые места, расхождения с инструментарием, оптимизация без PDM",
    run="Аудит регламента v2.2 и инструкций",
    chips=[(8, "расхождений с кодом", "#fcebea", "#b3261e"), (14, "внутренних противоречий", "#fff4e0", "#a15c00"),
           (9, "отсутствующих артефактов", "#f1eaf9", "#6a3fa0"), (13, "предложений", "#e7f5ec", "#1e7b45")],
    foot_l="Основание для редакции 2.3", foot_r="14 сентября 2026 г.", toc_depth=1,
    cards=[("Вердикт", "Подписывать v2.2 нельзя: §2.3 отстал от кода на день, часть 6 — бумажная PDM, 9 артефактов не существуют."),
           ("Сильное", "Дерево папок, два признака классификации, четыре проверки приёмки, заимствование эталона, архив комплектом."),
           ("Без PDM", "Файлы: _ИзмN, _Аннулировано, _Версии, Pack and Go. Процесс: задачи Битрикс24 вместо пяти журналов Excel."),
           ("Как читать", "Р — расхождения с кодом, В — противоречия. Ссылки РЕГ:N — строки регламента v2.2.")])

DOCS["concept"] = dict(
    dir="02_Аудит_и_заключения",
    md="КОНЦЕПЦИЯ_УПРОЩЕНИЯ_И_АВТОМАТИЗАЦИИ_2026-09-14.md", pdf="КОНЦЕПЦИЯ_УПРОЩЕНИЯ_И_АВТОМАТИЗАЦИИ_2026-09-14.pdf",
    org="ТОО «Троя» · Павлодар · Конструкторско-технологический отдел",
    kicker="Концепция · к решению владельца", title="Упрощение регламента и автоматизация",
    title_plain="Концепция упрощения и автоматизации",
    sub="Три системы, восемь кнопок вместо журналов, ручных действий у Начальника КТО — 4 вместо 28",
    run="Концепция упрощения и автоматизации",
    chips=[(11, "автоматизаций", "#e7f5ec", "#1e7b45"), (9, "документов убираем", "#fcebea", "#b3261e"),
           (3, "документа вместо 11", "#e8f1fa", "#1f5f99"), (5, "решений владельца", "#fff4e0", "#a15c00")],
    foot_l="Основание: аудит v2.2 от 14.09.2026", foot_r="14 сентября 2026 г.", toc_depth=1,
    cards=[("Принцип", "Правило есть, если его выполняет кнопка. Ручными остаются только решения."),
           ("Где что живёт", "Файлы — NAS. Процесс — Битрикс24. Работа конструктора — вкладка ЕСКД."),
           ("Первая неделя", "А1–А4: профиль настроек, отвязка с чертежом, экспорт для производства, проверка изделия."),
           ("Решить", "Права по умолчанию, часть 6 в Битрикс24, разморозка кода надстройки.")])

DOCS["addin"] = dict(
    dir="02_Аудит_и_заключения",
    md="ОПИСАНИЕ_РАБОТ_ПО_НАДСТРОЙКЕ_ЕСКД_2026-09-14.md", pdf="ОПИСАНИЕ_РАБОТ_ПО_НАДСТРОЙКЕ_ЕСКД_2026-09-14.pdf",
    org="ТОО «Троя» · Павлодар · Конструкторско-технологический отдел",
    kicker="Описание работ · к решению владельца", title="Разморозка кода надстройки ЕСКД",
    title_plain="Описание работ по надстройке ЕСКД",
    sub="Восемь кнопок вкладки ЕСКД: что делает каждая, что меняется в коде, что не меняется, риски",
    run="Описание работ по надстройке ЕСКД",
    chips=[(8, "новых кнопок", "#e7f5ec", "#1e7b45"), (119, "сценариев остаются зелёными", "#e8f1fa", "#1f5f99"),
           (2, "решения по словарю", "#fff4e0", "#a15c00")],
    foot_l="Основание: концепция упрощения от 14.09.2026", foot_r="14 сентября 2026 г.", toc_depth=1,
    cards=[("Состояние кода", "План согласования с SWPlus выполнен, метка v6.2-swplus-rc2. Разморозка = следующий этап поверх него."),
           ("Что не меняется", "Единственная точка записи свойств, имена только из словаря, правило «как MProp»."),
           ("Первая неделя", "К-1 отвязка с чертежом, К-2 экспорт для производства, К-3 проверка изделия."),
           ("Решить", "Свойство «Покрытие» и поля таблицы изменений в словаре SWplus.")])

if __name__ == "__main__":
    base = pathlib.Path(__file__).resolve().parent.parent   # папка Регламент_КТО_Металл
    keys = sys.argv[1:] or (["regl22", "regl21", "audit", "regl"] + [r[0] for r in ROLES])
    for key in keys:
        cfg = DOCS[key]
        folder = base / cfg["dir"]
        convert(folder / cfg["md"], folder / cfg["pdf"], cfg)
        print("built", cfg["dir"], "/", cfg["pdf"])
