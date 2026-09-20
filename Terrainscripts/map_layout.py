"""
Shared map layout definition -- single source of truth for the HagenDa 500x500 m map
pipeline (stages 0-4: layout render, heightmap masks, Blender sculpt targets,
Unity placement).

Coordinate system
-----------------
World: metres, map centre at (0, 0), x = east, z = north.  Extent [-250, +250]^2.
Playable combat area [-200, +200]^2; the outer 50 m band is an impassable ridge ring.

Two rotated axes describe the macro layout:

    s = (x + z) / sqrt(2)      "valley axis"   (s = 0 is the anti-diagonal, NW corner -> SE)
    t = (x - z) / sqrt(2)      "spawn axis"    (t = 0 is the main diagonal, SW corner -> NE)

* The **dry river valley** runs along s = 0: a low floor (6 m) with the two boundary lakes
  at its ends.  Terrain rises with |s| onto two mid terraces (up to 44 m), one on each side.
* The **spawn axis** t = 0 joins the two spawns (SW corner <-> NE corner) and crosses the
  valley at the origin.
* The **central butte** stands at the origin, i.e. on the spawn axis, and is tall enough to
  block the spawn-to-spawn sightline.  It carries a 30 m pass so the valley road can cross.

Mirror symmetry (the fairness guarantee)
----------------------------------------
The layout is invariant under

    M(a, b) = (-b, -a)          reflection about the anti-diagonal s = 0

M maps s -> -s and *preserves* t:

    s' = (x' + z')/sqrt2 = (-z - x)/sqrt2 = -s
    t' = (x' - z')/sqrt2 = (-z + x)/sqrt2 =  t

M swaps the two spawns (SW <-> NE), fixes objective A, the butte, both lakes, all four
lanes (each is M-invariant as a set) and every bridge, and swaps B <-> C and H1 <-> H2.
Because each lane is individually M-invariant, both teams traverse the *same* routes, so
lane-width variety does not create an advantage.  `assert_symmetry()` verifies all of this
numerically, including that mirror-pair features have *equal designed heights*.
"""

import math

import numpy as np

# --------------------------------------------------------------------------------------
# Hard parameters (docs/map/main.md)
# --------------------------------------------------------------------------------------
MAP_SIZE = 500.0          # metres
HALF = MAP_SIZE / 2.0     # 250
PLAY_HALF = 200.0         # playable combat half-extent -> 400 x 400 m
RING = 50.0               # impassable border width

HEIGHT_MIN = 0.0
HEIGHT_MAX = 120.0

SPRINT_SPEED = 8.0        # m/s, player sprint, used for traverse-time estimates
VEHICLE_SPEED = 14.0      # m/s, light transport
WALK_MAX_SLOPE_DEG = 40.0
CLIMB_MIN_SLOPE_DEG = 50.0

VALLEY_FLOOR = 6.0        # valley floor elevation (dry riverbed)
VALLEY_WALL_S = 20.0      # |s| where the valley wall starts to climb
TERRACE_TOP = 44.0        # mid-terrace elevation, reached by |s| = TERRACE_RAMP_S
TERRACE_RAMP_S = 150.0    # |s| at which the terrace reaches TERRACE_TOP (m)
RIDGE_TOP = 112.0         # boundary ridge crest (inside the 100-120 m tier)
OUTER_SHELL = 25.0        # outermost band that always stays ridge (m)
SLOPE_LIMIT_DEG = 35.0    # hard walkability ceiling enforced by the relaxation pass
LAKE_SHORE_M = 26.0       # shore band over which terrain descends into a lake
FLANK_MAX_DEG = 32.0      # target peak grade for a mesa/shoulder apron
HG_APRON_FACTOR = 1.5     # shoulder apron run = factor * rise / tan(FLANK_MAX_DEG)

SQ2 = math.sqrt(2.0)

# Elevation tiers (main.md)
TIER_LOWLAND = (0.0, 15.0)
TIER_MID = (25.0, 45.0)
TIER_HIGH = (65.0, 95.0)
TIER_RIDGE = (100.0, 120.0)


# --------------------------------------------------------------------------------------
# Helpers
# --------------------------------------------------------------------------------------
def s_of(x, z):
    return (np.asarray(x, dtype=float) + np.asarray(z, dtype=float)) / SQ2


def t_of(x, z):
    return (np.asarray(x, dtype=float) - np.asarray(z, dtype=float)) / SQ2


def mirror_point(p):
    """Reflection about the anti-diagonal s = 0 (swaps the two spawns)."""
    x, z = p
    return (-z, -x)


def mirror_poly(poly):
    return [mirror_point(p) for p in poly]


def grid(n=500):
    """Pixel-centre world coordinates for an n x n north-up raster covering the map.

    Returns (X, Z) with shape (n, n); row 0 is z = +250 (north), col 0 is x = -250 (west).
    """
    c = np.arange(n, dtype=float) + 0.5
    x = -HALF + c
    z = HALF - c
    return np.meshgrid(x, z)


def poly_dist(X, Z, poly):
    """Distance from every grid node to a polyline (metres)."""
    best = np.full(X.shape, np.inf)
    for i in range(len(poly) - 1):
        ax, az = poly[i]
        bx, bz = poly[i + 1]
        vx, vz = bx - ax, bz - az
        l2 = vx * vx + vz * vz
        if l2 == 0.0:
            d = np.hypot(X - ax, Z - az)
        else:
            u = np.clip(((X - ax) * vx + (Z - az) * vz) / l2, 0.0, 1.0)
            d = np.hypot(X - (ax + u * vx), Z - (az + u * vz))
        best = np.minimum(best, d)
    return best


def disc_mask(X, Z, cx, cz, r):
    return np.hypot(X - cx, Z - cz) <= r


def polyline_length(poly):
    return float(sum(math.dist(poly[i], poly[i + 1]) for i in range(len(poly) - 1)))


def smoothstep(e0, e1, x):
    t = np.clip((x - e0) / (e1 - e0), 0.0, 1.0)
    return t * t * (3.0 - 2.0 * t)


def _flank_run(rise, max_deg=32.0):
    """Horizontal run needed to climb `rise` without exceeding `max_deg`.

    Mesa flanks use a smoothstep blend, whose *peak* gradient at its midpoint is 1.5x its
    average -- hence the factor below.
    """
    return max(1.0, 1.5 * abs(rise) / math.tan(math.radians(max_deg)))


# --------------------------------------------------------------------------------------
# Design data
# --------------------------------------------------------------------------------------

# --- spawn safe zones -----------------------------------------------------------------
# Flat pockets on the terraces at the SW / NE corners of the map.  Each is cut into the
# corner of the boundary ridge, with the outermost OUTER_SHELL forced to solid ridge so a
# pocket can never become an exit through the map border.
SPAWN_CENTER = (-150.0, -150.0)          # SW team; NE = mirror_point(this)
SPAWN_POCKET_R = 72.0                    # flattened pocket radius (m)
SPAWN_GROUND = 26.0                      # pocket floor (bottom of the mid tier)
SPAWN_GRID_DX = 26.0                     # spawn-point spacing along the spawn axis
SPAWN_GRID_DZ = 58.0                     # spawn-point spacing across the spawn axis
SPAWN_CLEAR_R = 112.0                    # radius kept free of full cover

SPAWNS = [
    dict(id="SPAWN_SW", team="SW", center=SPAWN_CENTER, ground=SPAWN_GROUND,
         n_points=8, layout=(4, 2), flatten_r=SPAWN_POCKET_R),
    dict(id="SPAWN_NE", team="NE", center=mirror_point(SPAWN_CENTER), ground=SPAWN_GROUND,
         n_points=8, layout=(4, 2), flatten_r=SPAWN_POCKET_R),
]

# --- objectives: equilateral triangle, side ~165 m, mirror-symmetric about s = 0 -------
# A sits on the valley axis (s = 0) so it is exactly equidistant from both spawns; B and C
# are the two home objectives (a mirror pair, hence identical tops).
OBJECTIVES = [
    dict(id="A", name="Central bridgehead (main)", pos=(67.0, -67.0),
         top=42.0, plateau=(40.0, 40.0), r=30.0, n_spawns=6, spawn_layout=(3, 2),
         note="neutral main objective: a mesa rising out of the dry riverbed, on the mirror axis"),
    dict(id="B", name="South-west gate", pos=(-92.0, -25.0),
         top=35.0, plateau=(40.0, 40.0), r=30.0, n_spawns=6, spawn_layout=(3, 2),
         note="SW team home objective on the south terrace; mirror of C"),
    dict(id="C", name="North-east gate", pos=(25.0, 92.0),
         top=35.0, plateau=(40.0, 40.0), r=30.0, n_spawns=6, spawn_layout=(3, 2),
         note="NE team home objective on the north terrace; mirror of B"),
]

# --- reachable high ground ------------------------------------------------------------
# Mirror pair, both on the valley shoulders (|s| ~ 99 m), 150 m from their own spawn and
# 250 m from the enemy's -- contestable rather than a home camping spot.
#
# They are deliberately STEEP cones (cliff flanks) whose tops are protected from the
# walkability relaxation; access is via the 2 attack ramps + 1 retreat ramp below.  That
# matches the design brief ("high ground reachable by >= 2 attack routes + 1 retreat")
# and is why the ramps are cut as flat-floored corridors rather than relying on the flank.
# Tops are equal (76 m) because H1 and H2 are a mirror pair.
HIGH_GROUNDS = [
    dict(id="H1", name="South valley shoulder", pos=(0.0, -140.0), top=66.0, r=38.0,
         routes=[
             dict(id="H1_R1", kind="attack", poly=[(-70.0, -70.0), (0.0, -140.0)],
                  slope_deg=28.0),
             dict(id="H1_R2", kind="attack", poly=[(90.0, -60.0), (0.0, -140.0)],
                  slope_deg=30.0),
             dict(id="H1_R3", kind="retreat", poly=[(0.0, -140.0), (-110.0, -30.0)],
                  slope_deg=19.0),
         ]),
    dict(id="H2", name="North valley shoulder", pos=(140.0, 0.0), top=66.0, r=38.0,
         routes=[
             dict(id="H2_R1", kind="attack", poly=[(70.0, 70.0), (140.0, 0.0)],
                  slope_deg=28.0),
             dict(id="H2_R2", kind="attack", poly=[(60.0, -90.0), (140.0, 0.0)],
                  slope_deg=30.0),
             dict(id="H2_R3", kind="retreat", poly=[(140.0, 0.0), (30.0, 110.0)],
                  slope_deg=19.0),
         ]),
]
RAMP_HALF_WIDTH = 16.0     # m -- walkable half-width carved for a ramp
HG_FLAT_R = 22.0           # radius of a bastion's flat top

# --- central butte + the pass through it ----------------------------------------------
# The butte is the tallest thing in the valley and blocks the spawn-to-spawn sightline
# (that ray runs along t = 0, straight through the origin).  The valley road (along s = 0)
# crosses it via a 30 m pass: 30 m > the 27.8 m spawn eye height, so the pass does not
# reopen the sightline.
BUTTE = dict(id="BUTTE", name="Central butte", pos=(0.0, 0.0), top=44.0, r=40.0,
             core_r=26.0)
BUTTE_PASS = dict(id="BPASS", name="Butte pass",
                  poly=[(-75.0, 75.0), (0.0, 0.0), (75.0, -75.0)], floor=32.0,
                  half_width=15.0)

# --- three primary lanes + the valley road --------------------------------------------
# Every lane is M-invariant as a set (endpoints are the spawn pair, and A / B,C are mirror
# fixed / mirror partners), so both teams traverse the identical routes.
LANES = [
    dict(id="L_MID", name="Centre run (tunnel)", kind="mid", width=15.0,
         poly=[SPAWN_CENTER, (0.0, 0.0), mirror_point(SPAWN_CENTER)],
         note="15 m chokepoint along the spawn axis, bored under the central butte (TUN_00)"),
    dict(id="L_NORTH", name="Main assault (via A)", kind="main", width=25.0,
         poly=[SPAWN_CENTER, OBJECTIVES[0]["pos"], mirror_point(SPAWN_CENTER)],
         note="25 m main axis descending into the valley through neutral objective A"),
    dict(id="L_SOUTH", name="Terrace flank (via B/C)", kind="flank", width=40.0,
         poly=[SPAWN_CENTER, OBJECTIVES[1]["pos"], OBJECTIVES[2]["pos"],
               mirror_point(SPAWN_CENTER)],
         note="40 m open flank over both terraces, linking the two home objectives"),
    dict(id="L_VALLEY", name="Valley road", kind="valley", width=30.0,
         poly=[(-190.0, 190.0), (0.0, 0.0), (190.0, -190.0)],
         note="30 m road along the dry riverbed, NW corner -> butte pass -> SE corner"),
]
ALL_LANES = LANES

# --- bridges over the dry riverbed ----------------------------------------------------
# Placed where a lane crosses the valley floor (s = 0).  All lie on s = 0, hence are
# mirror-fixed.
BRIDGES = [
    dict(id="BRG_01", name="Lower crossing", pos=(33.5, -33.5), width=13.0, span=70.0),
    dict(id="BRG_02", name="A bridgehead", pos=(67.0, -67.0), width=16.0, span=90.0),
    dict(id="BRG_03", name="Terrace flank bridge", pos=(-33.5, 33.5), width=13.0,
         span=70.0),
]

# --- tunnels (mirror pairs) -----------------------------------------------------------
# TUN_00 is mirror-fixed (it lies on the spawn axis, under the butte).  TUN_01 / TUN_02 are
# a mirror pair, each tunnelling into its own shoulder from the valley side.
TUNNELS = [
    dict(id="TUN_00", name="Central tunnel", p0=(-30.0, -30.0), p1=(30.0, 30.0),
         level=14.0, clear=5.0),
    dict(id="TUN_01", name="South shoulder tunnel", p0=(-46.0, -120.0), p1=(46.0, -120.0),
         level=24.0, clear=5.0),
    dict(id="TUN_02", name="North shoulder tunnel", p0=(120.0, 46.0), p1=(120.0, -46.0),
         level=24.0, clear=5.0),
]

# --- ridge notches: intentional long sightlines (mirror pairs) ------------------------
NOTCHES = [
    dict(id="NR_1", name="North ridge notch", mouth=(-30.0, 205.0), target=(-30.0, 10.0),
         width=12.0),
    dict(id="WR_1", name="West ridge notch", mouth=(-205.0, 30.0), target=(-10.0, 30.0),
         width=12.0),
    dict(id="SR_1", name="South ridge notch", mouth=(60.0, -205.0), target=(60.0, -25.0),
         width=10.0),
    dict(id="ER_1", name="East ridge notch", mouth=(205.0, -60.0), target=(25.0, -60.0),
         width=12.0),
]

# --- boundary lakes: the only places allowed to sit at the 0 m floor ------------------
# Both are mirror-fixed (they lie on s = 0), one at each end of the dry valley.
LAKES = [
    dict(id="LAKE_NW", name="North-west boundary lake", pos=(-222.0, 222.0), r=62.0),
    dict(id="LAKE_SE", name="South-east boundary lake", pos=(222.0, -222.0), r=62.0),
]

# --- cover belts ----------------------------------------------------------------------
COVER_BELTS = [
    dict(id="CB_MID", name="Centre run belt", kind="lane", width=26.0, poly=LANES[0]["poly"]),
    dict(id="CB_NORTH", name="Main assault belt", kind="lane", width=40.0, poly=LANES[1]["poly"]),
    dict(id="CB_SOUTH", name="Terrace flank belt", kind="lane", width=54.0, poly=LANES[2]["poly"]),
    dict(id="CB_VALLEY", name="Valley road belt", kind="valley", width=36.0,
         poly=LANES[3]["poly"]),
    dict(id="CB_A", name="A defensive ring", kind="ring", width=64.0, ring=(67.0, -67.0, 32.0)),
    dict(id="CB_B", name="B defensive ring", kind="ring", width=56.0, ring=(-92.0, -25.0, 28.0)),
    dict(id="CB_C", name="C defensive ring", kind="ring", width=56.0, ring=(25.0, 92.0, 28.0)),
    dict(id="CB_H1", name="H1 shoulder ring", kind="ring", width=60.0, ring=(0.0, -140.0, 30.0)),
    dict(id="CB_H2", name="H2 shoulder ring", kind="ring", width=60.0, ring=(140.0, 0.0, 30.0)),
]

# --- cover density targets (step0.md / step4.md) --------------------------------------
COVER_HALF_HEIGHT = (0.9, 1.3)     # m
COVER_FULL_HEIGHT = (1.8, 2.5)     # m
COVER_HALF_RADIUS = 15.0           # m -- half cover required within this
COVER_FULL_RADIUS = 40.0           # m -- full cover required within this
COVER_HALF_GRID = 17.0             # m -- placement spacing (max dist to a grid point 12.0)
COVER_FULL_GRID = 30.0             # m -- placement spacing (max dist to a grid point 21.2)

# --- vehicles (mirror pairs) ----------------------------------------------------------
VEHICLES = [
    dict(id="VEH_SW_1", pos=(-118.0, -150.0), team="SW", type="light_transport"),
    dict(id="VEH_SW_2", pos=(-150.0, -118.0), team="SW", type="light_transport"),
    dict(id="VEH_NE_1", pos=(150.0, 118.0), team="NE", type="light_transport"),
    dict(id="VEH_NE_2", pos=(118.0, 150.0), team="NE", type="light_transport"),
    dict(id="VEH_N_1", pos=(-30.0, 30.0), team="neutral", type="light_transport"),
    dict(id="VEH_N_2", pos=(30.0, -30.0), team="neutral", type="light_transport"),
]

# --- teleport pads (mirror pairs) -----------------------------------------------------
TELEPORTS = [
    dict(id="TP_SW_A", pair="SW", pos=(-140.0, -160.0), role="spawn"),
    dict(id="TP_SW_B", pair="SW", pos=(-92.0, -25.0), role="home_objective"),
    dict(id="TP_NE_A", pair="NE", pos=(160.0, 140.0), role="spawn"),
    dict(id="TP_NE_B", pair="NE", pos=(25.0, 92.0), role="home_objective"),
]
TELEPORT_COOLDOWN_S = 12.0
TELEPORT_RADIUS = 3.0


# --------------------------------------------------------------------------------------
# Spawn point generation
# --------------------------------------------------------------------------------------
def spawn_points(spawn):
    """The n spawn-point positions: a cols x rows grid on the spawn/t axes, so the pattern
    is symmetric about the spawn axis."""
    cols, rows = spawn["layout"]
    cx, cz = spawn["center"]
    us = (np.arange(cols) - (cols - 1) / 2.0) * SPAWN_GRID_DX      # along s
    ut = (np.arange(rows) - (rows - 1) / 2.0) * SPAWN_GRID_DZ      # along t
    pts = []
    for u in us:
        for v in ut:
            # s unit vector = (1,1)/sqrt2 ; t unit vector = (1,-1)/sqrt2
            x = cx + (u + v) / SQ2
            z = cz + (u - v) / SQ2
            pts.append((round(x, 1), round(z, 1)))
    return pts


def spawn_bbox(spawn):
    pts = np.asarray(spawn_points(spawn), dtype=float)
    return (float(pts[:, 0].min()), float(pts[:, 0].max()),
            float(pts[:, 1].min()), float(pts[:, 1].max()))


# --------------------------------------------------------------------------------------
# Raster masks
# --------------------------------------------------------------------------------------
def mask_outside_play(X, Z):
    return (np.abs(X) > PLAY_HALF) | (np.abs(Z) > PLAY_HALF)


def mask_spawn_pockets(X, Z):
    m = np.zeros(X.shape, dtype=bool)
    for sp in SPAWNS:
        m |= disc_mask(X, Z, sp["center"][0], sp["center"][1], sp["flatten_r"])
    return m


def mask_spawn_clear(X, Z):
    """Area kept free of enemy-exploitable full cover (stage 4 constraint)."""
    m = np.zeros(X.shape, dtype=bool)
    for sp in SPAWNS:
        m |= disc_mask(X, Z, sp["center"][0], sp["center"][1], SPAWN_CLEAR_R)
    return m


def mask_lakes(X, Z):
    m = np.zeros(X.shape, dtype=bool)
    for lk in LAKES:
        m |= disc_mask(X, Z, lk["pos"][0], lk["pos"][1], lk["r"])
    return m


def mask_outer_shell(X, Z):
    """Outermost band, kept solid ridge so a spawn pocket cannot breach the map border.
    Lake discs are excluded -- the water must be able to sit at the 0 m floor."""
    band = (np.abs(X) > HALF - OUTER_SHELL) | (np.abs(Z) > HALF - OUTER_SHELL)
    return band & ~mask_lakes(X, Z)


def mask_ring(X, Z):
    """Impassable boundary ridge: outside the play area, minus the spawn pockets, minus
    the lakes, plus the always-solid outer shell."""
    carve = mask_outside_play(X, Z) & ~mask_spawn_pockets(X, Z) & ~mask_lakes(X, Z)
    return carve | mask_outer_shell(X, Z)


def mask_butte(X, Z):
    return disc_mask(X, Z, BUTTE["pos"][0], BUTTE["pos"][1], BUTTE["r"])


def mask_butte_core(X, Z):
    return disc_mask(X, Z, BUTTE["pos"][0], BUTTE["pos"][1], BUTTE["core_r"])


def mask_butte_pass(X, Z):
    return poly_dist(X, Z, BUTTE_PASS["poly"]) <= BUTTE_PASS["half_width"]


def mask_bastion_cones(X, Z):
    """The steep (protected) part of every high-ground shoulder."""
    m = np.zeros(X.shape, dtype=bool)
    for hg in HIGH_GROUNDS:
        m |= disc_mask(X, Z, hg["pos"][0], hg["pos"][1], hg["r"])
    return m


def mask_water(X, Z):
    return mask_lakes(X, Z)


def mask_play_area(X, Z):
    """Playable combat area (includes the spawn pockets carved into the ring)."""
    return (~mask_outside_play(X, Z)) | mask_spawn_pockets(X, Z)


def mask_no_cover(X, Z):
    """Terrain that cannot carry cover: the ridge, the lakes, the butte cliffs, the steep
    shoulder cones, and the steep ring of every objective mesa."""
    m = mask_ring(X, Z) | mask_lakes(X, Z) | mask_butte_core(X, Z) | mask_bastion_cones(X, Z)
    return m


def mask_high_ground(X, Z):
    m = mask_bastion_cones(X, Z)
    m |= mask_butte(X, Z)
    return m


def mask_notch(X, Z):
    m = np.zeros(X.shape, dtype=bool)
    for nt in NOTCHES:
        m |= poly_dist(X, Z, [nt["mouth"], nt["target"]]) <= nt["width"] / 2.0
    return m


def mask_cover_belt(X, Z):
    m = np.zeros(X.shape, dtype=bool)
    for cb in COVER_BELTS:
        if cb["kind"] == "ring":
            cx, cz, r = cb["ring"]
            m |= disc_mask(X, Z, cx, cz, r)
        else:
            m |= poly_dist(X, Z, cb["poly"]) <= cb["width"] / 2.0
    return m


def mask_lane(X, Z, lane, extra=0.0):
    return poly_dist(X, Z, lane["poly"]) <= lane["width"] / 2.0 + extra


# --------------------------------------------------------------------------------------
# Design-intent elevation field
# --------------------------------------------------------------------------------------
def intent_landscape(X, Z):
    """The landscape layers of the design-intent field.

    Ordering matters and is deliberate: **every mesa flank is sized to stay walkable, and
    the objective mesas are applied last so they win** where an apron would otherwise
    swallow them (a 66 m shoulder's legal apron reaches ~120 m, far enough to overwrite a
    42 m objective if the order were reversed).

    Layers:
    1. dry river valley along s = 0, rising with |s| onto the two mid terraces
    2. high-ground shoulders, each with a walkable apron sized from its *local* rise
    3. central butte, carrying the 30 m pass
    4. boundary ridge ring, crest outside the play area
    5. boundary lakes and their shore
    6. objective mesas, last so their designed tops always win

    Corridors and the walkability relaxation are applied afterwards by `intent_height`.
    """
    S = np.abs(s_of(X, Z))

    # 1. valley -> terraces
    h = VALLEY_FLOOR + (TERRACE_TOP - VALLEY_FLOOR) * smoothstep(VALLEY_WALL_S,
                                                                TERRACE_RAMP_S, S)
    # 2. high-ground shoulders, apron sized from the local terrace height they rise from
    for hg in HIGH_GROUNDS:
        px, pz = hg["pos"]
        base = float(h[int(np.clip(round(HALF - pz), 0, h.shape[0] - 1)),
                       int(np.clip(round(px + HALF), 0, h.shape[1] - 1))])
        apron = HG_APRON_FACTOR * max(1.0, hg["top"] - base) / math.tan(
            math.radians(FLANK_MAX_DEG))
        d = np.hypot(X - px, Z - pz)
        h = h + (hg["top"] - h) * smoothstep(HG_FLAT_R + apron, HG_FLAT_R, d)

    # 3. central butte
    dbu = np.hypot(X - BUTTE["pos"][0], Z - BUTTE["pos"][1])
    h = h + (BUTTE["top"] - h) * smoothstep(BUTTE["r"], BUTTE["core_r"], dbu)

    # 4. boundary ridge: the blend is 0 at the play edge, so terrain is continuous there
    # and the whole climb to the crest happens outside the play area.
    edge = np.maximum(np.abs(X), np.abs(Z))
    s = smoothstep(PLAY_HALF, HALF - 5.0, edge)
    ridge = h * (1.0 - s) + RIDGE_TOP * s
    ridge_here = (edge > PLAY_HALF) & ~mask_spawn_pockets(X, Z) & ~mask_lakes(X, Z)
    h = np.where(ridge_here, ridge, h)
    h = np.where(mask_outer_shell(X, Z), np.maximum(h, RIDGE_TOP), h)

    # 5. lakes: smooth shore descent finishing at the 0 m floor
    for lk in LAKES:
        d = np.hypot(X - lk["pos"][0], Z - lk["pos"][1])
        f = 1.0 - smoothstep(lk["r"], lk["r"] + LAKE_SHORE_M, d)
        h = h * (1.0 - f)

    # 6. objective mesas last, so their designed tops win over any shoulder apron
    for ob in OBJECTIVES:
        d = np.hypot(X - ob["pos"][0], Z - ob["pos"][1])
        flat_r = ob["plateau"][0] * 0.6
        flank = _flank_run(ob["top"] - VALLEY_FLOOR)
        f = 1.0 - smoothstep(flat_r, flat_r + flank, d)
        h = h * (1.0 - f) + ob["top"] * f
    return h


def _sample_polyline_frac(poly, step=2.0):
    """Sample a polyline at ~`step` spacing; also return the fractional position within the
    current segment, so a piecewise-linear control-height profile can be evaluated."""
    L = polyline_length(poly)
    n = max(2, int(round(L / step)))
    out = []
    for k in range(n + 1):
        d = L * k / n
        acc = 0.0
        seg_i, u, p = len(poly) - 2, 0.0, poly[-1]
        for i in range(len(poly) - 1):
            sl = math.dist(poly[i], poly[i + 1])
            if acc + sl >= d or i == len(poly) - 2:
                u = min(1.0, (d - acc) / sl) if sl > 0 else 0.0
                seg_i = i
                p = (poly[i][0] + u * (poly[i + 1][0] - poly[i][0]),
                     poly[i][1] + u * (poly[i + 1][1] - poly[i][1]))
                break
            acc += sl
        out.append((d, p[0], p[1], seg_i, u))
    return out


def _clamp_profile(hs, step, max_deg):
    """Clamp a 1-D profile so it never rises faster than `max_deg` (forward + backward
    sweep).  The result is the lowest profile within the gradient limit of the original."""
    hs = np.asarray(hs, dtype=float).copy()
    max_rise = math.tan(math.radians(max_deg)) * step
    for i in range(1, len(hs)):
        hs[i] = min(hs[i], hs[i - 1] + max_rise)
    for i in range(len(hs) - 2, -1, -1):
        hs[i] = min(hs[i], hs[i + 1] + max_rise)
    return hs


def intent_baseline(X, Z):
    """The walkable baseline terrain: dry valley, the two mid terraces, and the objective
    mesas -- but *without* the steep high-ground shoulders or the butte.

    This is what corridor control heights are sampled from.  Sampling the full landscape
    instead would start a shoulder ramp at the cone's own height, so the ramp would begin
    half-way up the cliff and its profile would contain the cliff step.  Starting from the
    baseline makes a shoulder ramp a genuine climb from low ground, which the corridor then
    carves through the cone as a flat-floored cutting.
    """
    S = np.abs(s_of(X, Z))
    h = VALLEY_FLOOR + (TERRACE_TOP - VALLEY_FLOOR) * smoothstep(VALLEY_WALL_S,
                                                                TERRACE_RAMP_S, S)
    for ob in OBJECTIVES:
        d = np.hypot(X - ob["pos"][0], Z - ob["pos"][1])
        flat_r = ob["plateau"][0] * 0.6
        flank = _flank_run(ob["top"] - VALLEY_FLOOR)
        f = 1.0 - smoothstep(flat_r, flat_r + flank, d)
        h = h * (1.0 - f) + ob["top"] * f
    return h


def corridor_control_heights(poly):
    """Designed elevation at each polyline vertex.

    Pinned features, in priority order: spawn pocket floor, objective plateau, butte pass
    floor, high-ground shoulder top.  Everything else takes `intent_baseline` (walkable
    terrain), so a ramp climbs from genuine low ground rather than from the flank it is cut
    into -- see `intent_baseline`.

    The butte pass is handled here rather than as a corridor of its own.  The valley road
    already runs along s = 0 and crosses the butte, so adding a second corridor with the
    same centreline made every cell alternate between the two profiles (their normalized
    distances trade places sample by sample), which produced a 40 m sawtooth along the
    road.  Pinning the road's own vertex to the pass floor keeps one corridor per route.
    """
    out = []
    for vx, vz in poly:
        p = (vx, vz)
        val = None
        for sp in SPAWNS:
            if math.dist(p, sp["center"]) <= sp["flatten_r"] * 0.55:
                val = sp["ground"]
                break
        if val is None:
            for ob in OBJECTIVES:
                if math.dist(p, ob["pos"]) <= ob["plateau"][0] * 0.6:
                    val = ob["top"]
                    break
        if val is None:
            for hg in HIGH_GROUNDS:
                if math.dist(p, hg["pos"]) <= HG_FLAT_R * 1.05:
                    val = hg["top"]
                    break
        if val is None and float(poly_dist(np.array(vx), np.array(vz),
                                           BUTTE_PASS["poly"])) <= BUTTE_PASS["half_width"]:
            val = BUTTE_PASS["floor"]
        if val is None:
            val = float(intent_baseline(np.array(vx), np.array(vz)))
        out.append(val)
    return out


def corridor_target(X, Z, poly, heights, half_width, max_deg=34.0, step=2.0, blend=10.0):
    """Per-cell corridor target and *normalized* distance.

    Returns (target, d_over_hw):

    * `target` is the corridor's designed profile at the cell's nearest corridor point.
    * `d_over_hw` is the distance to the corridor centreline divided by its own half
      width -- 0 on the centreline, 1 at the walkable edge.  Normalizing by the corridor's
      own width is what makes two overlapping corridors comparable: raw distance would let
      a wide lane outrank a narrow ramp everywhere they cross, and the ramp would lose its
      own surface to the lane.

    The caller resolves overlaps by taking the smallest `d_over_hw` (see `intent_height`);
    a weight tie (both cores at 1.0) must resolve consistently, which raw distance cannot
    do.
    """
    from scipy.spatial import cKDTree

    assert len(heights) == len(poly), "control heights must match polyline vertices"
    samples = _sample_polyline_frac(poly, step)
    prof = np.array([heights[si] + (heights[min(si + 1, len(heights) - 1)] - heights[si]) * u
                     for _, _, _, si, u in samples])
    prof = _clamp_profile(prof, step, max_deg)

    pts = np.array([(x, z) for _, x, z, _, _ in samples])
    tree = cKDTree(pts)
    q = np.column_stack([X.ravel(), Z.ravel()])
    dist, idx = tree.query(q)
    d_over_hw = (dist.reshape(X.shape) / max(half_width, 1e-9))
    target = prof[idx].reshape(X.shape)
    return target, d_over_hw


def corridor_weight(d_over_hw, half_width, blend=10.0):
    """Lateral falloff from a normalized distance: exactly 1 across the whole walkable
    width, then a falloff over `blend` metres forming the corridor's side walls."""
    return np.where(d_over_hw <= 1.0, 1.0,
                    1.0 - smoothstep(1.0, 1.0 + blend / max(half_width, 1e-9), d_over_hw))


def mask_designed_flats(X, Z):
    """The flat tops of every designed mesa / shoulder.

    These are protected from the walkability relaxation.  Without protection the
    relaxation shaves them: a 76 m shoulder can only be reached at a legal grade if a long
    legal ramp already exists, and the relaxation (which only ever lowers) would otherwise
    pull the top down to whatever the surrounding terrain allows.  The designed plateaus
    are intent, not artifact, so they are held fixed and the *routes to them* are what the
    relaxation has to make legal.
    """
    m = np.zeros(X.shape, dtype=bool)
    for ob in OBJECTIVES:
        flat_r = ob["plateau"][0] * 0.6
        m |= disc_mask(X, Z, ob["pos"][0], ob["pos"][1], flat_r)
    for hg in HIGH_GROUNDS:
        m |= disc_mask(X, Z, hg["pos"][0], hg["pos"][1], HG_FLAT_R)
    m |= mask_spawn_pockets(X, Z)
    m |= mask_butte_pass(X, Z)
    return m


def enforce_slope_limit(h, max_deg=SLOPE_LIMIT_DEG, cell=1.0, max_iter=200, tol=1e-3,
                        protect=None):
    """Relax a height field until no adjacent-cell rise exceeds `max_deg`.

    Each directional sweep is sequential along its axis, so the constraint propagates the
    full length of the grid in one pass; alternating the four directions handles the 2-D
    coupling.  Every operation only ever *lowers* a cell, so the relaxation cannot invent
    peaks and a designed flat survives wherever a legal route to it exists.

    `protect` marks cells that are never lowered: the boundary ring (impassable by design),
    the central butte cliffs, and the steep high-ground cones (deliberately steep, with
    flat-floored ramps cut through them).  Protecting those is what lets the relaxation
    make the *rest* of the map walkable without eroding the designed silhouettes.

    The per-step rise is divided by sqrt(2) because the sweeps only constrain the four axis
    directions: a cell relaxed to the axis limit in *both* x and z has a diagonal gradient
    of sqrt(2)x that limit.  Without the factor the field converges to a true 35 deg on the
    axes but 44.7 deg diagonally -- which is exactly the artefact that kept the walkable
    fraction pinned near 77%.
    """
    h = np.asarray(h, dtype=float).copy()
    keep = np.zeros(h.shape, dtype=bool) if protect is None else np.asarray(protect)
    max_rise = math.tan(math.radians(max_deg)) * cell / SQ2

    for _ in range(max_iter):
        before = h.max()
        for axis in (0, 1):
            hs = h if axis == 0 else h.T
            ks = keep if axis == 0 else keep.T
            for direction in (1, -1):
                rng = (range(1, hs.shape[0]) if direction == 1
                       else range(hs.shape[0] - 2, -1, -1))
                for i in rng:
                    lim = hs[i - direction] + max_rise
                    m = (hs[i] > lim) & ~ks[i]
                    if m.any():
                        hs[i] = np.where(m, lim, hs[i])
            if axis == 1:
                h = hs.T
        if before - h.max() < tol:
            break
    return h


def intent_height(X, Z):
    """Full design-intent elevation field, composed as:

        landscape -> designed corridors -> walkability flats -> slope relaxation

    Corridors are resolved per cell by taking the **single highest-weight** corridor,
    rather than blending them one after another.  Sequential blending is wrong here: where
    two corridors overlap, the later one leaves a partial blend of the earlier one's flank
    behind, producing a steep scar exactly inside a route that is supposed to be walkable
    (observed as 70 deg steps on the centre run).  Picking the dominant corridor per cell
    keeps every route's own profile intact.

    The relaxation then makes the rest of the play area walkable, with the designed tops
    held fixed so their silhouettes survive.

    This is the "designed level, not random noise" target that stage 1 blends its FBM
    toward.
    """
    h = intent_landscape(X, Z)
    pass_mask = mask_butte_pass(X, Z)

    corridors = [(ln["poly"], ln["width"] * 0.6) for ln in ALL_LANES]
    for hg in HIGH_GROUNDS:
        for r in hg["routes"]:
            corridors.append((r["poly"], RAMP_HALF_WIDTH))

    blocked = mask_ring(X, Z) | mask_lakes(X, Z) | (mask_butte_core(X, Z) & ~pass_mask)

    # Resolve corridor overlaps by the smallest *normalized* distance (see
    # `corridor_target`): the cell takes the profile of whichever corridor it is most
    # central to.  Taking the largest weight instead would tie at 1.0 for two overlapping
    # cores and then always keep the first one, so a ramp crossing a lane would inherit
    # the lane's profile along part of its length -- which showed up as a 29 -> 47 -> 34 ->
    # 63 m oscillation on H1_R1.
    best_d = np.full(X.shape, np.inf)
    best_t = np.zeros(X.shape)
    best_hw = np.ones(X.shape)
    for poly, hw in corridors:
        ctrl = corridor_control_heights(poly)
        tgt, dov = corridor_target(X, Z, poly, ctrl, hw)
        dov = np.where(blocked, np.inf, dov)
        take = dov < best_d
        best_d = np.where(take, dov, best_d)
        best_t = np.where(take, tgt, best_t)
        best_hw = np.where(take, hw, best_hw)

    best_w = corridor_weight(best_d, 1.0, blend=10.0)
    h = h * (1.0 - best_w) + best_t * best_w

    # walkability flats: spawn pockets
    for sp in SPAWNS:
        d = np.hypot(X - sp["center"][0], Z - sp["center"][1])
        f = 1.0 - smoothstep(sp["flatten_r"] * 0.55, sp["flatten_r"], d)
        h = h * (1.0 - f) + sp["ground"] * f

    # Protections from the relaxation: the impassable boundary ridge, the butte cliffs
    # (outside the pass the valley road runs through), every designed flat top / bay, and
    # the flat core of every corridor.  The corridor cores are already at their designed
    # legal profile; protecting them stops the relaxation from shaving a legal ramp back
    # into the steep apron it was cut through.
    protect = (mask_ring(X, Z)
               | (mask_butte_core(X, Z) & ~pass_mask)
               | mask_designed_flats(X, Z)
               | (best_w >= 0.999))
    h = enforce_slope_limit(h, protect=protect)
    return np.clip(h, HEIGHT_MIN, HEIGHT_MAX)


def elevation_profile(poly, n=200):
    """Sampled designed elevation along a route."""
    out = []
    for d, x, z, _, _ in _sample_polyline_frac(poly, polyline_length(poly) / n):
        out.append((round(d, 1), float(intent_height(np.array(x), np.array(z)))))
    return out


# --------------------------------------------------------------------------------------
# Symmetric cover plan + desert fill
# --------------------------------------------------------------------------------------
def _symmetrise(points):
    out = {}
    for p in points:
        out[(round(p[0], 3), round(p[1], 3))] = True
        q = mirror_point(p)
        out[(round(q[0], 3), round(q[1], 3))] = True
    return [(a, b) for a, b in out]


def _sym_jitter(x, z, amp):
    """Deterministic jitter invariant under M: the hash is taken over the mirror-canonical
    copy of the cell and the offset reflected back, so the point placed for cell p is
    exactly the mirror of the point placed for cell M(p).  Keeps symmetry without
    introducing near-duplicate partners."""
    mx, mz = mirror_point((x, z))
    flipped = (mx, mz) < (x, z)
    cx, cz = (mx, mz) if flipped else (x, z)
    h1 = math.sin(cx * 12.9898 + cz * 78.233) * 43758.5453
    h2 = math.sin(cx * 39.3468 + cz * 11.135) * 24634.6345
    ox = ((h1 - math.floor(h1)) - 0.5) * amp
    oz = ((h2 - math.floor(h2)) - 0.5) * amp
    if flipped:
        ox, oz = -oz, -ox
    return x + ox, z + oz


def plan_cover_points(X, Z, spacing, radius, walk_mask, seed_offset=(0.0, 0.0)):
    """Place cover on a mirror-symmetric jittered grid, then greedily fill every remaining
    'cover desert' (walkable cell further than `radius` from the nearest cover).

    Returns (points, desert_pixels, coverage_pct).  Placement is exactly mirror-symmetric,
    so both teams face identical cover.
    """
    from scipy.spatial import cKDTree

    no_cover = mask_no_cover(X, Z)
    walk = walk_mask & ~no_cover
    ys, xs = np.nonzero(walk)
    q = np.column_stack([X[ys, xs], Z[ys, xs]])

    n = int(math.ceil(MAP_SIZE / spacing)) + 2
    pts = []
    for i in range(-n, n + 1):
        for j in range(-n, n + 1):
            x = seed_offset[0] + i * spacing
            z = seed_offset[1] + j * spacing
            if abs(x) > HALF or abs(z) > HALF:
                continue
            px, pz = _sym_jitter(x, z, spacing * 0.45)
            if mask_no_cover(np.array(px), np.array(pz)):
                continue
            pts.append((px, pz))

    for _ in range(24):
        tree = cKDTree(np.asarray(pts, dtype=float))
        d, _ = tree.query(q)
        bad = d > radius
        if not bad.any():
            break
        order = np.argsort(-d[bad])
        cells = q[bad][order]
        added = []
        for c in cells:
            if all(math.dist(c, a) > spacing * 0.9 for a in added):
                added.append(c)
            if len(added) >= 60:
                break
        pair = []
        for c in added:
            pair.append(tuple(c))
            pair.append(mirror_point(tuple(c)))
        pts.extend(_symmetrise(pair))

    tree = cKDTree(np.asarray(pts, dtype=float))
    d, _ = tree.query(q)
    ok = d <= radius
    return _symmetrise(pts), int((~ok).sum()), float(100.0 * ok.mean())


def verify_cover(X, Z, points, radius, walk_mask):
    from scipy.spatial import cKDTree

    walk = walk_mask & ~mask_no_cover(X, Z)
    ys, xs = np.nonzero(walk)
    q = np.column_stack([X[ys, xs], Z[ys, xs]])
    tree = cKDTree(np.asarray(points, dtype=float))
    d, _ = tree.query(q)
    ok = d <= radius
    desert = np.zeros(X.shape, dtype=bool)
    desert[ys[~ok], xs[~ok]] = True
    return desert, float(100.0 * ok.mean()), int(ok.sum()), int(ok.size)


# --------------------------------------------------------------------------------------
# Metrics / self-checks
# --------------------------------------------------------------------------------------
def triple_distances():
    a, b, c = (o["pos"] for o in OBJECTIVES)
    return {
        "A_main_to_B": round(math.dist(a, b), 1),
        "B_to_C": round(math.dist(b, c), 1),
        "C_to_A_main": round(math.dist(c, a), 1),
    }


def spawn_zone_metrics():
    out = {}
    for sp in SPAWNS:
        x0, x1, z0, z1 = spawn_bbox(sp)
        pts = spawn_points(sp)
        out[sp["id"]] = {
            "team": sp["team"],
            "center": [round(sp["center"][0], 1), round(sp["center"][1], 1)],
            "bbox_m": [round(x1 - x0, 1), round(z1 - z0, 1)],
            "bbox_requirement_min_m": [80.0, 60.0],
            "bbox_pass": bool((x1 - x0) >= 80.0 and (z1 - z0) >= 60.0),
            "spawn_points": len(pts),
            "layout": f"{sp['layout'][0]}x{sp['layout'][1]}",
            "ground_level_m": sp["ground"],
            "points": pts,
        }
    return out


def sightlines():
    out = []
    for nt in NOTCHES:
        L = math.dist(nt["mouth"], nt["target"])
        out.append(dict(id=nt["id"], name=nt["name"], width_m=nt["width"],
                        length_m=round(L, 1), mouth=list(nt["mouth"]),
                        target=list(nt["target"]), within_200m=bool(L <= 200.0)))
    return out


def lanes_metrics():
    out = []
    for ln in ALL_LANES:
        L = polyline_length(ln["poly"])
        out.append(dict(
            id=ln["id"], name=ln["name"], kind=ln["kind"], width_m=ln["width"],
            length_m=round(L, 1),
            traverse_sprint_s=round(L / SPRINT_SPEED, 1),
            traverse_vehicle_s=round(L / VEHICLE_SPEED, 1),
            note=ln["note"],
        ))
    return out


def team_access():
    out = {}
    for sp in SPAWNS:
        c = sp["center"]
        d = {o["id"]: round(math.dist(c, o["pos"]), 1) for o in OBJECTIVES}
        d.update({h["id"]: round(math.dist(c, h["pos"]), 1) for h in HIGH_GROUNDS})
        d["BUTTE"] = round(math.dist(c, BUTTE["pos"]), 1)
        out[sp["team"]] = d
    return out


def assert_symmetry(tol=1e-6):
    """Verify the layout is invariant under M, including equal heights for mirror pairs."""
    problems = []

    def chk(name, pts):
        for p in pts:
            q = mirror_point(p)
            if not any(math.dist(q, r) <= tol for r in pts):
                problems.append(f"{name}: {tuple(p)} -> {tuple(q)} has no partner")

    chk("objectives", [o["pos"] for o in OBJECTIVES])
    chk("spawn_centers", [s["center"] for s in SPAWNS])
    chk("spawn_points", [p for s in SPAWNS for p in spawn_points(s)])
    chk("bastions", [h["pos"] for h in HIGH_GROUNDS])
    chk("bastion_routes", [p for h in HIGH_GROUNDS for r in h["routes"] for p in r["poly"]])
    chk("tunnels", [t["p0"] for t in TUNNELS] + [t["p1"] for t in TUNNELS])
    chk("bridges", [b["pos"] for b in BRIDGES])
    chk("notches", [n["mouth"] for n in NOTCHES] + [n["target"] for n in NOTCHES])
    chk("lakes", [l["pos"] for l in LAKES])
    chk("butte", [BUTTE["pos"]])
    chk("vehicles", [v["pos"] for v in VEHICLES])
    chk("teleports", [t["pos"] for t in TELEPORTS])
    chk("cover_ring_centers", [c["ring"][:2] for c in COVER_BELTS if c["kind"] == "ring"])

    # mirror-pair features must have EQUAL designed heights, or the map is unfair
    for o in OBJECTIVES:
        m = mirror_point(o["pos"])
        for o2 in OBJECTIVES:
            if math.dist(m, o2["pos"]) <= tol and o2["top"] != o["top"]:
                problems.append(f"objective {o['id']} top {o['top']} != mirror "
                                f"{o2['id']} top {o2['top']}")
    for hg in HIGH_GROUNDS:
        m = mirror_point(hg["pos"])
        for hg2 in HIGH_GROUNDS:
            if math.dist(m, hg2["pos"]) <= tol and hg2["top"] != hg["top"]:
                problems.append(f"{hg['id']} top {hg['top']} != mirror {hg2['id']} "
                                f"top {hg2['top']}")
    for sp in SPAWNS:
        m = mirror_point(sp["center"])
        for sp2 in SPAWNS:
            if math.dist(m, sp2["center"]) <= tol and sp2["ground"] != sp["ground"]:
                problems.append(f"{sp['id']} ground {sp['ground']} != mirror "
                                f"{sp2['id']} ground {sp2['ground']}")

    # every lane must be M-invariant as a set (so both teams use the same routes)
    for ln in ALL_LANES:
        mp = mirror_poly(ln["poly"])
        same = all(math.dist(a, b) <= 1e-6 for a, b in zip(mp, ln["poly"]))
        rev = all(math.dist(a, b) <= 1e-6 for a, b in zip(reversed(mp), ln["poly"]))
        if not (same or rev):
            problems.append(f"lane {ln['id']} is not mirror-invariant: "
                            f"{ln['poly']} vs mirrored {mp}")

    for cb in COVER_BELTS:
        if cb["kind"] == "ring":
            q = mirror_point(cb["ring"][:2])
            if not any(math.dist(q, c["ring"][:2]) <= tol
                       for c in COVER_BELTS if c["kind"] == "ring"):
                problems.append(f"cover ring {cb['id']} has no mirror partner")
        else:
            mp = mirror_poly(cb["poly"])
            same = all(math.dist(a, b) <= 1e-6 for a, b in zip(mp, cb["poly"]))
            rev = all(math.dist(a, b) <= 1e-6 for a, b in zip(reversed(mp), cb["poly"]))
            if not (same or rev):
                problems.append(f"cover belt {cb['id']} is not mirror-invariant")

    # structural: a bastion must not sit on a lane, or lane grading would flatten it
    for hg in HIGH_GROUNDS:
        for ln in ALL_LANES:
            d = float(poly_dist(np.array(hg["pos"][0]), np.array(hg["pos"][1]), ln["poly"]))
            if d < hg["r"]:
                problems.append(f"{hg['id']} sits on lane {ln['id']} ({d:.1f} m)")

    # structural: the butte must be tall enough to block the spawn sightline, and the pass
    # must stay above the spawn eye height
    eye = SPAWN_GROUND + 1.8
    if BUTTE["top"] <= eye:
        problems.append(f"butte top {BUTTE['top']} does not clear the spawn eye {eye}")
    if BUTTE_PASS["floor"] <= eye:
        problems.append(f"butte pass floor {BUTTE_PASS['floor']} does not clear the "
                        f"spawn eye {eye}")

    # structural: spawn pockets must not touch a lake
    for sp in SPAWNS:
        for lk in LAKES:
            d = math.dist(sp["center"], lk["pos"])
            if d < sp["flatten_r"] + lk["r"]:
                problems.append(f"{sp['id']} overlaps {lk['id']} (centre distance {d:.1f} m)")
    return problems


def spawn_to_spawn_los_blocked():
    """The SW->NE spawn sightline runs along the spawn axis t = 0 straight through the
    origin, where the central butte stands."""
    eye = SPAWN_GROUND + 1.8
    return {
        "spawn_eye_height_m": round(eye, 2),
        "butte_top_m": BUTTE["top"],
        "butte_pass_floor_m": BUTTE_PASS["floor"],
        "blocked": bool(BUTTE["top"] > eye and BUTTE_PASS["floor"] > eye),
        "margin_m": round(BUTTE["top"] - eye, 2),
        "pass_margin_m": round(BUTTE_PASS["floor"] - eye, 2),
        "note": ("the SW->NE spawn sightline lies on the spawn axis and passes through the "
                 "central butte; the 30 m pass stays above the spawn eye line, so blocking "
                 "holds. 3-D confirmation with the real heightfield is re-run in stages 1/2."),
    }


def build_metrics(cover_half=None, cover_full=None, terrain_stats=None):
    m = {
        "stage": 0,
        "generated_by": "Terrainscripts/stage0_layout.py",
        "map": {
            "size_m": [MAP_SIZE, MAP_SIZE],
            "playable_m": [2 * PLAY_HALF, 2 * PLAY_HALF],
            "impassable_ring_m": RING,
            "height_range_m": [HEIGHT_MIN, HEIGHT_MAX],
            "units": "1 unit = 1 m",
            "raster": "1 px = 1 m (500 x 500)",
        },
        "axes": {
            "s": "valley axis (x+z)/sqrt2; s = 0 is the dry river valley (NW corner -> SE)",
            "t": "spawn axis (x-z)/sqrt2; t = 0 joins SPAWN_SW to SPAWN_NE",
            "mirror": "M(a,b)=(-b,-a) about s=0; flips s, preserves t; swaps the two spawns",
        },
        "elevation_tiers": {
            "lowland_0_15": "dry riverbed along s = 0 (floor 6 m)",
            "mid_terrace_25_45": f"terraces rising with |s|, top {TERRACE_TOP} m",
            "high_65_95": f"two valley shoulders, tops {HIGH_GROUNDS[0]['top']} m",
            "ridge_100_120": f"boundary ring, crest {RIDGE_TOP} m (impassable)",
        },
        "objectives": [
            dict(id=o["id"], name=o["name"], pos=list(o["pos"]), top_m=o["top"],
                 plateau_m=list(o["plateau"]), n_spawns=o["n_spawns"], note=o["note"])
            for o in OBJECTIVES
        ],
        "objective_triple_distances_m": triple_distances(),
        "objective_triangle_target_m": [120.0, 180.0],
        "spawn_zones": spawn_zone_metrics(),
        "long_sightlines": {
            "count": len(NOTCHES),
            "requirement": ">= 3 ridge notches, width 8-15 m, sight length <= 200 m",
            "lines": sightlines(),
        },
        "lanes": lanes_metrics(),
        "high_grounds": [
            dict(id=h["id"], name=h["name"], pos=list(h["pos"]), top_m=h["top"],
                 attack_routes=sum(1 for r in h["routes"] if r["kind"] == "attack"),
                 retreat_routes=sum(1 for r in h["routes"] if r["kind"] == "retreat"),
                 routes=[dict(id=r["id"], kind=r["kind"], slope_deg=r["slope_deg"],
                              length_m=round(polyline_length(r["poly"]), 1))
                         for r in h["routes"]])
            for h in HIGH_GROUNDS
        ],
        "vertical_routes": {
            "count": len(TUNNELS) + len(BRIDGES) + len(HIGH_GROUNDS),
            "requirement": ">= 2 (high ground / underground)",
            "tunnels": [dict(id=t["id"], name=t["name"],
                             length_m=round(math.dist(t["p0"], t["p1"]), 1)) for t in TUNNELS],
            "bridges": [dict(id=b["id"], name=b["name"], span_m=b["span"]) for b in BRIDGES],
            "high_ground_ramps": sum(len(h["routes"]) for h in HIGH_GROUNDS),
        },
        "vehicles": {
            "count": len(VEHICLES),
            "type": "light_transport, 4 seats, 14 m/s",
            "placements": [dict(id=v["id"], pos=list(v["pos"]), team=v["team"],
                                type=v["type"]) for v in VEHICLES],
        },
        "teleports": {
            "pairs": 2,
            "cooldown_s": TELEPORT_COOLDOWN_S,
            "radius_m": TELEPORT_RADIUS,
            "placements": [dict(id=t["id"], pair=t["pair"], pos=list(t["pos"]),
                                role=t["role"]) for t in TELEPORTS],
            "note": "spawn <-> home objective only; the contested middle stays on foot",
        },
        "cover": {
            "half_cover_height_m": list(COVER_HALF_HEIGHT),
            "full_cover_height_m": list(COVER_FULL_HEIGHT),
            "required_radius_half_m": COVER_HALF_RADIUS,
            "required_radius_full_m": COVER_FULL_RADIUS,
            "belt_count": len(COVER_BELTS),
            "spawn_full_cover_buffer_m": SPAWN_CLEAR_R,
            "verification_owner": "stage 4 (real geometry + LOS raycast)",
        },
        "balance": {
            "team_access_m": team_access(),
            "symmetry_problems": assert_symmetry(),
        },
        "spawn_los": spawn_to_spawn_los_blocked(),
    }
    if terrain_stats:
        m["terrain_stage0"] = terrain_stats
    if cover_half is not None:
        dc, pct, ok, total = cover_half
        m["cover"]["half_cover_plan"] = {
            "desert_pixels": int(dc), "walkable_verified_pixels": total,
            "coverage_pct": round(pct, 4), "target_pct": 100.0, "pass": bool(dc == 0),
        }
    if cover_full is not None:
        dc, pct, ok, total = cover_full
        m["cover"]["full_cover_plan"] = {
            "desert_pixels": int(dc), "walkable_verified_pixels": total,
            "coverage_pct": round(pct, 4), "target_pct": 100.0, "pass": bool(dc == 0),
        }
    return m
