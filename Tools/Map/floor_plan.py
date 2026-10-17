"""ECHO facility floor plan (top-down) from SampleScene -> Docs/Map/ECHO_floor_plan_v3.png.

1. Unity: ECHO > Map > Export Floor Plan Data (SampleScene)   -> Temp/Map/floor_plan_data.json
2. python Tools/Map/floor_plan.py [data.json] [out.png]          (needs Pillow)

Geometry is painted bottom level first, so an upper floor hides what is under it (the Locked Lab runs under
Containment: its hidden part is outlined with dashes). Ceilings, roofs, overhead beams/pipes and light fixtures are skipped.
"""
import json, math, os, sys
from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DATA = sys.argv[1] if len(sys.argv) > 1 else os.path.join(ROOT, "Temp", "Map", "floor_plan_data.json")
OUT = sys.argv[2] if len(sys.argv) > 2 else os.path.join(ROOT, "Docs", "Map", "ECHO_floor_plan_v5.png")
TERRITORY_VIEW = "v4" not in os.path.basename(OUT)      # v5+: proposed agent posts, territories, Mimic voice range

# ------------------------------------------------------------------------------------------------ layout
W, H = 2812, 2312
SS = 2                                    # supersampling for the geometry layer
PX = 22.0                                 # pixels per metre (final)
X0, Z1 = 17.0, 97.0                       # world x at the left edge, world z at the top edge (north = +Z up)
LEFT, TOP = 70, 120
LEGEND_X = 2240

BG = (13, 16, 21)
GRID = (24, 29, 36)
LEVELS = [0, -6, -12, -24, -30]
LEVEL_COL = {0: (164, 178, 188), -6: (135, 151, 162), -12: (104, 121, 133), -24: (47, 74, 89), -30: (40, 52, 76)}
WALL = (9, 11, 14)
PROP, PROP_EDGE = (88, 95, 103), (52, 57, 63)
STAIR, STAIR_EDGE = (190, 172, 108), (120, 104, 58)
CATWALK = (120, 86, 46, 120)
DOOR, FRAME = (236, 186, 58), (70, 66, 52)
SEAL = (255, 61, 127)
LOCKED = (255, 138, 40)
LIGHT = {"cool": (240, 244, 225), "red": (240, 50, 50), "green": (90, 230, 90)}
DECAL = (205, 120, 60)
SPAWN = (40, 210, 255)
TEXT, SUB = (240, 242, 245), (170, 178, 188)
AGENTS = [("Listener", "L", (50, 31), -6, (255, 150, 40)), ("Ambusher", "A", (75, 26), -12, (170, 90, 230)),
          ("Tracker", "T", (54, 78), -24, (235, 60, 60)), ("Mimic", "M", (102, 22), -12, (190, 230, 100))]
# Proposed layout (v5): one territory per agent along the objective route. The Mimic moves out of the locked Lab
# (it could never leave while the door is locked) to the Lab-stairs corner between the Warehouse and the Key room.
PROPOSED_POSTS = {"Mimic": ((93, 68), -24)}
TERRITORIES = [   # agent, colour, zone boxes (x0, x1, z0, z1) from FacilitySandboxBuilder, floor levels it walks
    ("Listener", (255, 150, 40), [(32, 58, 21, 40.5), (33, 46, 40.5, 53), (57, 69, 27, 35)], [-6, -12]),
    ("Ambusher", (170, 90, 230), [(69, 81, 21, 40), (74, 87, 9, 22), (87, 93, 9, 17)], [-12]),
    ("Tracker", (235, 60, 60), [(44, 88, 62, 94), (74, 82, 41.5, 62)], [-12, -24]),
    ("Mimic", (190, 230, 100), [(87.3, 100, 44, 76), (86, 107, 20, 44)], [-24, -30]),
]
AMBUSH_SPOT = (84.5, 14.0)        # where the Ambusher waits once it has learned the Tee -> Corner route
MIMIC_VOICE = 18.0

# Main objective (game design, Docs/Map/Edit.png): get the key (Key room) + switch on the Lab door power (warehouse
# fusebox), open the locked Lab, take the medicine. Positions not in the scene yet are design spots (world x, z).
KEY_POS = (88.8, 22.3)            # Key room (hospital wing, -30 m), south-west corner
MEDICINE_POS = (109.3, 7.6)       # Lab (Room_2, -12 m), east wall
GOAL = (255, 214, 64)
MED = (70, 220, 120)

FONTS = "C:/Windows/Fonts"
def font(bold, size):
    for name in (("segoeuib.ttf", "arialbd.ttf") if bold else ("segoeui.ttf", "arial.ttf")):
        try: return ImageFont.truetype(os.path.join(FONTS, name), size)
        except OSError: pass
    return ImageFont.load_default()

def to_px(x, z, s=1):
    return ((LEFT + (x - X0) * PX) * s, (TOP + (Z1 - z) * PX) * s)

def level_of(y):
    return min(LEVELS, key=lambda L: abs(L - y))

# ------------------------------------------------------------------------------------------------ classification
SKIP = ("ceiling", "/roof/", "beams", "pipes", "piipes", "light_bulb", "lightbulb", "light bulb", "light_fixture",
        "light fixture", "wall light", "walllight", "lamp", "window_glass", "muntin", "security camera")

def classify(path):
    n = path.lower(); leaf = n.split("/")[-1]
    if n.startswith(("horror_lighting", "playercapsule", "interactionhud")): return None
    if any(k in n for k in SKIP): return None
    if "sealed" in n or "closed" in n: return "seal"
    if "incline floor" in n: return "ramp"
    own = "/".join(n.split("/")[-2:]).replace("locked_lab/stairs", "")     # the lab's "Stairs" folder holds whole corridors
    if "steps" in own or "stair" in own: return "stairs"
    if "catwalk" in n: return "catwalk"
    if "door" in n:
        if "bulkheadpanels" in n: return "locked"
        if "wall" in leaf: return "wall"
        if "frame" in leaf: return "frame"
        return "door"
    if "tunnel walls" in n or "tunnel incline walls" in n: return "wall"
    if "floor" in leaf: return "floor"
    if any(k in n for k in ("wall", "perimeter", "partition", "support")): return "wall"
    return "prop"

KIND_ORDER = {"floor": 0, "ramp": 1, "stairs": 2, "prop": 3, "catwalk": 4, "wall": 5, "frame": 6, "door": 7, "seal": 8, "locked": 9}

def base_level(kind, r):
    if kind in ("floor", "ramp"): return level_of(r["y1"])
    if kind == "catwalk": return -24 if r["y0"] < -6 else level_of(r["y0"])
    return level_of(r["y0"] + 0.3)

# ------------------------------------------------------------------------------------------------ drawing helpers
def tri_pts(t, s):
    return [to_px(t[0], t[1], s), to_px(t[2], t[3], s), to_px(t[4], t[5], s)]

def rect_pts(r, s):
    a = to_px(r["x0"], r["z1"], s); b = to_px(r["x1"], r["z0"], s)
    return [a, b]

def ramp_downhill(r):
    """Downhill direction from the up-facing triangles (least squares of y over x, z)."""
    pts = [((t[0] + t[2] + t[4]) / 3, (t[1] + t[3] + t[5]) / 3, t[7]) for t in r.get("t", []) if t[6]]
    if len(pts) < 3: return None
    mx = sum(p[0] for p in pts) / len(pts); mz = sum(p[1] for p in pts) / len(pts); my = sum(p[2] for p in pts) / len(pts)
    sxx = sum((p[0] - mx) ** 2 for p in pts); szz = sum((p[1] - mz) ** 2 for p in pts)
    sxy = sum((p[0] - mx) * (p[2] - my) for p in pts); szy = sum((p[1] - mz) * (p[2] - my) for p in pts)
    gx = sxy / sxx if sxx > 1e-6 else 0.0; gz = szy / szz if szz > 1e-6 else 0.0
    # pick the dominant axis (ramps here run along x or z)
    return (-math.copysign(1, gx), 0.0) if abs(gx) > abs(gz) else (0.0, -math.copysign(1, gz))

def dashed_rect(d, a, b, col, w, dash=14, gap=9):
    (x0, y0), (x1, y1) = a, b
    for (p, q) in (((x0, y0), (x1, y0)), ((x1, y0), (x1, y1)), ((x1, y1), (x0, y1)), ((x0, y1), (x0, y0))):
        L = math.dist(p, q); n = int(L // (dash + gap)) + 1
        for i in range(n):
            t0 = i * (dash + gap) / L; t1 = min(1.0, (i * (dash + gap) + dash) / L)
            if t0 >= 1: break
            d.line([(p[0] + (q[0] - p[0]) * t0, p[1] + (q[1] - p[1]) * t0),
                    (p[0] + (q[0] - p[0]) * t1, p[1] + (q[1] - p[1]) * t1)], fill=col, width=w)

def dashed_line(d, p, q, col, w, dash, gap):
    L = math.dist(p, q); n = int(L // (dash + gap)) + 1
    for i in range(n):
        t0 = i * (dash + gap) / L; t1 = min(1.0, (i * (dash + gap) + dash) / L)
        if t0 >= 1: break
        d.line([(p[0] + (q[0] - p[0]) * t0, p[1] + (q[1] - p[1]) * t0),
                (p[0] + (q[0] - p[0]) * t1, p[1] + (q[1] - p[1]) * t1)], fill=col, width=w)

def draw_territories(img, covered, s):
    """Tint each agent's territory, clipped to the floor it walks that is visible from above (not under a higher floor)."""
    def visible(levels):
        m = Image.new("L", (W, H), 0)
        for L in levels:
            v = covered[L]
            for up in LEVELS:
                if up > L + 1: v = ImageChops.subtract(v, covered[up])
            m = ImageChops.lighter(m, v)
        return m
    for name, col, boxes, levels in TERRITORIES:
        rect = Image.new("L", (W, H), 0); rd = ImageDraw.Draw(rect)
        for x0, x1, z0, z1 in boxes:
            rd.rectangle([*to_px(x0, z1), *to_px(x1, z0)], fill=255)
        m = ImageChops.multiply(rect, visible(levels))
        layer = Image.new("RGBA", (W, H), col + (0,))
        layer.putalpha(m.point(lambda v: 58 if v > 128 else 0))
        edge = m.filter(ImageFilter.FIND_EDGES).filter(ImageFilter.MaxFilter(3)).point(lambda v: 210 if v > 60 else 0)
        rim = Image.new("RGBA", (W, H), col + (0,)); rim.putalpha(edge)
        layer = Image.alpha_composite(layer, rim)
        img = Image.alpha_composite(img, layer.resize(img.size, Image.NEAREST))
    return img, ImageDraw.Draw(img)

# ------------------------------------------------------------------------------------------------ main
def main():
    data = json.load(open(DATA, encoding="utf-8"))
    items = []
    for r in data["renderers"]:
        k = classify(r["p"])
        if k is None: continue
        if k == "prop" and (r["x1"] - r["x0"]) * (r["z1"] - r["z0"]) < 0.04: continue
        items.append((base_level(k, r), KIND_ORDER[k], k, r))
    items.sort(key=lambda it: (it[0], it[1]))

    s = SS
    img = Image.new("RGBA", (W * s, H * s), BG + (255,))
    d = ImageDraw.Draw(img)
    # grid every 5 m
    for gx in range(int(X0), int(X0 + (LEGEND_X - LEFT - 60) / PX) + 1, 5):
        x, _ = to_px(gx, 0, s); d.line([(x, TOP * s), (x, (H - 140) * s)], fill=GRID, width=s)
    for gz in range(int(Z1), int(Z1 - (H - 140 - TOP) / PX) - 1, -5):
        _, y = to_px(0, gz, s); d.line([(LEFT * s, y), ((LEGEND_X - 60) * s, y)], fill=GRID, width=s)

    covered = {L: Image.new("L", (W, H), 0) for L in LEVELS}    # floor coverage per level (final resolution)
    cov_d = {L: ImageDraw.Draw(covered[L]) for L in LEVELS}
    ramps = []
    stats = {"props": 0}
    current = None
    overlay = None
    def flush_overlay():
        nonlocal overlay, img, d
        if overlay is not None:
            img = Image.alpha_composite(img, overlay); d = ImageDraw.Draw(img); overlay = None
    for lvl, _o, kind, r in items:
        if lvl != current:
            flush_overlay(); current = lvl
        tris = r.get("t")
        if kind in ("floor", "ramp"):
            col = LEVEL_COL[lvl]
            if kind == "ramp": col = tuple(int(c * 0.92 + 12) for c in col)
            for t in (tris or []):
                if t[6]:
                    d.polygon(tri_pts(t, s), fill=col)
                    cov_d[lvl].polygon(tri_pts(t, 1), fill=255)
            if not tris:
                d.rectangle(rect_pts(r, s), fill=col); cov_d[lvl].rectangle(rect_pts(r, 1), fill=255)
            if kind == "ramp": ramps.append((lvl, r))
        elif kind == "stairs":
            for t in (tris or []):
                if t[6]: d.polygon(tri_pts(t, s), fill=STAIR, outline=STAIR_EDGE)
        elif kind == "catwalk":
            if overlay is None: overlay = Image.new("RGBA", img.size, (0, 0, 0, 0))
            od = ImageDraw.Draw(overlay)
            ups = [t for t in (tris or []) if t[6]]
            for t in ups: od.polygon(tri_pts(t, s), fill=CATWALK, outline=(150, 110, 60, 160))
            if not ups: od.rectangle(rect_pts(r, s), fill=CATWALK)
        elif kind == "prop":
            stats["props"] += 1
            d.rectangle(rect_pts(r, s), fill=PROP, outline=PROP_EDGE, width=s)
        else:
            col = {"wall": WALL, "frame": FRAME, "door": DOOR, "seal": SEAL, "locked": LOCKED}[kind]
            if tris:
                # only the part of a wall that stands on the floor: lintels over corridor openings would close them
                low = [t for t in tris if t[7] < r["y0"] + 1.8] if kind == "wall" else tris
                for t in low: d.polygon(tri_pts(t, s), fill=col, outline=col)
            else:
                d.rectangle(rect_pts(r, s), fill=col)
    flush_overlay()

    # ramp chevrons (point downhill)
    for lvl, r in ramps:
        dirn = ramp_downhill(r)
        if not dirn: continue
        cx, cz = (r["x0"] + r["x1"]) / 2, (r["z0"] + r["z1"]) / 2
        length = (r["x1"] - r["x0"]) if dirn[0] else (r["z1"] - r["z0"])
        half_w = 1.2
        for k in range(-int(length / 2.4), int(length / 2.4) + 1):
            px, pz = cx + dirn[0] * k * 2.0, cz + dirn[1] * k * 2.0
            tip = (px + dirn[0] * 0.6, pz + dirn[1] * 0.6)
            side = (-dirn[1], dirn[0])
            a = (px + side[0] * half_w, pz + side[1] * half_w); b = (px - side[0] * half_w, pz - side[1] * half_w)
            d.line([to_px(*a, s), to_px(*tip, s), to_px(*b, s)], fill=(215, 225, 232), width=2 * s)

    def hidden(x, z, lvl):
        """True if a floor of a higher level covers this point (e.g. the lab under Containment)."""
        px, py = to_px(x, z)
        if not (0 <= px < W and 0 <= py < H): return False
        return any(covered[L].getpixel((int(px), int(py))) for L in LEVELS if L > lvl + 1)

    # Locked Lab outline (its south end runs under Containment)
    lab = [r for _l, _o, k, r in items if k == "floor" and "Locked_lab/Lab/Floor" in r["p"]]
    lab_box = None
    if lab:
        lab_box = (min(r["x0"] for r in lab), max(r["x1"] for r in lab), min(r["z0"] for r in lab), max(r["z1"] for r in lab))
        dashed_rect(d, to_px(lab_box[0], lab_box[3], s), to_px(lab_box[1], lab_box[2], s), (150, 175, 230), 3 * s)

    if TERRITORY_VIEW:
        img, d = draw_territories(img, covered, s)

    # decals
    decals = [o for o in data["objects"] if o["t"] == "DecalProjector"]
    for o in decals:
        if hidden(o["x"], o["z"], level_of(o["y"])): continue
        x, y = to_px(o["x"], o["z"], s); r = 6 * s
        d.line([(x - r, y - r), (x + r, y + r)], fill=DECAL, width=3 * s); d.line([(x - r, y + r), (x + r, y - r)], fill=DECAL, width=3 * s)

    # lights
    on = shadowed = 0
    glow = Image.new("RGBA", img.size, (0, 0, 0, 0)); gd = ImageDraw.Draw(glow)
    marks = []
    for l in data["lights"]:
        if hidden(l["x"], l["z"], level_of(l["y"] - 2.5)): continue
        r_, g_, b_ = l["r"], l["g"], l["b"]
        kind = "red" if r_ > 0.6 and g_ < 0.45 and b_ < 0.45 else "green" if g_ > r_ * 1.25 and g_ > b_ else "cool"
        x, y = to_px(l["x"], l["z"], s)
        if l["on"]:
            on += 1; shadowed += l["sh"]
            R = 26 * s; c = LIGHT[kind]
            gd.ellipse([x - R, y - R, x + R, y + R], fill=c + (38,))
        marks.append((x, y, kind, l["on"], l["sh"]))
    img = Image.alpha_composite(img, glow); d = ImageDraw.Draw(img)
    for x, y, kind, lit, sh in marks:
        if lit:
            R = 8 * s; d.ellipse([x - R, y - R, x + R, y + R], fill=LIGHT[kind], outline=(20, 20, 20), width=s)
            if sh: R = 14 * s; d.ellipse([x - R, y - R, x + R, y + R], outline=(245, 245, 235), width=3 * s)
        else:
            R = 7 * s; d.ellipse([x - R, y - R, x + R, y + R], outline=(150, 155, 160), width=2 * s)

    # power: fusebox -> bulkhead
    sw = next((o for o in data["objects"] if o["t"] == "SwitchInteractable"), None)
    doors = [o for o in data["objects"] if o["t"] == "Door"]
    bulk = next((o for o in doors if "Bulkhead" in o["p"]), None)
    if sw:
        x, y = to_px(sw["x"], sw["z"], s); R = 13 * s
        d.rectangle([x - R, y - R, x + R, y + R], fill=(30, 30, 30), outline=LOCKED, width=3 * s)
        d.polygon([(x + 2 * s, y - 10 * s), (x - 6 * s, y + 2 * s), (x, y + 2 * s), (x - 2 * s, y + 10 * s),
                   (x + 6 * s, y - 2 * s), (x, y - 2 * s)], fill=LOCKED)
    if bulk:
        x, y = to_px(bulk["x"], bulk["z"], s); R = 12 * s
        d.rounded_rectangle([x - R, y - R * 0.6, x + R, y + R], radius=3 * s, fill=LOCKED)
        d.arc([x - R * 0.65, y - R * 1.5, x + R * 0.65, y + R * 0.2], 180, 360, fill=LOCKED, width=3 * s)
    for o in doors:
        if o is bulk: continue
        x, y = to_px(o["x"], o["z"], s); R = 9 * s
        d.polygon([(x, y - R), (x + R, y), (x, y + R), (x - R, y)], fill=DOOR, outline=(40, 30, 10), width=s)

    # agent start posts (facility sandbox; v5: proposed)
    if TERRITORY_VIEW:
        AGENTS[:] = [(n, c, PROPOSED_POSTS.get(n, (p, l))[0], PROPOSED_POSTS.get(n, (p, l))[1], col)
                     for n, c, p, l, col in AGENTS]
        amb = next(a for a in AGENTS if a[0] == "Ambusher")
        a0 = to_px(*amb[2], s); a1 = to_px(*AMBUSH_SPOT, s)
        dashed_line(d, a0, a1, amb[4], 4 * s, 16 * s, 10 * s)
        ang = math.atan2(a1[1] - a0[1], a1[0] - a0[0])
        tip = (a1[0] - 24 * s * math.cos(ang), a1[1] - 24 * s * math.sin(ang))
        for da in (0.5, -0.5):
            d.line([tip, (tip[0] - 24 * s * math.cos(ang + da), tip[1] - 24 * s * math.sin(ang + da))], fill=amb[4], width=4 * s)
        R = 22 * s; x, y = a1
        d.polygon([(x, y - R), (x + R, y), (x, y + R), (x - R, y)], outline=amb[4], width=4 * s)
        d.ellipse([x - 9 * s, y - 5 * s, x + 9 * s, y + 5 * s], outline=amb[4], width=2 * s)
        d.ellipse([x - 3 * s, y - 3 * s, x + 3 * s, y + 3 * s], fill=amb[4])
        mim = next(a for a in AGENTS if a[0] == "Mimic")
        cx, cy = to_px(*mim[2], s); R = MIMIC_VOICE * PX * s
        for k in range(72):
            d.arc([cx - R, cy - R, cx + R, cy + R], k * 5, k * 5 + 3, fill=mim[4], width=3 * s)
    for name, ch, (ax, az), lvl, col in AGENTS:
        x, y = to_px(ax, az, s); R = 16 * s
        d.polygon([(x, y - R), (x + R, y), (x, y + R), (x - R, y)], fill=col, outline=(15, 15, 15), width=2 * s)

    # player spawn
    sp = next((o for o in data["objects"] if o["t"] == "FirstPersonController"), None)
    if sp:
        x, y = to_px(sp["x"], sp["z"], s); R = 16 * s
        d.ellipse([x - R, y - R, x + R, y + R], outline=SPAWN, width=4 * s)
        fx, fz = sp["fx"], sp["fz"]; n = math.hypot(fx, fz) or 1
        tip = to_px(sp["x"] + fx / n * 2.2, sp["z"] + fz / n * 2.2, s)
        d.line([(x, y), tip], fill=SPAWN, width=4 * s)
        ang = math.atan2(tip[1] - y, tip[0] - x)
        for da in (2.5, -2.5):
            d.line([tip, (tip[0] - 14 * s * math.cos(ang + da / 3), tip[1] - 14 * s * math.sin(ang + da / 3))], fill=SPAWN, width=4 * s)

    # objective: numbered gold badges (1 key, 2 power, 3 lab door, 4 medicine)
    objective = [(1, KEY_POS), (2, (sw["x"], sw["z"]) if sw else None), (3, (bulk["x"], bulk["z"]) if bulk else None),
                 (4, MEDICINE_POS)]
    badges = []
    for num, pos in objective:
        if pos is None: continue
        x, y = to_px(pos[0], pos[1], s)
        off = {1: (0, 0), 2: (34, -34), 3: (-34, -34), 4: (0, 0)}[num]
        bx, by = x + off[0] * s, y + off[1] * s
        if num == 1:     # key
            R = 20 * s; d.ellipse([x - R, y - R, x + R, y + R], fill=(30, 26, 10), outline=GOAL, width=4 * s)
            d.ellipse([x - 12 * s, y - 7 * s, x - 2 * s, y + 3 * s], outline=GOAL, width=3 * s)
            d.line([(x - 2 * s, y - 2 * s), (x + 12 * s, y - 2 * s)], fill=GOAL, width=3 * s)
            d.line([(x + 8 * s, y - 2 * s), (x + 8 * s, y + 5 * s)], fill=GOAL, width=3 * s)
            d.line([(x + 12 * s, y - 2 * s), (x + 12 * s, y + 4 * s)], fill=GOAL, width=3 * s)
            bx, by = x + 26 * s, y - 26 * s
        elif num == 4:   # medicine: cross in a glowing disc
            R = 24 * s; d.ellipse([x - R, y - R, x + R, y + R], fill=(20, 60, 36), outline=MED, width=4 * s)
            d.rectangle([x - 4 * s, y - 13 * s, x + 4 * s, y + 13 * s], fill=(235, 255, 240))
            d.rectangle([x - 13 * s, y - 4 * s, x + 13 * s, y + 4 * s], fill=(235, 255, 240))
            bx, by = x + 30 * s, y - 30 * s
        badges.append((num, bx, by))
        R = 15 * s; d.ellipse([bx - R, by - R, bx + R, by + R], fill=GOAL, outline=(20, 20, 20), width=2 * s)

    img = img.resize((W, H), Image.LANCZOS).convert("RGB")
    d = ImageDraw.Draw(img)

    # ---------------------------------------------------------------- text layer
    f_title, f_sub = font(True, 40), font(False, 19)
    f_lab, f_lsub = font(True, 21), font(False, 17)
    f_leg_h, f_leg, f_small = font(True, 27), font(False, 20), font(False, 18)
    d.text((LEFT, 22), "ECHO — Facility Floor Plan", font=f_title, fill=TEXT)
    d.text((LEFT + 560, 40), ("SampleScene · top-down · v5 (2026-10-17): objective + proposed agent posts & territories"
            if TERRITORY_VIEW else "SampleScene · top-down · ceilings & overhead geometry removed · v4 (2026-10-17): "
           "main objective — key + power → Lab → medicine"), font=f_sub, fill=SUB)

    for num, bx, by in badges:
        t = str(num); tw = d.textlength(t, font=f_lab)
        d.text((bx / SS - tw / 2, by / SS - 15), t, font=f_lab, fill=(20, 20, 20))
    for name, ch, (ax, az), lvl, col in AGENTS:
        x, y = to_px(ax, az)
        tw = d.textlength(ch, font=f_lab)
        d.text((x - tw / 2, y - 14), ch, font=f_lab, fill=(15, 15, 15))

    labels = [
        ("SPAWN STAIRWELL", "0 m → −6 m · safe start" if TERRITORY_VIEW else "0 m → −6 m · top sealed", 24, 74.5),
        ("ENTRANCE CORRIDORS", "−6 m", 33.5, 47.2),
        ("ENTRANCE HALL", "Room_1 · −6 m", 44.5, 23.5),
        ("RAMP", "−6 → −12 m", 63, 25.2),
        ("CORRIDOR TEE", "−12 m · hub: passed 3×" if TERRITORY_VIEW else "−12 m", 75, 42.3),
        ("RAMPS DOWN", "−12 → −24 m", 87.8, 52.5),
        ("WAREHOUSE", "Room_3 · −24 m · catwalks above", 57, 86.5),
        ("STORAGE HALL", "−24 m", 78, 65.2),
        ("CATWALK BAY", "−24 m · Lab door power switch", 78, 92),
        ("CORRIDOR CORNER", "−12 m", 80, 6.2),
        ("LAB (LOCKED)", "Room_2 · −12 m · the medicine · door needs key + power", 101.5, 1.6),
        ("LAB STAIRS", "−24 → −30 m", 104.5, 57),
        ("KEY ROOM", "hospital wing · −30 m · runs under the Lab", 96, 47.5),
    ]
    if TERRITORY_VIEW:
        labels.append(("LEARNED AMBUSH", "Ambusher waits here (3rd pass)", 84.5, 20.5))
    for title, sub, x, z in labels:
        px, py = to_px(x, z)
        w1 = d.textlength(title, font=f_lab); w2 = d.textlength(sub, font=f_lsub); w = max(w1, w2) + 18
        d.rounded_rectangle([px - w / 2, py - 18, px + w / 2, py + 30], radius=6, fill=(14, 17, 22), outline=(60, 66, 74))
        d.text((px - w1 / 2, py - 17), title, font=f_lab, fill=TEXT)
        d.text((px - w2 / 2, py + 7), sub, font=f_lsub, fill=SUB)

    # scale bar + north
    sx, sy = LEFT, H - 90
    d.rectangle([sx, sy, sx + 5 * PX, sy + 8], fill=(220, 225, 230)); d.rectangle([sx + 5 * PX, sy, sx + 10 * PX, sy + 8], fill=(110, 118, 126))
    d.text((sx - 4, sy - 28), "0", font=f_small, fill=SUB); d.text((sx + 10 * PX - 20, sy - 28), "10 m", font=f_small, fill=SUB)
    nx, ny = LEFT + 10 * PX + 90, H - 100
    d.polygon([(nx, ny - 30), (nx + 16, ny + 14), (nx, ny + 4), (nx - 16, ny + 14)], fill=(225, 230, 235))
    d.text((nx - 26, ny + 20), "N (+Z)", font=f_small, fill=SUB)

    # ---------------------------------------------------------------- legend
    d.rectangle([LEGEND_X - 20, 0, W, H], fill=(17, 20, 26))
    x, y = LEGEND_X + 10, 110
    def head(t):
        nonlocal y
        d.text((x, y), t, font=f_leg_h if t == "LEGEND" else font(True, 22), fill=TEXT); y += 44 if t == "LEGEND" else 36
    def swatch(col, t, kind="box"):
        nonlocal y
        if kind == "box": d.rectangle([x, y + 2, x + 30, y + 24], fill=col, outline=(70, 76, 84))
        elif kind == "dash": dashed_rect(d, (x, y + 3), (x + 30, y + 23), col, 2, 6, 4)
        elif kind == "diamond": d.polygon([(x + 15, y), (x + 30, y + 13), (x + 15, y + 26), (x, y + 13)], fill=col, outline=(15, 15, 15))
        elif kind == "dot": d.ellipse([x + 8, y + 6, x + 22, y + 20], fill=col)
        elif kind == "ring": d.ellipse([x + 4, y + 2, x + 26, y + 24], outline=(245, 245, 235), width=3); d.ellipse([x + 9, y + 7, x + 21, y + 19], fill=col)
        elif kind == "off": d.ellipse([x + 9, y + 7, x + 21, y + 19], outline=(150, 155, 160), width=2)
        elif kind == "x": d.line([(x + 8, y + 5), (x + 22, y + 21)], fill=col, width=3); d.line([(x + 8, y + 21), (x + 22, y + 5)], fill=col, width=3)
        elif kind == "spawn": d.ellipse([x + 3, y + 1, x + 27, y + 25], outline=col, width=3)
        elif kind == "lock": d.rounded_rectangle([x + 5, y + 9, x + 25, y + 25], radius=3, fill=col); d.arc([x + 9, y, x + 21, y + 16], 180, 360, fill=col, width=3)
        elif kind == "bolt": d.rectangle([x + 2, y + 2, x + 28, y + 26], fill=(30, 30, 30), outline=col, width=2); d.polygon([(x + 17, y + 5), (x + 9, y + 15), (x + 15, y + 15), (x + 13, y + 23), (x + 21, y + 12), (x + 15, y + 12)], fill=col)
        d.text((x + 44, y), t, font=f_leg, fill=(215, 220, 226)); y += 34

    head("LEGEND")
    head("Floor elevation")
    for L in LEVELS: swatch(LEVEL_COL[L], f"{L} m" if L else "0 m")
    y += 10
    swatch(tuple(int(c * 0.92 + 12) for c in LEVEL_COL[-12]), "Ramp (chevrons point downhill)")
    swatch(STAIR, "Stairs / steps")
    swatch((86, 66, 44), "Catwalk (upper level)")
    swatch(PROP, "Props: crates, shelves, beds")
    swatch(WALL, "Walls / corridor shell")
    swatch(DOOR, "Door leaves / openings")
    swatch(DOOR, "Working sliding door", "diamond")
    swatch(LOCKED, "Lab door (needs key + power)", "lock")
    swatch(LOCKED, "Lab door power switch", "bolt")
    swatch(SEAL, "Sealed door")
    swatch((150, 175, 230), "Lower level hidden below", "dash")
    y += 14
    head("Lights")
    swatch(LIGHT["cool"], "Cool fluorescent", "dot")
    swatch(LIGHT["red"], "Red emergency", "dot")
    swatch(LIGHT["green"], "Sickly green accent", "dot")
    swatch(LIGHT["cool"], "Casts shadows (ring)", "ring")
    swatch(None, "Dead fixture (off)", "off")
    y += 8
    swatch(DECAL, "Grime / leak decal", "x")
    swatch(SPAWN, "Player spawn + facing", "spawn")
    y += 14
    if TERRITORY_VIEW:
        head("Agents: proposed posts & territories")
        roles = {"Listener": "Entrance Hall · hears loud noise", "Ambusher": "Corridor Tee · learns your route",
                 "Tracker": "Warehouse · follows footsteps", "Mimic": "Lab stairs · fakes sounds"}
        for name, ch, _p, lvl, col in AGENTS:
            swatch(col, f"{ch}  {name} — {roles[name]}", "diamond")
        d.rectangle([x, y + 2, x + 30, y + 24], fill=(70, 60, 50), outline=(255, 150, 40), width=2)
        d.text((x + 44, y), "Territory (tinted, own floor only)", font=f_leg, fill=(215, 220, 226)); y += 34
        d.arc([x + 2, y + 1, x + 28, y + 27], 0, 360, fill=(190, 230, 100), width=2)
        d.text((x + 44, y), "Mimic voice range (18 m)", font=f_leg, fill=(215, 220, 226)); y += 34
        d.polygon([(x + 15, y), (x + 30, y + 13), (x + 15, y + 26), (x, y + 13)], outline=(170, 90, 230), width=2)
        d.text((x + 44, y), "Learned ambush spot", font=f_leg, fill=(215, 220, 226)); y += 40
        head("Acts")
        for line in ("1 In: Listener only — learn to be quiet", "2 Power + key: Tracker + Mimic (dead end!)",
                     "3 Lab: Ambusher has learned the route;", "   medicine → alarm → escape"):
            d.text((x, y), line, font=f_small, fill=SUB); y += 27
        y += 16
    else:
        head("Agent start posts")
        for name, ch, _p, lvl, col in AGENTS:
            swatch(col, f"{ch}  {name} ({lvl} m)", "diamond")
        d.text((x, y), "(facility sandbox: Sandbox_AllAgents)", font=f_small, fill=SUB); y += 44
    head("Objective: find the medicine")
    for num, line in ((1, "Key — Key room (−30 m, via Lab stairs)"), (2, "Power — switch in the Catwalk Bay"),
                      (3, "Open the Lab door (key + power)"), (4, "Medicine — Lab, east wall")):
        d.ellipse([x + 2, y + 1, x + 28, y + 27], fill=GOAL, outline=(20, 20, 20))
        tw = d.textlength(str(num), font=f_small); d.text((x + 15 - tw / 2, y + 2), str(num), font=f_small, fill=(20, 20, 20))
        d.text((x + 44, y), line, font=f_leg, fill=(215, 220, 226)); y += 34
    d.text((x, y), "1 and 2 in any order", font=f_small, fill=SUB); y += 40
    head("Route")
    for line in ("Spawn → stairwell → Entrance corridors", "→ Entrance Hall → ramp → Corridor Tee",
                 "Tee north: ramps → Warehouse (power 2)", "  Warehouse east → Lab stairs → Key room (1)",
                 "Tee south: Corner → Lab door (3) → Lab (4)"):
        d.text((x, y), line, font=f_small, fill=SUB); y += 27
    y += 16
    d.text((x, y), f"{on} lights on · {shadowed} shadowed · {len(decals)} decals · {stats['props']} props",
           font=f_small, fill=(130, 138, 148))

    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    img.save(OUT, optimize=True)
    print("wrote", OUT, img.size, "lights on", on, "shadowed", shadowed)

if __name__ == "__main__":
    main()
