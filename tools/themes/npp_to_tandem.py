# Builds the bundled Tandem themes (src/Tandem.Core/Themes/Bundled) from Notepad++ theme XML (PowerEditor/installer/themes).
# Every colour is taken from the XML unchanged; the token's "usage" field names the XML style it came from.
# Usage: python3 -I -X utf8 npp_to_tandem.py <npp-xml-dir> src/Tandem.Core/Themes/Bundled <report.md> [<themes.json>]
import sys, os, json, xml.etree.ElementTree as ET

SRC, OUT, REPORT = sys.argv[1], sys.argv[2], sys.argv[3]
BASE = "https://github.com/notepad-plus-plus/notepad-plus-plus/blob/master/PowerEditor/installer/themes/"

def palette(xml_name):
    root = ET.parse(os.path.join(SRC, xml_name)).getroot()
    p = {}
    for w in root.iter('WidgetStyle'):
        n = w.get('name')
        for attr, suffix in (('fgColor', 'fg'), ('bgColor', 'bg')):
            if w.get(attr):
                p[f'{n} {suffix}'] = '#' + w.get(attr).lower()
    # Syntax colours come from the C++ lexer. A theme that leaves C++ in one colour (Ruby Blue styles only the web
    # languages) falls back to the most colourful lexer that has the same style names; the keys stay "C++ …" and
    # LEXER records which one was used, so the usage field names it.
    def colours(lx):
        return {w.get('fgColor').lower() for w in lx.iter('WordsStyle') if w.get('fgColor')}
    needed = {'INSTRUCTION WORD', 'TYPE WORD', 'NUMBER', 'STRING', 'COMMENT'}
    lexers = [lx for lx in root.iter('LexerType')]
    lexer = next((lx for lx in lexers if lx.get('name') == 'cpp'), None)
    if lexer is None or len(colours(lexer)) < 3:
        full = [lx for lx in lexers if needed <= {w.get('name') for w in lx.iter('WordsStyle')}]
        if full:
            lexer = max(full, key=lambda lx: len(colours(lx)))
    if lexer is not None:
        p['LEXER'] = lexer.get('name')
        for w in lexer.iter('WordsStyle'):
            if w.get('fgColor'):
                p.setdefault(f'C++ {w.get("name")} fg', '#' + w.get('fgColor').lower())
    return p

def style_name(p, key):
    """The XML style a palette key stands for, naming the fallback lexer instead of C++ where one was used."""
    lexer = p.get('LEXER', 'cpp')
    return key if lexer == 'cpp' or not key.startswith('C++ ') else f'{lexer} {key[4:]}'

def lum(h):
    h = h.lstrip('#')[-6:]
    def ch(v):
        v = int(v, 16) / 255
        return v / 12.92 if v <= 0.03928 else ((v + 0.055) / 1.055) ** 2.4
    r, g, b = (ch(h[i:i + 2]) for i in (0, 2, 4))
    return 0.2126 * r + 0.7152 * g + 0.0722 * b

def cr(a, b):
    la, lb = sorted((lum(a), lum(b)), reverse=True)
    return (la + 0.05) / (lb + 0.05)

D_FG, D_BG = 'Default Style fg', 'Default Style bg'

def build(p):
    """Token -> (xml style, colour). Candidates are tried in order; the first that meets the token's need wins,
    else the best of them (the failure shows in the contrast report)."""
    t = {}
    def put(token, key):
        t[token] = (style_name(p, key), p[key])
    def pick(token, cands, ok, score=None):
        cands = [c for c in cands if c in p]
        for c in cands:
            if ok(p[c]):
                return put(token, c)
        best = max(cands, key=lambda c: (score or (lambda v: 0))(p[c]))
        put(token, best)
    surface = p[D_BG]
    put('surface', D_BG)
    put('field', D_BG)
    # Body text: the default foreground, or the theme's own emphasised tone when the default is below 4.5:1.
    margin = p.get('Line number margin bg', surface)
    pick('ink', [D_FG, 'C++ OPERATOR fg', 'Caret colour fg'], lambda v: min(cr(surface, v), cr(margin, v)) >= 4.5,
         lambda v: min(cr(surface, v), cr(margin, v)))
    ink = t['ink'][1]
    differs = lambda v: v != surface
    readable = lambda v: v != surface and cr(ink, v) >= 4.5
    pick('surface-raised', ['Line number margin bg', 'Fold margin bg', 'Current line background colour bg'],
         readable, lambda v: cr(ink, v) + (v != surface))
    pick('bar', ['Line number margin bg', 'Fold margin bg', 'Current line background colour bg'],
         readable, lambda v: cr(ink, v) + (v != surface))
    bar = t['bar'][1]
    pick('bar-hover', ['Current line background colour bg', 'Fold margin bg', 'Selected text colour bg'],
         lambda v: v not in (surface, bar) and cr(ink, v) >= 4.5, lambda v: cr(ink, v) + (v not in (surface, bar)))
    hover = t['bar-hover'][1]
    pick('bar-pressed', ['Selected text colour bg', 'Fold margin bg', 'Current line background colour bg'],
         lambda v: v not in (surface, bar, hover) and cr(ink, v) >= 3, lambda v: cr(ink, v) + (v not in (surface, bar, hover)))
    pick('cursor', ['Selected text colour bg', 'Current line background colour bg'], readable,
         lambda v: cr(ink, v) + (v != surface))
    pick('line', ['Indent guideline style fg', 'Edge colour fg', 'Fold margin bg'], differs, lambda v: cr(surface, v))
    pick('edge', ['Line number margin fg', 'Indent guideline style fg', 'C++ COMMENT fg', 'Edge colour fg', 'C++ OPERATOR fg'],
         lambda v: cr(surface, v) >= 3, lambda v: cr(surface, v))
    pick('ink-muted', ['Line number margin fg', 'C++ COMMENT fg', 'C++ OPERATOR fg', D_FG, 'Indent guideline style fg'],
         lambda v: v != ink and cr(surface, v) >= 4.5 and cr(t['bar'][1], v) >= 4.5,
         lambda v: min(cr(surface, v), cr(t['bar'][1], v)) if v != ink else 0)
    pick('ink-disabled', ['C++ COMMENT fg', 'Indent guideline style fg', 'Line number margin fg'],
         lambda v: cr(t['bar'][1], v) >= 2.5, lambda v: cr(t['bar'][1], v))
    # Most stylers.xml files keep the template's tab indicator #faaa3c; only a theme's own one counts.
    own_tab = ['Active tab focused indicator fg'] if p.get('Active tab focused indicator fg') not in (None, '#faaa3c') else []
    def with_on(token, on_token, cands, need_vs_surface):
        cands = [c for c in cands if c in p]
        def best_on(v):
            return max((D_BG, D_FG), key=lambda k: cr(p[k], v))
        def ok(v):
            return cr(p[best_on(v)], v) >= 4.5 and cr(surface, v) >= need_vs_surface
        chosen = next((c for c in cands if ok(p[c])), None)
        if chosen is None:
            chosen = max(cands, key=lambda c: cr(p[best_on(p[c])], p[c]))
        put(token, chosen)
        put(on_token, best_on(p[chosen]))
    with_on('accent', 'on-accent', ['C++ INSTRUCTION WORD fg', 'C++ TYPE WORD fg', 'C++ STRING fg', 'C++ NUMBER fg']
            + own_tab + ['C++ PREPROCESSOR fg'], 0)
    with_on('focus', 'on-focus', ['Brace highlight style fg', 'Caret colour fg', 'C++ STRING fg', 'C++ NUMBER fg',
                                  'C++ INSTRUCTION WORD fg'], 3)
    pick('folder', ['C++ TYPE WORD fg', 'C++ NUMBER fg', 'C++ STRING fg', 'C++ INSTRUCTION WORD fg', 'C++ PREPROCESSOR fg'],
         lambda v: cr(surface, v) >= 3 and v != t['accent'][1], lambda v: cr(surface, v))
    pick('file', ['C++ COMMENT fg', 'Line number margin fg', 'Indent guideline style fg', 'C++ OPERATOR fg'],
         lambda v: cr(surface, v) >= 3, lambda v: cr(surface, v))
    pick('syntax', ['C++ NUMBER fg', 'C++ STRING fg', 'C++ OPERATOR fg', 'C++ TYPE WORD fg'],
         lambda v: cr(t['surface-raised'][1], v) >= 4.5 and v != ink, lambda v: cr(t['surface-raised'][1], v))
    return t

ORDER = ['surface', 'surface-raised', 'bar', 'bar-hover', 'bar-pressed', 'field', 'line', 'edge', 'ink', 'ink-muted',
         'ink-disabled', 'accent', 'on-accent', 'cursor', 'focus', 'on-focus', 'folder', 'file', 'syntax']

THEMES = [
    dict(id='npp-obsidian', name='Obsidian (Notepad++)', dark='Obsidian.xml',
         author='Joni Eskelinen (Obsidian v2, 2009–2013)', license='в файле темы не указана'),
    dict(id='npp-monokai', name='Monokai (Notepad++)', dark='Monokai.xml',
         author='Copyright (c) 2008 Fabio Zendhi Nagao', license='MIT (текст разрешения в файле темы)'),
    dict(id='npp-twilight', name='Twilight (Notepad++)', dark='Twilight.xml',
         author='Copyright (c) 2008 Fabio Zendhi Nagao, 2011–2014 Renato Silva', license='MIT (текст разрешения в файле темы)'),
    dict(id='npp-bespin', name='Bespin (Notepad++)', dark='Bespin.xml',
         author='Copyright (c) 2009 Oren Farhi, Orizen Designs', license='MIT (текст разрешения в файле темы)'),
    dict(id='npp-solarized', name='Solarized (Notepad++)', dark='Solarized.xml', light='Solarized-light.xml',
         author='Paul Neubauer, по Solarized Итана Шуновера (Ethan Schoonover)', license='CC BY 3.0 Unported'),
    dict(id='npp-navajo', name='Navajo (Notepad++)', light='Navajo.xml',
         author='Paul Neubauer, по navajo.vim Р. Эдварда Ралстона (R. Edward Ralston)', license='CC BY 3.0 Unported'),
    # Dark themes added later. DansLeRuSH-Dark.xml is left out: its licence line is CC BY-NC-SA 3.0, which does not
    # fit a theme shipped inside the app. Hello Kitty.xml and khaki.xml are light.
    dict(id='npp-black-board', name='Black board (Notepad++)', dark='Black board.xml',
         author='Copyright (c) 2008 Fabio Zendhi Nagao', license='MIT (текст разрешения в файле темы)'),
    dict(id='npp-choco', name='Choco (Notepad++)', dark='Choco.xml',
         author='Copyright (c) 2008 Fabio Zendhi Nagao', license='MIT (текст разрешения в файле темы)'),
    dict(id='npp-dark-mode-default', name='Dark Mode Default (Notepad++)', dark='DarkModeDefault.xml',
         author='в файле темы не указан (основана на теме Zenburn, zenburn.xml)', license='GPL2'),
    dict(id='npp-deep-black', name='Deep Black (Notepad++)', dark='Deep Black.xml',
         author='Mariusz Kasperkiewicz (2009), по теме Port VibrantInk (tyler)',
         license='«GNU Free License» (так в файле темы; разрешено изменять и перевыпускать)'),
    dict(id='npp-hot-fudge-sundae', name='Hot Fudge Sundae (Notepad++)', dark='HotFudgeSundae.xml',
         author='Paul Neubauer (2012–2015)', license='CC BY 3.0 Unported'),
    dict(id='npp-mono-industrial', name='Mono Industrial (Notepad++)', dark='Mono Industrial.xml',
         author='Copyright (c) 2008 Fabio Zendhi Nagao', license='MIT (текст разрешения в файле темы)'),
    dict(id='npp-mossy-lawn', name='Mossy Lawn (Notepad++)', dark='MossyLawn.xml',
         author='Paul Neubauer', license='CC BY 3.0 Unported'),
    dict(id='npp-plastic-code-wrap', name='Plastic Code Wrap (Notepad++)', dark='Plastic Code Wrap.xml',
         author='Copyright (c) 2008 Fabio Zendhi Nagao', license='MIT (текст разрешения в файле темы)'),
    dict(id='npp-ruby-blue', name='Ruby Blue (Notepad++)', dark='Ruby Blue.xml',
         author='tomsolo (aka tonoslo), по теме TextMate Ruby Blue Джона У. Лонга (John W. Long)',
         license='GNU Free License (так в файле темы, версия не указана)'),
    dict(id='npp-vibrant-ink', name='Vibrant Ink (Notepad++)', dark='Vibrant Ink.xml',
         author='tyler, по теме TextMate VibrantInk Джастина Палмера (Justin Palmer)',
         license='GNU Free License (так в файле темы, версия не указана)'),
    dict(id='npp-zenburn', name='Zenburn (Notepad++)', dark='Zenburn.xml',
         author='Jani Kesänen, по Zenburn для Vim Яни Нурминена (Jani Nurminen)', license='GPL2'),
    dict(id='npp-vim-dark-blue', name='vim Dark Blue (Notepad++)', dark='vim Dark Blue.xml',
         author='в файле темы не указан', license='в файле темы не указана'),
]

# Optional 4th argument: a JSON file with a list of theme entries (the same keys as THEMES) to build instead of THEMES,
# e.g. to try a few new themes without touching the others.
if len(sys.argv) > 4:
    with open(sys.argv[4], encoding='utf-8') as f:
        THEMES = json.load(f)

PAIRS = [('ink', 'surface', 4.5), ('ink', 'surface-raised', 4.5), ('ink', 'bar', 4.5), ('ink', 'field', 4.5),
         ('ink', 'cursor', 4.5), ('ink', 'bar-hover', 4.5), ('ink-muted', 'surface', 4.5), ('ink-muted', 'bar', 4.5),
         ('on-accent', 'accent', 4.5), ('on-focus', 'focus', 4.5), ('syntax', 'surface-raised', 4.5),
         ('edge', 'surface', 3), ('focus', 'surface', 3), ('focus', 'field', 3), ('folder', 'surface', 3),
         ('file', 'surface', 3)]

os.makedirs(OUT, exist_ok=True)
rep = ['# Поставляемые темы по мотивам Notepad++ — отчёт генератора', '']
for th in THEMES:
    variants = {}
    for v in ('light', 'dark'):
        if v in th:
            variants[v] = (th[v], build(palette(th[v])))
    tokens = []
    for tok in ORDER:
        if len(variants) == 2:
            (lx, lt), (dx, dt) = variants['light'], variants['dark']
            value = {'light': lt[tok][1], 'dark': dt[tok][1]}
            usage = f'light: {lx} «{lt[tok][0]}»; dark: {dx} «{dt[tok][0]}»'
        else:
            (x, tt), = variants.values()
            value = tt[tok][1]
            usage = f'{x} «{tt[tok][0]}»'
        tokens.append({'name': tok, 'value': value, 'usage': usage})
    sources = ', '.join(BASE + x.replace(' ', '%20') for x, _ in variants.values())
    doc = {
        'name': th['name'],
        'origin': {
            'basedOn': 'По мотивам темы Notepad++ ' + ' и '.join(f'«{x[:-4]}»' for x, _ in variants.values()),
            'source': sources,
            'author': th['author'],
            'license': th['license'],
            'note': 'Цвет каждого токена взят из XML темы без изменений; поле usage называет стиль-источник. '
                    'Шрифты и размеры — встроенной темы Stone.',
        },
        'color': {'tokens': tokens},
    }
    with open(os.path.join(OUT, th['id'] + '.json'), 'w', encoding='utf-8', newline='\n') as f:
        json.dump(doc, f, ensure_ascii=False, indent=2)
        f.write('\n')
    rep.append(f'## {th["name"]} — `{th["id"]}.json`')
    rep.append('')
    rep.append('Источник: ' + sources + f'  \nАвтор: {th["author"]}; лицензия: {th["license"]}')
    rep.append('')
    for v, (x, tt) in variants.items():
        rep.append(f'**Вариант {v if len(variants) == 2 else "light и dark (один)"}** — {x}')
        rep.append('')
        rep.append('| Токен | Цвет | Стиль Notepad++ |')
        rep.append('|---|---|---|')
        for tok in ORDER:
            rep.append(f'| `{tok}` | `{tt[tok][1]}` | {tt[tok][0]} |')
        rep.append('')
        rep.append('| Пара | Контраст | Порог | |')
        rep.append('|---|---|---|---|')
        for a, b, need in PAIRS:
            c = cr(tt[a][1], tt[b][1])
            rep.append(f'| `{a}` / `{b}` | {c:.2f} | {need} | {"OK" if c >= need else "**провал**"} |')
        rep.append('')
with open(REPORT, 'w', encoding='utf-8') as f:
    f.write('\n'.join(rep) + '\n')
print('ok')
