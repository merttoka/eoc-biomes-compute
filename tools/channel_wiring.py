#!/usr/bin/env python3
"""Channel-wiring visualizer for DAC_params sets → Paper-ready HTML.

For a set folder (BiomeFieldConfig + 3 UmweltMapping assets + Scene_DAC_*.unity) it draws:
  left   WRITES INTO   seeder routes, injector firing dispersal + sources, species deposits,
                       metabolic heat / O2, termite mound building
  centre BIOME CHANNELS one row per channel with its config (diffuse/decay/relax/init/kernel/advect)
  gutter PDE           channel→channel couplings from the config globals
  right  READ BY       each species' UmweltMapping reads (effect + weight) and habitat band
Rows: full = a species or external writer/reader touches it · 0.6 = PDE/advection only · 0.35 = nobody.
Death is not drawn (no kernel reads enableDeath / corpse*).

Usage:
  tools/.venv/bin/python tools/channel_wiring.py "<set folder>" --out <dir>
  tools/.venv/bin/python tools/channel_wiring.py --all "<DAC_params folder>" --out <dir>   # one subdir per set
Outputs per set: header.html, legend.html, lane_*.html (one write_html each), body_shell.html
(the flex row the lanes go into), preview.html (standalone, Google Fonts).
No third-party deps.
"""
from __future__ import annotations
import argparse, glob, html, math, os, re, sys

# ---------------------------------------------------------------- Unity YAML subset
def _scalar(v: str):
    v = v.strip()
    if v == "":
        return None
    if v.startswith("{") and v.endswith("}"):
        d = {}
        for part in v[1:-1].split(","):
            if part.strip():
                k, _, val = part.partition(":")
                d[k.strip()] = _scalar(val)
        return d
    if len(v) >= 2 and v[0] == v[-1] == '"':
        return re.sub(r"\\u([0-9a-fA-F]{4})", lambda m: chr(int(m.group(1), 16)), v[1:-1])
    if len(v) >= 2 and v[0] == v[-1] == "'":
        return v[1:-1]
    for cast in (int, float):
        try:
            return cast(v)
        except ValueError:
            pass
    return v

def parse_yaml(text: str):
    lines = [l for l in text.splitlines() if l.strip() and not l.startswith(("%", "---"))]
    pos = [0]
    ind = lambda s: len(s) - len(s.lstrip(" "))
    def pmap(level):
        d = {}
        while pos[0] < len(lines):
            line = lines[pos[0]]; li = ind(line); s = line.strip()
            if li < level or s.startswith("- "):
                break
            k, _, v = s.partition(":")
            pos[0] += 1
            if v.strip() == "":
                if pos[0] < len(lines):
                    nl = lines[pos[0]]; ni = ind(nl)
                    if nl.strip().startswith("- ") and ni >= level:
                        d[k] = plist(ni)
                    elif ni > level:
                        d[k] = pmap(ni)
                    else:
                        d[k] = None
                else:
                    d[k] = None
            else:
                d[k] = _scalar(v)
        return d
    def plist(level):
        out = []
        while pos[0] < len(lines):
            line = lines[pos[0]]; s = line.strip()
            if ind(line) != level or not s.startswith("- "):
                break
            lines[pos[0]] = " " * (level + 2) + s[2:]
            out.append(pmap(level + 2))
        return out
    return pmap(ind(lines[0])) if lines else {}

def parse_asset(path):
    return parse_yaml(open(path, encoding="utf-8").read()).get("MonoBehaviour", {})

def parse_scene(path):
    docs, cur, head = [], [], None
    for line in open(path, encoding="utf-8").read().splitlines():
        m = re.match(r"^--- !u!(\d+) &(-?\d+)", line)
        if m:
            if head: docs.append((head, "\n".join(cur)))
            head, cur = (int(m.group(1)), int(m.group(2))), []
        else:
            cur.append(line)
    if head: docs.append((head, "\n".join(cur)))
    out = []
    for (cid, fid), body in docs:
        d = parse_yaml(body)
        key = next(iter(d), None)
        out.append((cid, fid, d.get(key, {}) if key else {}))
    return out

# ---------------------------------------------------------------- domain constants
CHANNELS = ["Nutrient", "Pheromone_0", "Pheromone_1", "Pheromone_2", "Oxygen", "Temperature",
            "Waste", "Permeability", "Flow_X", "Flow_Y", "Dispersal", "Humidity",
            "Humidity_Grad", "Excitability", "Substrate"]
CH = {n: i for i, n in enumerate(CHANNELS)}
EFFECT = ["chemotaxis", "speed penalty", "avoidance", "speed boost"]
BLEND = ["Additive", "MaxToward", "SetToward", "MinToward"]
SEEDSRC = ["R", "G", "B", "A", "luma"]
SPECIES = [("Physarum", "PhysarumSim", "UmweltPhysarum"), ("Boids", "BoidSim", "UmweltBoid"),
           ("Termites", "TermiteSim", "UmweltTermite")]

# ---------------------------------------------------------------- style
BG, CARD, LINE, TXT, MUTED, DIM, ACC = "#0F0F0F", "#171717", "#2C2C2C", "#F2F2F2", "#8C8C8C", "#4A4A4A", "#F2C12E"
READ, PDE = "#D6D6D6", "#6A6A6A"
MONO = "font-family:'JetBrains Mono'"
LABEL = "font-family:'Space Mono'"
DISPLAY = "font-family:'Space Grotesk';font-weight:600"

W_SRC, W_G1, W_CH, W_PDE, W_G2, W_RD = 400, 120, 600, 170, 120, 400
ROW_H, ROW_GAP = 44, 8
PITCH = ROW_H + ROW_GAP
CARD_TITLE, LINE_H, CARD_PAD, CARD_GAP = 30, 22, 14, 20
LANE_HEAD = 36 + 24
END_OFFSET = 7          # px between arrows that land on the same row from different cards

def fmt(v):
    if v is None: return "–"
    if isinstance(v, float):
        s = f"{v:.4f}".rstrip("0").rstrip(".")
        return s if s not in ("", "-0") else "0"
    return str(v)

def signed(v):
    return ("+" if v > 0 else "−" if v < 0 else "") + fmt(abs(v))

def width_for(mag, lo, hi):
    if mag <= 0: return 1.0
    t = (math.sqrt(mag) - math.sqrt(lo)) / (math.sqrt(hi) - math.sqrt(lo))
    return round(1.0 + 2.5 * max(0.0, min(1.0, t)), 2)

# ---------------------------------------------------------------- model
class Card:
    def __init__(self, title, tag, accent=False):
        self.title, self.tag, self.accent = title, tag, accent
        self.lines = []   # (text, channel|None, magnitude, negative, color, dim)
    def add(self, text, channel=None, mag=0.0, neg=False, color=ACC, dim=False):
        self.lines.append((text, channel, mag, neg, color, dim))
    @property
    def height(self):
        return CARD_PAD * 2 + CARD_TITLE + LINE_H * len(self.lines)

def build(setdir):
    name = os.path.basename(os.path.normpath(setdir))
    def one(pattern):
        hits = sorted(glob.glob(os.path.join(setdir, pattern)))
        if not hits: sys.exit(f"missing {pattern} in {setdir}")
        return hits[0]
    cfg = parse_asset(one("BiomeFieldConfig_*.asset"))
    umw, umw_file = {}, {}
    for sp, _, prefix in SPECIES:
        p = one(f"{prefix}_*.asset")
        umw[sp], umw_file[sp] = parse_asset(p), os.path.basename(p).replace(".asset", "")
    scene = parse_scene(one("Scene_DAC_*.unity"))
    by_id = {fid: body for _, fid, body in scene}
    def find(cls):
        return [b for _, _, b in scene if str(b.get("m_EditorClassIdentifier", "")).endswith(cls)]
    def sim_name(fid):
        b = by_id.get(fid, {})
        for sp, cls, _ in SPECIES:
            if str(b.get("m_EditorClassIdentifier", "")).endswith(cls): return sp
        return f"#{fid}"
    seeder = (find("TextureChannelSeeder") or [{}])[0]
    injector = (find("BiomeInjector") or [{}])[0]

    sources = []
    c = Card("Video · seeder", "TextureChannelSeeder", accent=True)
    routes = seeder.get("routes") or []
    off = [r for r in routes if not r.get("enabled")]
    for r in routes:
        if r.get("enabled"):
            c.add(f"{SEEDSRC[r['from']]} → {CHANNELS[r['channel']]} · {BLEND[r['mode']]} ×{fmt(float(r['gain']))}",
                  r["channel"], mag=float(r["gain"]))
    if off: c.add(f"off: {', '.join(SEEDSRC[r['from']] + '→' + CHANNELS[r['channel']] for r in off)}", dim=True)
    if not routes: c.add("no seeder in scene", dim=True)
    sources.append(c)

    c = Card("Neuron firing · injector", "BiomeInjector", accent=True)
    if injector.get("firingDispersalEnabled"):
        at_agents = injector.get("firingDispersalSource") == 1
        who = sim_name(injector.get("firingAgentSim", {}).get("fileID")) if at_agents else "neuron positions"
        c.add(f"firing → {CHANNELS[injector.get('dispersalChannel', 10)]} · {fmt(float(injector.get('dispersalAmount', 0)))} · r {fmt(float(injector.get('dispersalRadius', 0)))}",
              injector.get("dispersalChannel", 10), mag=float(injector.get("dispersalAmount", 0)))
        c.add(f"stamped at {who}{' agents' if at_agents else ''} · cap {injector.get('firingAgentStampCap', '')}", dim=True)
    else:
        c.add("firing dispersal off", dim=True)
    sources.append(c)

    c = Card("Injector sources", "BiomeInjector.sources")
    srcs = injector.get("sources") or []
    for s in srcs:
        if s.get("enabled"):
            c.add(f"{s['name']} → {CHANNELS[s['channel']]} · {BLEND[s['mode']]} ×{fmt(float(s['gain']))}",
                  s["channel"], mag=float(s["gain"]))
            if s.get("drive") == 1: c.add("procedural day/night sweep", dim=True)
    off = [s["name"] for s in srcs if not s.get("enabled")]
    if off: c.add(f"off: {', '.join(off)}", dim=True)
    if not srcs: c.add("no sources", dim=True)
    sources.append(c)

    for sp, _, _ in SPECIES:
        u = umw[sp]
        c = Card(f"{sp} writes", umw_file[sp])
        for w in u.get("writes") or []:
            a = float(w["amount"])
            c.add(f"deposit → {CHANNELS[w['channel']]} · {signed(a)}", w["channel"], mag=abs(a), neg=a < 0)
        heat, o2 = float(u.get("metabolicHeat", 0)), float(u.get("oxygenConsumption", 0))
        c.add(f"metabolic heat → Temperature · +{fmt(heat)}", CH["Temperature"], mag=heat, dim=heat == 0)
        c.add(f"breathes → Oxygen · −{fmt(o2)}", CH["Oxygen"], mag=o2, neg=True, dim=o2 == 0)
        if sp == "Termites":
            c.add("builds mounds → Permeability · firing-gated", CH["Permeability"], mag=0.3)
        sources.append(c)

    readers = []
    for sp, _, _ in SPECIES:
        u = umw[sp]
        c = Card(f"{sp} reads", umw_file[sp])
        for r in u.get("reads") or []:
            w = float(r["weight"])
            c.add(f"{CHANNELS[r['channel']]} · {EFFECT[r['effect']]} · {signed(w)}", r["channel"],
                  mag=abs(w), neg=w < 0, color=READ)
        lo, hi = float(u.get("preferredPermeabilityMin", 0)), float(u.get("preferredPermeabilityMax", 1))
        band_open = lo <= 0 and hi >= 1
        c.add(f"habitat · Permeability {fmt(lo)}–{fmt(hi)}{' (open, no gate)' if band_open else ' gate'}",
              None if band_open else CH["Permeability"], mag=1.0, color=READ, dim=band_open)
        readers.append(c)

    arcs = []   # (src ch | None, dst ch, label lines, magnitude)
    g = lambda k: float(cfg.get(k, 0) or 0)
    if g("temperatureToFlowStrength"):
        arcs.append((CH["Temperature"], CH["Flow_X"], [f"convection ×{fmt(g('temperatureToFlowStrength'))}", "→ Flow X · Y"], g("temperatureToFlowStrength")))
    wind = cfg.get("ambientWind") or {}
    if wind and (float(wind.get("x", 0)) or float(wind.get("y", 0))):
        arcs.append((None, CH["Flow_X"], [f"wind {fmt(float(wind['x']))}, {fmt(float(wind['y']))}"], 0.5))
    if g("wasteToNutrientRate"):
        arcs.append((CH["Waste"], CH["Nutrient"], [f"decomposes ×{fmt(g('wasteToNutrientRate'))}", f"Q10 span {fmt(g('decompositionTempSpan'))}"], g("wasteToNutrientRate") * 10))
    if g("temperatureToEvaporation"):
        arcs.append((CH["Temperature"], CH["Humidity"], [f"evaporates ×{fmt(g('temperatureToEvaporation'))}"], g("temperatureToEvaporation") * 3))
    if g("humidityGradientGain"):
        arcs.append((CH["Humidity"], CH["Humidity_Grad"], [f"|∇| gain {fmt(g('humidityGradientGain'))}"], 0.6))
    if g("temperatureToPermeability"):
        arcs.append((CH["Temperature"], CH["Permeability"], [f"softens ×{fmt(g('temperatureToPermeability'))}"], g("temperatureToPermeability") * 5))

    touched, pde_only = set(), set()
    for card in sources + readers:
        for (_, ch, _, _, _, dim) in card.lines:
            if ch is not None and not dim: touched.add(ch)
    for a in arcs:
        pde_only.update(x for x in (a[0], a[1]) if x is not None)
    for ch in cfg.get("channels") or []:
        if ch.get("advectedByFlow"): pde_only.update((CH["Flow_X"], CH["Flow_Y"]))
    pde_only -= touched

    return dict(name=name, cfg=cfg, sources=sources, readers=readers, arcs=arcs, touched=touched, pde_only=pde_only,
                assets=[os.path.basename(p) for p in sorted(glob.glob(os.path.join(setdir, "*.asset")))],
                scene=os.path.basename(one("Scene_DAC_*.unity")))

# ---------------------------------------------------------------- render
def lane_label(text):
    return (f'<div style="{LABEL};font-size:12px;letter-spacing:0.2em;color:{MUTED};line-height:16px;'
            f'height:36px;padding-top:4px;white-space:nowrap">{html.escape(text)}</div>')

def render_card(card, width):
    border = ACC if card.accent else LINE
    out = [f'<div layer-name="{html.escape(card.title)}" style="display:flex;flex-direction:column;width:{width}px;'
           f'height:{card.height}px;padding:{CARD_PAD}px 18px;background-color:{CARD};border:1px solid {border};'
           f'border-radius:4px;flex-shrink:0">',
           f'<div style="display:flex;flex-direction:row;align-items:baseline;gap:10px;height:{CARD_TITLE}px">',
           f'<div style="{DISPLAY};font-size:18px;color:{TXT};line-height:24px;white-space:nowrap">{html.escape(card.title)}</div>',
           f'<div style="{LABEL};font-size:10px;letter-spacing:0.12em;color:{ACC if card.accent else MUTED};line-height:14px;white-space:nowrap">{html.escape(card.tag)}</div>',
           '</div>']
    for (text, ch, mag, neg, color, dim) in card.lines:
        out.append(f'<div style="{MONO};font-size:12px;color:{DIM if dim else "#B8B8B8"};line-height:{LINE_H}px;height:{LINE_H}px;white-space:pre">{html.escape(text)}</div>')
    out.append("</div>")
    return "\n".join(out)

def render_row(i, ch, level):
    op = {2: "1", 1: "0.6", 0: "0.35"}[level]
    kern = "gauss" if ch.get("kernelShape") == 1 else "box"
    parts = [f"diff {fmt(float(ch.get('diffuseRate', 0)))}", f"decay {fmt(float(ch.get('decayRate', 0)))}",
             f"relax {fmt(float(ch.get('relaxRate', 0)))}", f"init {fmt(float(ch.get('initialValue', 0)))}", kern]
    if ch.get("advectedByFlow"): parts.append("⇢flow")
    if float(ch.get("flowAnisotropy", 0) or 0): parts.append(f"aniso {fmt(float(ch['flowAnisotropy']))}")
    if float(ch.get("permeabilityInfluence", 0) or 0): parts.append(f"perm {fmt(float(ch['permeabilityInfluence']))}")
    return (f'<div layer-name="{html.escape(ch["name"])}" style="display:flex;flex-direction:row;align-items:center;gap:12px;'
            f'width:{W_CH}px;height:{ROW_H}px;padding:0 16px;background-color:{CARD};border:1px solid {LINE};'
            f'border-radius:4px;flex-shrink:0;opacity:{op}">'
            f'<div style="{LABEL};font-size:10px;letter-spacing:0.1em;color:{MUTED};line-height:14px;width:22px;flex-shrink:0">{i:02d}</div>'
            f'<div style="{DISPLAY};font-size:15px;color:{TXT};line-height:20px;width:112px;flex-shrink:0;white-space:nowrap">{html.escape(ch["name"])}</div>'
            f'<div style="{MONO};font-size:11px;color:#B8B8B8;line-height:16px;white-space:pre">{html.escape(" · ".join(parts))}</div>'
            f'</div>')

def bez(x0, y0, x1, y1):
    mx = (x0 + x1) / 2
    return f"M{x0:.1f} {y0:.1f} C{mx:.1f} {y0:.1f} {mx:.1f} {y1:.1f} {x1:.1f} {y1:.1f}"

def head(x, y, color, left=False):
    b = x + 10 if left else x - 10
    return f'<path d="M{b:.1f} {y-5:.1f} L{x:.1f} {y:.1f} L{b:.1f} {y+5:.1f} Z" fill="{color}"/>'

def render(model):
    cfg, sources, readers, arcs = (model[k] for k in ("cfg", "sources", "readers", "arcs"))
    touched, pde_only = model["touched"], model["pde_only"]
    channels = cfg.get("channels") or []
    row_c = lambda i: LANE_HEAD + i * PITCH + ROW_H / 2
    ch_height = LANE_HEAD + len(channels) * PITCH - ROW_GAP
    def card_ys(cards):
        ys, y = [], LANE_HEAD
        for c in cards:
            ys.append(y); y += c.height + CARD_GAP
        return ys, y - CARD_GAP
    src_ys, src_h = card_ys(sources)
    rd_ys, rd_h = card_ys(readers)
    H = max(ch_height, src_h, rd_h)
    # spread arrows that land on the same row: one slot per card, centred
    def slot(n_cards, k):
        return (k - (n_cards - 1) / 2) * END_OFFSET

    def gutter_svg(name, width, cards, ys, lo, hi, into_rows):
        svg = [f'<svg layer-name="{name}" width="{width}" height="{H}" viewBox="0 0 {width} {H}" style="position:absolute;left:0;top:0">']
        for k, (c, y0) in enumerate(zip(cards, ys)):
            for n, (text, ch, mag, neg, color, dim) in enumerate(c.lines):
                if ch is None or dim: continue
                ly = y0 + CARD_PAD + CARD_TITLE + n * LINE_H + LINE_H / 2
                ry = row_c(ch) + slot(len(cards), k)
                w = width_for(mag, lo, hi)
                dash = ' stroke-dasharray="5 4"' if neg else ""
                if into_rows:
                    svg.append(f'<path d="{bez(0, ly, width - 10, ry)}" stroke="{color}" stroke-width="{w}" fill="none"{dash} opacity="0.9"/>')
                    svg.append(head(width, ry, color))
                else:
                    svg.append(f'<path d="{bez(0, ry, width - 10, ly)}" stroke="{color}" stroke-width="{w}" fill="none"{dash} opacity="0.85"/>')
                    svg.append(head(width, ly, color))
        svg.append("</svg>")
        return "\n".join(svg)

    g1 = gutter_svg("writes", W_G1, sources, src_ys, 0.0005, 1.0, True)
    g2 = gutter_svg("reads", W_G2, readers, rd_ys, 0.25, 2.0, False)

    pg = [f'<svg layer-name="pde" width="{W_PDE}" height="{H}" viewBox="0 0 {W_PDE} {H}" style="position:absolute;left:0;top:0">']
    labels = []
    for n, (a, b, lab, mag) in enumerate(arcs):
        yb = row_c(b) + (n % 3 - 1) * END_OFFSET
        w = width_for(mag, 0.05, 1.0)
        if a is None:
            pg.append(f'<path d="M52 {yb:.1f} L14 {yb:.1f}" stroke="{PDE}" stroke-width="{w}" fill="none"/>')
            pg.append(head(4, yb, PDE, left=True)); labels.append((yb - 7, lab)); continue
        ya = row_c(a) + (n % 3 - 1) * END_OFFSET
        bulge = 30 + 10 * (n % 3)
        pg.append(f'<path d="M4 {ya:.1f} C{bulge+24} {ya:.1f} {bulge+24} {yb:.1f} 14 {yb:.1f}" stroke="{PDE}" stroke-width="{w}" fill="none"/>')
        pg.append(head(4, yb, PDE, left=True))
        labels.append(((ya + yb) / 2 - 7 * len(lab), lab))
    pg.append("</svg>")
    # keep labels from stacking on each other
    labels.sort(key=lambda t: t[0]); last = -1e9
    for (ly, lab) in labels:
        ly = max(ly, last + 4); last = ly + 14 * len(lab)
        pg.append(f'<div style="position:absolute;left:62px;top:{ly:.0f}px;{LABEL};font-size:9px;letter-spacing:0.06em;color:#8C8C8C;line-height:14px;white-space:pre">{html.escape(chr(10).join(lab))}</div>')

    lanes = {}
    lanes["writers"] = "\n".join([f'<div layer-name="Writers" style="display:flex;flex-direction:column;gap:{CARD_GAP}px;width:{W_SRC}px;flex-shrink:0">',
                                  lane_label("WRITES INTO")] + [render_card(c, W_SRC) for c in sources] + ["</div>"])
    lanes["gutter_writes"] = f'<div layer-name="Gutter writes" style="position:relative;width:{W_G1}px;height:{H}px;flex-shrink:0">{g1}</div>'
    def level(i): return 2 if i in touched else 1 if i in pde_only else 0
    lanes["channels"] = "\n".join([f'<div layer-name="Channels" style="display:flex;flex-direction:column;gap:{ROW_GAP}px;width:{W_CH}px;flex-shrink:0">',
                                   lane_label("BIOME CHANNELS · BiomeFieldConfig")] + [render_row(i, ch, level(i)) for i, ch in enumerate(channels)] + ["</div>"])
    lanes["gutter_pde"] = f'<div layer-name="Gutter pde" style="position:relative;width:{W_PDE}px;height:{H}px;flex-shrink:0">' + "\n".join(pg) + "</div>"
    lanes["gutter_reads"] = f'<div layer-name="Gutter reads" style="position:relative;width:{W_G2}px;height:{H}px;flex-shrink:0">{g2}</div>'
    lanes["readers"] = "\n".join([f'<div layer-name="Readers" style="display:flex;flex-direction:column;gap:{CARD_GAP}px;width:{W_RD}px;flex-shrink:0">',
                                  lane_label("READ BY · UmweltMapping")] + [render_card(c, W_RD) for c in readers] + ["</div>"])
    body_shell = '<div layer-name="Wiring" style="display:flex;flex-direction:row;align-items:flex-start;width:100%"></div>'

    gv = lambda k: fmt(float(cfg.get(k, 0) or 0))
    header = "\n".join([
        '<div layer-name="Header" style="display:flex;flex-direction:column;gap:10px;width:100%">',
        f'<div style="{LABEL};font-size:14px;letter-spacing:0.2em;color:{ACC};line-height:20px">CHANNEL WIRING · DAC_PARAMS</div>',
        f'<div style="{DISPLAY};font-size:56px;letter-spacing:-0.02em;color:{TXT};line-height:64px">{html.escape(model["name"])}</div>',
        f'<div style="{MONO};font-size:14px;color:{MUTED};line-height:22px;white-space:pre">'
        + html.escape(f'{model["scene"]} · {" · ".join(model["assets"])}\n'
                      f'globals: convection {gv("temperatureToFlowStrength")} · decomposition {gv("wasteToNutrientRate")} (Q10 span {gv("decompositionTempSpan")}) · '
                      f'evaporation {gv("temperatureToEvaporation")} · ∇humidity gain {gv("humidityGradientGain")} · temp→perm {gv("temperatureToPermeability")} · '
                      f'perm baseline {gv("permeabilityOpenBaseline")}')
        + '</div>', '</div>'])

    def leg(color, text):
        return (f'<div style="display:flex;flex-direction:row;align-items:center;gap:10px;flex-shrink:0"><div style="width:28px;height:2px;background-color:{color}"></div>'
                f'<div style="{MONO};font-size:12px;color:{MUTED};line-height:16px;white-space:nowrap">{html.escape(text)}</div></div>')
    legend = "\n".join([
        f'<div layer-name="Legend" style="display:flex;flex-direction:row;align-items:center;gap:28px;width:100%;padding-top:8px;border-top:1px solid {LINE}">',
        leg(ACC, "writes into a channel · width = amount"), leg(READ, "read by a species · width = |weight|"), leg(PDE, "PDE coupling (config globals)"),
        f'<div style="{MONO};font-size:12px;color:{MUTED};line-height:16px;white-space:nowrap">dashed = negative · rows: bright = species or input touches it, mid = PDE only, dim = untouched · death not drawn</div>',
        '</div>'])
    return header, lanes, body_shell, legend, H

LANE_ORDER = ["writers", "gutter_writes", "channels", "gutter_pde", "gutter_reads", "readers"]

def emit(setdir, outdir):
    model = build(setdir)
    header, lanes, shell, legend, H = render(model)
    os.makedirs(outdir, exist_ok=True)
    open(os.path.join(outdir, "header.html"), "w", encoding="utf-8").write(header)
    open(os.path.join(outdir, "body_shell.html"), "w", encoding="utf-8").write(shell)
    for k in LANE_ORDER:
        open(os.path.join(outdir, f"lane_{k}.html"), "w", encoding="utf-8").write(lanes[k])
    open(os.path.join(outdir, "legend.html"), "w", encoding="utf-8").write(legend)
    total_w = W_SRC + W_G1 + W_CH + W_PDE + W_G2 + W_RD + 144
    body = shell.replace("></div>", ">" + "\n".join(lanes[k] for k in LANE_ORDER) + "</div>", 1)
    preview = ("<!doctype html><meta charset='utf-8'><link rel='stylesheet' href='https://fonts.googleapis.com/css2?"
               "family=Space+Grotesk:wght@600&family=Space+Mono&family=JetBrains+Mono&display=swap'>"
               f"<body style='margin:0;background:{BG}'><div style='display:flex;flex-direction:column;gap:56px;width:{total_w}px;"
               f"padding:72px;background:{BG};box-sizing:border-box'>{header}{body}{legend}</div></body>")
    open(os.path.join(outdir, "preview.html"), "w", encoding="utf-8").write(preview)
    print(f"{model['name']}: {len(model['sources'])} writer cards, {len(model['readers'])} reader cards, {len(model['arcs'])} PDE arcs, "
          f"{len(model['touched'])} touched / {len(model['pde_only'])} pde-only / {15-len(model['touched'])-len(model['pde_only'])} idle channels · "
          f"lane height {H}px · artboard width {total_w}px → {outdir}")

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("target", help="a set folder, or the DAC_params folder with --all")
    ap.add_argument("--out", required=True)
    ap.add_argument("--all", action="store_true", help="treat target as DAC_params/ and emit every NN_* set into <out>/<set>/")
    a = ap.parse_args()
    if a.all:
        sets = sorted(d for d in glob.glob(os.path.join(a.target, "[0-9][0-9]_*")) if os.path.isdir(d))
        for d in sets:
            emit(d, os.path.join(a.out, os.path.basename(d)))
    else:
        emit(a.target, a.out)

if __name__ == "__main__":
    main()
