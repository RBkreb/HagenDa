"""
Stage 0 -- design document + top-down layout.

Outputs
-------
docs/layout_topdown.png            500x500 px, 1 px = 1 m (canonical, data-faithful)
docs/layout_topdown_annotated.png  1000x1000 px with legend + labels (for eyeballing)
docs/metrics_layout.json           quantified layout metrics
docs/gdd.md                        game design document
docs/flow.md                       text player-flow chart

Run:  python Terrainscripts/stage0_layout.py
"""

import json
import math
import os
import shutil
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFont

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import map_layout as ML  # noqa: E402

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DOCS = os.path.join(ROOT, "docs")
VERS = os.path.join(DOCS, "versions")
N = 500  # raster resolution: 1 px = 1 m

# ---- palette -------------------------------------------------------------------------
COL = {
    "water":      (34, 78, 130),
    "ring":       (58, 58, 64),
    "ring_top":   (92, 92, 99),
    "walk":       (150, 158, 140),
    "lowland":    (168, 178, 152),
    "mid":        (186, 172, 132),
    "high":       (192, 140, 92),
    "ridge":      (214, 202, 172),
    "cover_half": (120, 104, 66),
    "cover_full": (88, 74, 44),
    "lane_mid":   (222, 96, 96),
    "lane_main":  (235, 168, 72),
    "lane_flank": (128, 190, 120),
    "lane_valley":(150, 140, 220),
    "sightline":  (255, 246, 130),
    "objective":  (214, 46, 46),
    "highground": (246, 140, 40),
    "spawn_sw":   (72, 130, 226),
    "spawn_ne":   (72, 180, 226),
    "butte":      (140, 112, 84),
    "tunnel":     (30, 30, 30),
    "bridge":     (250, 236, 210),
    "notch":      (255, 246, 130),
    "vehicle":    (226, 196, 90),
    "teleport":   (140, 250, 240),
    "grid":       (110, 110, 110),
}

FONT_BOLD = None
FONT_REG = None
for cand in [r"C:\Windows\Fonts\msyhbd.ttc", r"C:\Windows\Fonts\arialbd.ttf",
             r"C:\Windows\Fonts\segoeuib.ttf", r"C:\Windows\Fonts\consolab.ttf"]:
    if FONT_BOLD is None and os.path.exists(cand):
        try:
            FONT_BOLD = ImageFont.truetype(cand, 15)
        except Exception:
            pass
for cand in [r"C:\Windows\Fonts\msyh.ttc", r"C:\Windows\Fonts\arial.ttf",
             r"C:\Windows\Fonts\segoeui.ttf", r"C:\Windows\Fonts\consola.ttf"]:
    if FONT_REG is None and os.path.exists(cand):
        try:
            FONT_REG = ImageFont.truetype(cand, 14)
        except Exception:
            pass
if FONT_BOLD is None:
    FONT_BOLD = ImageFont.load_default()
if FONT_REG is None:
    FONT_REG = ImageFont.load_default()


def world_to_px(x, z, size=N):
    """World metres -> pixel. row 0 = north (z = +250)."""
    scale = size / ML.MAP_SIZE
    return (x + ML.HALF) * scale, (ML.HALF - z) * scale


def fill(mask, rgb, size=N):
    """Paint a boolean north-up mask onto an RGBA array of the given pixel size."""
    if size == N:
        m = mask
    else:
        img = Image.fromarray((mask * 255).astype(np.uint8), mode="L")
        m = np.asarray(img.resize((size, size), Image.NEAREST)) > 127
    return m, rgb


# --------------------------------------------------------------------------------------
# 1. data-faithful 500x500 render
# --------------------------------------------------------------------------------------
def render_base(size=N, draw_overlay=True):
    X, Z = ML.grid(size)
    arr = np.zeros((size, size, 4), dtype=np.uint8)
    arr[..., 3] = 255

    def paint(mask, rgb, alpha=255):
        arr[mask] = (*rgb, alpha)

    ring = ML.mask_ring(X, Z)
    water = ML.mask_lakes(X, Z)
    play = ML.mask_play_area(X, Z)
    butte = ML.disc_mask(X, Z, *ML.BUTTE["pos"], ML.BUTTE["r"])
    butte_core = ML.mask_butte_core(X, Z)
    high = ML.mask_high_ground(X, Z)
    t = ML.t_of(X, Z)

    # elevation tier background: a coarse proxy from the design intent
    paint(play, COL["lowland"])
    paint(play & (np.abs(t) > 105.0), COL["mid"])          # mid terrace away from valley
    paint(ring, COL["ring"])
    paint(ring & ML.mask_outside_play(X, Z), COL["ring_top"])
    paint(water, COL["water"])
    paint(butte, COL["butte"])
    paint(butte_core, (110, 88, 62))
    paint(high, COL["highground"])

    img = Image.fromarray(arr, mode="RGBA")
    d = ImageDraw.Draw(img, "RGBA")

    def overlay(mask, rgb, alpha):
        o = np.zeros((size, size, 4), dtype=np.uint8)
        o[mask] = (*rgb, alpha)
        img.alpha_composite(Image.fromarray(o, mode="RGBA"))

    # cover belts
    overlay(ML.mask_cover_belt(X, Z), COL["cover_half"], 120)

    # lanes
    for ln in ML.ALL_LANES:
        col = {"mid": COL["lane_mid"], "main": COL["lane_main"],
               "flank": COL["lane_flank"], "valley": COL["lane_valley"]}[ln["kind"]]
        overlay(ML.mask_lane(X, Z, ln), col, 135)

    # long sightlines
    overlay(ML.mask_notch(X, Z), COL["sightline"], 210)

    if draw_overlay:
        # tunnels (dashed)
        for tn in ML.TUNNELS:
            p0 = world_to_px(*tn["p0"], size)
            p1 = world_to_px(*tn["p1"], size)
            _dashed_line(d, p0, p1, COL["tunnel"], width=max(2, size // 250), dash=7, gap=5)
        # bridges
        for bg in ML.BRIDGES:
            px, py = world_to_px(*bg["pos"], size)
            r = max(2, bg["width"] / 2 * size / N)
            d.ellipse([px - r, py - r, px + r, py + r], fill=COL["bridge"])

        # teleport pads (square outline) and vehicles (small rounded rect)
        for tp in ML.TELEPORTS:
            px, py = world_to_px(*tp["pos"], size)
            r = 5 * size / N
            d.rectangle([px - r, py - r, px + r, py + r], outline=COL["teleport"],
                        width=max(2, size // 300))
        for vh in ML.VEHICLES:
            px, py = world_to_px(*vh["pos"], size)
            r = 4 * size / N
            d.rounded_rectangle([px - r, py - 2 * r, px + r, py + 2 * r], radius=2 * size / N,
                                fill=COL["vehicle"], outline=(20, 20, 20))

        # objective discs
        for o in ML.OBJECTIVES:
            px, py = world_to_px(*o["pos"], size)
            r = o["r"] * size / N
            d.ellipse([px - r, py - r, px + r, py + r], fill=COL["objective"])
            r2 = 4 * size / N
            d.ellipse([px - r2, py - r2, px + r2, py + r2], fill=(255, 255, 255))

        # spawn zones
        for sp in ML.SPAWNS:
            col = COL["spawn_sw"] if sp["team"] == "SW" else COL["spawn_ne"]
            px, py = world_to_px(*sp["center"], size)
            r = sp["flatten_r"] * size / N
            d.ellipse([px - r, py - r, px + r, py + r], outline=col, width=max(2, size // 200))
            for sx, sz in ML.spawn_points(sp):
                qx, qy = world_to_px(sx, sz, size)
                d.ellipse([qx - 3 * size / N, qy - 3 * size / N,
                           qx + 3 * size / N, qy + 3 * size / N], fill=col)

        # 100 m grid on the play area
        gcol = (*COL["grid"], 70)
        for v in np.arange(-200, 201, 100):
            a = world_to_px(v, -250, size)
            b = world_to_px(v, 250, size)
            d.line([a, b], fill=gcol, width=1)
            a = world_to_px(-250, v, size)
            b = world_to_px(250, v, size)
            d.line([a, b], fill=gcol, width=1)
    return img


def _dashed_line(d, p0, p1, col, width=2, dash=7, gap=5):
    x0, y0 = p0
    x1, y1 = p1
    L = math.hypot(x1 - x0, y1 - y0)
    if L == 0:
        return
    ux, uy = (x1 - x0) / L, (y1 - y0) / L
    t = 0.0
    while t < L:
        t2 = min(t + dash, L)
        d.line([(x0 + ux * t, y0 + uy * t), (x0 + ux * t2, y0 + uy * t2)],
               fill=col, width=width)
        t = t2 + gap


def make_annotated(size=1000):
    img = render_base(size=size, draw_overlay=True)
    d = ImageDraw.Draw(img, "RGBA")
    sc = size / N
    d.rectangle([0, 0, size - 1, size - 1], outline=(255, 255, 255), width=2)

    # axis / scale bar (100 m)
    x0, y0 = world_to_px(-230, -230, size)
    d.line([(x0, y0), (x0 + 100 * sc, y0)], fill=(255, 255, 255), width=4)
    d.line([(x0, y0 - 5), (x0, y0 + 5)], fill=(255, 255, 255), width=3)
    d.line([(x0 + 100 * sc, y0 - 5), (x0 + 100 * sc, y0 + 5)], fill=(255, 255, 255), width=3)
    d.text((x0 + 100 * sc + 8, y0 - 10), "100 m", font=FONT_BOLD, fill=(255, 255, 255))
    nx, ny = world_to_px(-230, -250 + 18, size)
    d.text((nx, ny), "N", font=FONT_BOLD, fill=(255, 255, 255))
    d.line([(nx + 5, ny + 20), (nx + 5, ny + 52)], fill=(255, 255, 255), width=3)

    # labels
    for o in ML.OBJECTIVES:
        px, py = world_to_px(*o["pos"], size)
        d.text((px - 8 * sc, py - 8 * sc), o["id"], font=FONT_BOLD, fill=(255, 255, 255))
    for h in ML.HIGH_GROUNDS:
        px, py = world_to_px(*h["pos"], size)
        d.text((px - 10 * sc, py - 9 * sc), h["id"], font=FONT_BOLD, fill=(255, 255, 255))
    px, py = world_to_px(*ML.BUTTE["pos"], size)
    d.text((px - 16 * sc, py - 8 * sc), "BUTTE", font=FONT_BOLD, fill=(255, 255, 255))
    for sp in ML.SPAWNS:
        px, py = world_to_px(*sp["center"], size)
        d.text((px - 34 * sc, py - 6 * sc), sp["team"] + " spawn", font=FONT_BOLD,
               fill=(255, 255, 255))
    for tn in ML.TUNNELS:
        mx = (tn["p0"][0] + tn["p1"][0]) / 2
        mz = (tn["p0"][1] + tn["p1"][1]) / 2
        px, py = world_to_px(mx, mz, size)
        d.text((px + 5 * sc, py + 4 * sc), tn["id"], font=FONT_REG, fill=(30, 30, 30))
    for bg in ML.BRIDGES:
        px, py = world_to_px(*bg["pos"], size)
        d.text((px + 5 * sc, py - 14 * sc), bg["id"], font=FONT_REG, fill=(30, 30, 30))

    # legend
    items = [
        ("walkable lowland", COL["lowland"]),
        ("mid terrace 25-45 m", COL["mid"]),
        ("high ground 65-95 m", COL["highground"]),
        ("impassable ridge 100-120 m", COL["ring_top"]),
        ("water (0 m)", COL["water"]),
        ("cover belt", COL["cover_half"]),
        ("lane: centre run 15 m", COL["lane_mid"]),
        ("lane: main assault 25 m", COL["lane_main"]),
        ("lane: south flank 40 m", COL["lane_flank"]),
        ("valley road 30 m", COL["lane_valley"]),
        ("long sightline (ridge notch)", COL["sightline"]),
        ("objective A/B/C", COL["objective"]),
        ("spawn zone SW / NE", COL["spawn_sw"]),
        ("tunnel (dashed)", COL["tunnel"]),
        ("bridge", COL["bridge"]),
        ("vehicle", COL["vehicle"]),
        ("teleport pad", COL["teleport"]),
    ]
    lx, ly = 14, 14
    bw = 330
    bh = len(items) * 22 + 14
    d.rectangle([lx, ly, lx + bw, ly + bh], fill=(0, 0, 0, 190))
    for i, (lab, c) in enumerate(items):
        yy = ly + 8 + i * 22
        d.rectangle([lx + 8, yy, lx + 30, yy + 14], fill=c, outline=(255, 255, 255))
        d.text((lx + 38, yy - 1), lab, font=FONT_REG, fill=(255, 255, 255))

    d.text((lx, ly + bh + 8), "HagenDa 500 x 500 m  |  1 px = 1 m (this image 2x)  |  "
                            "playable 400 x 400 m", font=FONT_BOLD, fill=(255, 255, 255))
    return img


# --------------------------------------------------------------------------------------
# 2. main
# --------------------------------------------------------------------------------------
def _route_samples(poly, step=5.0):
    """Walk a polyline in `step` metre increments; yield (dist, x, z)."""
    L = ML.polyline_length(poly)
    n = max(2, int(round(L / step)))
    pts = []
    for k in range(n + 1):
        d = L * k / n
        acc = 0.0
        p = poly[-1]
        for i in range(len(poly) - 1):
            sl = math.dist(poly[i], poly[i + 1])
            if acc + sl >= d or i == len(poly) - 2:
                u = min(1.0, (d - acc) / sl) if sl > 0 else 0.0
                p = (poly[i][0] + u * (poly[i + 1][0] - poly[i][0]),
                     poly[i][1] + u * (poly[i + 1][1] - poly[i][1]))
                break
            acc += sl
        pts.append((d, p[0], p[1]))
    return pts


def _cover_counts(pts, half_pts, full_pts):
    """Per sample: number of half-cover pieces within 15 m and full-cover pieces within 40 m."""
    from scipy.spatial import cKDTree
    h = cKDTree(np.asarray(half_pts, dtype=float))
    f = cKDTree(np.asarray(full_pts, dtype=float))
    q = np.asarray([(x, z) for _, x, z in pts], dtype=float)
    # count_neighbors alternative: query_ball_point is exact but slow; use radius counts
    nh = np.array([len(h.query_ball_point(p, ML.COVER_HALF_RADIUS)) for p in q])
    nf = np.array([len(f.query_ball_point(p, ML.COVER_FULL_RADIUS)) for p in q])
    return nh, nf


def _classify(nh, blocked, butte):
    """Route state at one sample.  `butte` means the lane is physically underground here
    (it passes through TUN_00), so it is not an exposed surface position."""
    if blocked:
        return "TUNNEL" if butte else "BLOCKED"
    if butte:
        return "TUNNEL"
    if nh == 0:
        return "EXPOSED"
    if nh == 1:
        return "PARTIAL"
    return "COVER"


STATE_NOTE = {
    "COVER": "掩体段：可就地架枪、连续掩护推进",
    "PARTIAL": "半暴露段：有落点但换弹/转移要挑时机",
    "EXPOSED": "暴露段：完全无掩体",
    "TUNNEL": "隧道段：从孤丘底部下穿（地面高程不适用，按隧道地面高）",
    "BLOCKED": "不可通行",
}


def generate_flow(m, half_pts, full_pts):
    """docs/flow.md -- the text player-flow chart, generated from the real cover plan and
    the design-intent elevation field (so the rhythm quoted is measured, not asserted)."""
    X, Z = ML.grid(N)
    ring = ML.mask_ring(X, Z)
    lakes = ML.mask_lakes(X, Z)
    butte_core = ML.mask_butte_core(X, Z)
    hfield = ML.intent_height(X, Z)
    gy, gx = np.gradient(hfield)
    slope = np.degrees(np.arctan(np.hypot(gx, gy)))

    def at(x, z):
        i = int(np.clip(round(ML.HALF - 1 - z), 0, N - 1))
        j = int(np.clip(round(x + ML.HALF - 1), 0, N - 1))
        return (float(hfield[i, j]), float(slope[i, j]), bool(butte_core[i, j]),
                bool(ring[i, j] or lakes[i, j]))

    out = []
    A = []
    A.append("# HagenDa — 玩家流向图（文字版）\n")
    A.append("> 由 `Terrainscripts/stage0_layout.py::generate_flow()` 从**实际掩体坐标**与"
             "**设计意图高程场**测量生成，不是人工估计。坐标单位 m。\n")
    A.append("## 0. 判定规则\n")
    A.append("沿路线每 5 m 采样一点，统计该点 **15 m 半径内的半身掩体数**（n_half）"
             "与 **40 m 半径内的全身掩体数**（n_full），并读取高程与坡度：\n")
    A.append("| 判定 | 规则 | 玩法含义 |")
    A.append("| --- | --- | --- |")
    for k in ("COVER", "PARTIAL", "EXPOSED", "TUNNEL"):
        rule = {"COVER": "n_half ≥ 2", "PARTIAL": "n_half = 1", "EXPOSED": "n_half = 0",
                "TUNNEL": "位于孤丘峭壁核心内"}[k]
        A.append(f"| **{k}** | {rule} | {STATE_NOTE[k]} |")
    A.append("")

    routes = [
        ("路线 1 — 中央隧道线（最窄，贴身战）", "L_MID",
         "从西南出生凹湾沿进攻轴直插东北，中途从 **TUN_00 隧道**穿过中央孤丘底部。"),
        ("路线 2 — 主攻线（经中立主目标 A）", "L_NORTH",
         "从出生凹湾爬上台地，经 **A（42 m 台面）** 与桥 BRG_02，再下坡推向对面出生区。"),
        ("路线 3 — 南侧翼线（经 B / C，最宽）", "L_SOUTH",
         "沿 40 m 开阔侧翼连接本方目标与敌方目标，长枪线与载具空间最大。"),
    ]

    A.append("## 1. 三条主路线\n")
    for title, lid, blurb in routes:
        ln = next(l for l in ML.ALL_LANES if l["id"] == lid)
        pts = _route_samples(ln["poly"])
        nh, nf = _cover_counts(pts, half_pts, full_pts)
        L = ML.polyline_length(ln["poly"])
        A.append(f"### {title}\n")
        A.append(f"{blurb}\n")
        A.append(f"- 宽度 **{ln['width']:.0f} m**，全长 **{L:.0f} m**，"
                 f"冲刺 **{L / ML.SPRINT_SPEED:.1f} s** / 载具 **{L / ML.VEHICLE_SPEED:.1f} s**\n")

        # per-sample state, then merge into runs
        samples = []
        for k, (d, x, z) in enumerate(pts):
            hh, sl, bc, bl = at(x, z)
            samples.append(dict(d=d, h=hh, sl=sl, nh=int(nh[k]), nf=int(nf[k]),
                                state=_classify(int(nh[k]), bl, bc)))
        runs = []
        for s in samples:
            if runs and runs[-1]["state"] == s["state"]:
                runs[-1]["items"].append(s)
                runs[-1]["d1"] = s["d"]
            else:
                runs.append(dict(state=s["state"], d0=s["d"], d1=s["d"], items=[s]))
        # drop trivial single-sample PARTIAL flickers so the rhythm reads cleanly
        runs = [r for r in runs if not (r["state"] == "PARTIAL" and len(r["items"]) < 2)]
        A.append("| 段 | 里程 | 长度 | 半身掩体/15m | 全身掩体/40m | 高程变化 | 坡度均值 |")
        A.append("| --- | --- | --- | --- | --- | --- | --- |")
        for r in runs:
            its = r["items"]
            nh_m = sum(i["nh"] for i in its) / len(its)
            nf_m = sum(i["nf"] for i in its) / len(its)
            sl_m = sum(i["sl"] for i in its) / len(its)
            if r["state"] == "TUNNEL":
                elev = "隧道地面 14 m"
            else:
                hmin = min(i["h"] for i in its)
                hmax = max(i["h"] for i in its)
                elev = (f"{hmin:.0f}–{hmax:.0f} m" if hmax - hmin >= 2.0
                        else f"{hmin:.0f} m")
            A.append(f"| {r['state']} | {r['d0']:.0f}–{r['d1']:.0f} m | "
                     f"{r['d1'] - r['d0']:.0f} m | {nh_m:.1f} | {nf_m:.1f} | {elev} | "
                     f"{sl_m:.0f}° |")
        n_exposed = sum(len(r["items"]) for r in runs if r["state"] == "EXPOSED")
        A.append("")
        A.append(f"- 暴露段采样点数：**{n_exposed}**（验收要求 0；隧道段不计入暴露）")
        A.append("")

    # vertical routes
    A.append("## 2. 竖向通路（换线 / 撤退）\n")
    A.append("| 通路 | 类型 | 起讫 | 长度 | 目的 |")
    A.append("| --- | --- | --- | --- | --- |")
    for tn in ML.TUNNELS:
        A.append(f"| {tn['id']} {tn['name']} | 隧道 | {tuple(tn['p0'])} → {tuple(tn['p1'])} | "
                 f"{math.dist(tn['p0'], tn['p1']):.0f} m | 贯穿孤丘 / 棱堡，被压制时换线 |")
    for bg in ML.BRIDGES:
        A.append(f"| {bg['id']} {bg['name']} | 桥 | {tuple(bg['pos'])} | 跨 {bg['span']:.0f} m "
                 f"| 跨河谷，宽 {bg['width']:.0f} m |")
    for hg in ML.HIGH_GROUNDS:
        for r in hg["routes"]:
            A.append(f"| {r['id']} | {'进攻' if r['kind'] == 'attack' else '撤退'}坡道 | "
                     f"{r['poly'][0]} → {r['poly'][1]} | "
                     f"{ML.polyline_length(r['poly']):.0f} m | {hg['id']} {r['slope_deg']:.0f}° |")
    A.append("")

    # objective flow
    A.append("## 3. 目标点流向\n")
    A.append("```")
    A.append("SW 出生凹湾 (-150,-150)")
    A.append("  ├─ 中央隧道线 (15m) ──► TUN_00 从孤丘底部穿过 ──► NE 出生凹湾")
    A.append("  ├─ 主攻线 (25m) ──► [B 西南门 36m] ──► A 中央桥头堡 42m ──► [C 东北门 34m] ──► NE")
    A.append("  └─ 南侧翼线 (40m) ──► [B] ──────────► [C] ──────────► NE")
    A.append("                            ▲")
    A.append("      河谷主街 (30m) ─ NW 湖 ──┴── A ──┴── SE 湖")
    A.append("```")
    A.append("")
    A.append("- **A 是唯一中立主目标**（落在镜像轴上，双方各 258 m），控制 A 即控制河谷中段。")
    A.append("- **B / C 是各自的本方目标**（距本方出生点 165 m、距对方 327 m）：保底收入点。")
    A.append("- 传送点只在 **本方出生点 ↔ 本方目标**（西南 spawn↔B，东北 spawn↔C），"
             "不提供直达 A 的捷径，A 必须靠步行或载具争夺。")
    A.append("")

    A.append("## 4. 出生点互视结论\n")
    A.append(f"- 两个出生点连线正好是进攻轴，穿过中央孤丘。出生眼高 "
             f"{m['spawn_los']['spawn_eye_height_m']:.1f} m，孤丘顶 "
             f"{m['spawn_los']['butte_top_m']:.0f} m，**高出 "
             f"{m['spawn_los']['margin_m']:.1f} m**，视线被阻断。")
    A.append("- 阶段 1/2 会用真实高度场重跑该射线检测（含地形起伏），结论写入对应 metrics。")
    A.append("")

    with open(os.path.join(DOCS, "flow.md"), "w", encoding="utf-8") as f:
        f.write("\n".join(A))
    return "\n".join(A)


def main():
    os.makedirs(DOCS, exist_ok=True)
    os.makedirs(VERS, exist_ok=True)

    X, Z = ML.grid(N)
    walk = ML.mask_walkable(X, Z)
    walk_pct = 100.0 * walk.sum() / (N * N)

    # --- cover density verification (design-intent placement) -------------------------
    print("planning half cover ...")
    half_pts, hd, hp = ML.plan_cover_points(
        X, Z, ML.COVER_HALF_GRID, ML.COVER_HALF_RADIUS, walk)
    hd, hp, hok, htot = ML.verify_cover(X, Z, half_pts, ML.COVER_HALF_RADIUS, walk)
    print(f"  half cover: {len(half_pts)} pieces, desert {int(hd.sum())} px, "
          f"coverage {hp:.6f}%")

    print("planning full cover ...")
    full_pts, fd, fp = ML.plan_cover_points(
        X, Z, ML.COVER_FULL_GRID, ML.COVER_FULL_RADIUS, walk)
    fd, fp, fok, ftot = ML.verify_cover(X, Z, full_pts, ML.COVER_FULL_RADIUS, walk)
    print(f"  full cover: {len(full_pts)} pieces, desert {int(fd.sum())} px, "
          f"coverage {fp:.6f}%")

    metrics = ML.build_metrics(cover_half=(hd, hp, hok, htot),
                               cover_full=(fd, fp, fok, ftot),
                               walkable_pct=walk_pct)
    metrics["cover"]["half_cover_plan"]["pieces"] = len(half_pts)
    metrics["cover"]["full_cover_plan"]["pieces"] = len(full_pts)
    metrics["cover"]["half_cover_positions"] = [[round(a, 1), round(b, 1)] for a, b in half_pts]
    metrics["cover"]["full_cover_positions"] = [[round(a, 1), round(b, 1)] for a, b in full_pts]
    metrics["self_check"] = self_check(metrics)

    with open(os.path.join(DOCS, "metrics_layout.json"), "w", encoding="utf-8") as f:
        json.dump(metrics, f, indent=2, ensure_ascii=False)

    generate_flow(metrics, half_pts, full_pts)

    # --- images ----------------------------------------------------------------------
    base = render_base(N, draw_overlay=True)
    base.save(os.path.join(DOCS, "layout_topdown.png"))
    ann = make_annotated(1000)
    ann.save(os.path.join(DOCS, "layout_topdown_annotated.png"))
    shutil.copy2(os.path.join(DOCS, "layout_topdown.png"),
                 os.path.join(VERS, "stage0_layout_topdown_v1.png"))
    shutil.copy2(os.path.join(DOCS, "metrics_layout.json"),
                 os.path.join(VERS, "stage0_metrics_layout_v1.json"))

    # --- cover desert heat map -------------------------------------------------------
    desert_h, _, _, _ = ML.verify_cover(X, Z, half_pts, ML.COVER_HALF_RADIUS, walk)
    desert_f, _, _, _ = ML.verify_cover(X, Z, full_pts, ML.COVER_FULL_RADIUS, walk)
    hm = np.zeros((N, N, 3), dtype=np.uint8)
    hm[walk] = (40, 44, 40)
    hm[desert_h] = (240, 60, 60)
    hm[desert_f] = (240, 180, 40)
    Image.fromarray(hm).resize((1000, 1000), Image.NEAREST).save(
        os.path.join(DOCS, "stage0_cover_desert_heatmap.png"))

    print("\n=== self-check ===")
    for k, v in metrics["self_check"].items():
        print(f"  {k}: {v}")
    return metrics


def self_check(m):
    """The four questions asked in docs/map/step0.md."""
    d = m["objective_triple_distances_m"]
    lo, hi = 120.0, 180.0
    sides_ok = all(lo <= v <= hi for v in d.values())
    spread = max(d.values()) - min(d.values())

    widths = {ln["id"]: ln["width_m"] for ln in m["lanes"]}
    want = {15.0, 25.0, 40.0}
    widths_ok = want.issubset(set(widths.values()))

    # full traverse: the main assault lane is the longest realistic route
    main = next(ln for ln in m["lanes"] if ln["id"] == "L_NORTH")
    flank = next(ln for ln in m["lanes"] if ln["id"] == "L_SOUTH")
    # spawn-to-spawn straight-line crossing (worst case, sprinting)
    straight = 2 * math.dist(ML.SPAWN_CENTER, (0.0, 0.0)) / ML.SPRINT_SPEED
    traverse_ok = (straight <= 65.0
                   and main["traverse_sprint_s"] <= 65.0
                   and flank["traverse_sprint_s"] <= 65.0)

    los = m["spawn_los"]

    return {
        "objective_triangle_sides_120_180m": "PASS" if sides_ok else "FAIL",
        "objective_triangle_side_spread_m": round(spread, 1),
        "objective_triangle_equilateral_enough": "PASS" if spread <= 10.0 else "FAIL",
        "lane_widths_15_25_40": "PASS" if widths_ok else "FAIL",
        "lane_widths_found_m": sorted(widths.values()),
        "lane_widths_all_distinct": "PASS" if len(
            {ln["width_m"] for ln in m["lanes"] if ln["kind"] != "valley"}) == 3 else "FAIL",
        "spawn_to_spawn_los_blocked": "PASS" if los["blocked"] else "FAIL",
        "spawn_los_margin_m": los["margin_m"],
        "full_map_crossing_sprint_s": round(straight, 1),
        "main_lane_traverse_sprint_s": main["traverse_sprint_s"],
        "flank_traverse_sprint_s": flank["traverse_sprint_s"],
        "traverse_within_65s": "PASS" if traverse_ok else "FAIL",
        "spawn_zone_bbox_80x60": "PASS" if all(
            v["bbox_pass"] for v in m["spawn_zones"].values()) else "FAIL",
        "spawn_bbox_sizes_m": [v["bbox_m"] for v in m["spawn_zones"].values()],
        "sightlines_le_200m": "PASS" if all(
            l["within_200m"] for l in m["long_sightlines"]["lines"]) else "FAIL",
        "sightline_count_ge_3": "PASS" if m["long_sightlines"]["count"] >= 3 else "FAIL",
        "vertical_routes_ge_2": "PASS" if m["vertical_routes"]["count"] >= 2 else "FAIL",
        "high_ground_attack_routes_ge_2": "PASS" if all(
            h["attack_routes"] >= 2 and h["retreat_routes"] >= 1
            for h in m["high_grounds"]) else "FAIL",
        "cover_half_desert_zero": "PASS" if m["cover"]["half_cover_plan"]["pass"] else "FAIL",
        "cover_full_desert_zero": "PASS" if m["cover"]["full_cover_plan"]["pass"] else "FAIL",
        "symmetry_problems": m["balance"]["symmetry_problems"] or "none",
    }


if __name__ == "__main__":
    main()
