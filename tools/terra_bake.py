"""Bake Terra's surface from the sources in Data/Sources into Data/Terra.

Every grid is equirectangular with row 0 at the north edge and column 0 at 180 W; heights and distances are real
Earth metres, which the sim scales to Terra.

elevation.i16  GEBCO 2024 heights on the 15" grid, lake beds carved, sea beds the satellite sees raised to the depth
               it sees them at, beds shelving from the coast, and shores banked, followed by six 2x2 box mips.
levels.i16     water surface level in quarter metres on a 30" grid; -32768 where there is no water. Lakes stand at
               HydroLAKES' surveyed levels, and rivers wide enough to show on the grid slope down their valleys.
shore.i16      signed distance to the nearest shore in units of 16 m on the 30" grid, negative in water.
coast.i8       signed distance to the coast of the carved bodies in units of 8 m on the 15" grid, out to a kilometre,
               from the water mask's own 5" pixels, so coastlines keep their shape between the posts.
fetch.u8       open water upwind on a 2' grid, 8 directions per cell (E, NE, N, NW, W, SW, S, SE), log-encoded.
rivers.bin     HydroRIVERS reaches carrying at least MIN_DISCHARGE, as smoothed polylines with a level, width and depth per
               vertex and each vertex's neighbours along the curve (across a junction, on the main stem), speed per
               reach, and an index of the reaches near each 0.1 degree cell.
sea_state.bin  ERA5 1991-2020 monthly climatology on a 0.5 degree grid: wind, wind sea, swell and sea ice.
clouds.bin     ERA5 weather at one hour on a 0.25 degree grid, eight bytes per cell: the low cloud's cover, convection,
               base and top, then the anvils' cover, base and top. Heights are Terra's: the ground scaled by TERRA,
               the air above it at full size.
colour.raw     Blue Marble June 2004 as cube-face tiles (levels 0-6, 256 px: 248 plus a 4 px border),
               uncompressed; the Unity bake (Max-Q > Bake Terra Tiles) compresses it to colour.tiles.

Every step caches its output, so a rerun resumes where the last one stopped.
"""

import csv
import glob
import io
import math
import os
import re
import sys
import zipfile
from multiprocessing import Pool

import h5py
import imagecodecs
import numpy as np
import pyogrio
import shapely
import tifffile
from PIL import Image
from scipy import ndimage

DATA = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "Data")
SOURCES = os.path.join(DATA, "Sources")
WORK = os.path.join(SOURCES, "work")
OUT = os.path.join(DATA, "Terra")

RIVERS = "/vsizip/" + os.path.join(SOURCES, "HydroRIVERS_v10.gdb.zip").replace("\\", "/") + "/HydroRIVERS_v10.gdb"
LAKES = "/vsizip/" + os.path.join(SOURCES, "HydroLAKES_points_v10_shp.zip").replace("\\", "/") + "/HydroLAKES_points_v10_shp/HydroLAKES_points_v10.shp"
BATHY = os.path.join(SOURCES, "globathy.zip")
ERA5 = os.path.join(SOURCES, "era5")

W, H = 86_400, 43_200
MIPS = 7
NONE = -32_768

# Metres of Earth per degree of latitude, and per 15" post.
DEGREE = 111_195.0
POST = DEGREE / 240.0

# Levels are stored in quarter metres, so gently sloping rivers don't step; shore distance in 16 m units.
LEVEL_UNITS = 4.0
SHORE_UNIT = 16.0
SHORE_FINE = 16_000.0

# Fetch grid (2') and its log encoding: code 1 is 100 m of open water, 255 is FETCH_MAX or more; 0 is land.
FETCH_SCALE = 8
FETCH_MIN = 100.0
FETCH_MAX = 1_000_000.0

# Rivers carrying less than this (m^3/s) are narrower than 15 m by Andreadis' width law, 3 m on Terra.
MIN_DISCHARGE = 4.34
TERRA = 0.2
RIVER_MAGIC = 0x5652514F
RIVER_CELLS = 10
SEA_MAGIC = 0x5353514D
CLOUD_MAGIC = 0x4443514D

# Cloud heights are encoded in bytes over these ranges (m): the low cloud's base, and every top and the anvils' base.
CLOUD_BASE_RANGE = 6_000.0
CLOUD_TOP_RANGE = 16_000.0

# The coast's distance on the 15" posts, from the source's own 5" pixels: 8 m units out to a kilometre either side,
# negative in water. Bands of posts are measured with a halo wide enough for the bodies' filters and that kilometre.
FINE = 3
PIXEL = POST / FINE
COAST_UNIT = 8.0
COAST_REACH = 127.0 * COAST_UNIT
COAST_BAND = 64
COAST_HALO = 8

TILE = 248
BORDER = 4
TEXELS = TILE + 2 * BORDER
LEVELS = 7

# Blue Marble's sRGB bytes as linear reflectance.
LINEAR = np.where(np.arange(256) <= 10, np.arange(256) / 255.0 / 12.92, ((np.arange(256) / 255.0 + 0.055) / 1.055) ** 2.4).astype(np.float32)

# Where the satellite sees a sea bed through clear water, the water column the shaders use (WaterOptics.hlsl) is undone
# for its depth: Jerlov II's attenuation (per metre) and Blue Marble's deep-ocean colour at green and blue, over sand.
SEEN_SAND = np.array([0.35, 0.3], np.float32)
SEEN_DEEP = np.array([0.0015, 0.007], np.float32)
SEEN_CLEAR = np.array([0.072, 0.043], np.float32)
SEEN_LATITUDE = 35.0

# Within the coast's reach a bed shelves from the coast no steeper than this (real metres down per metre out, from a
# start): beds carved by posts would otherwise drop at the posts' staircase, which clear water shows.
SHELF_START = 0.5
SHELF_SLOPE = 0.15

# Round footprint, five posts in radius, for the banks around each body.
DISK = np.hypot(*np.mgrid[-5:6, -5:6]) <= 5.0

# Cube faces as (normal, right, up), sim frame, Z up, longitude 0 on +X. Mirrors MaxQ.Sim.Surface.CubeFace.
FACES = [
    ((1, 0, 0), (0, 1, 0), (0, 0, 1)),
    ((-1, 0, 0), (0, 0, 1), (0, 1, 0)),
    ((0, 1, 0), (0, 0, 1), (1, 0, 0)),
    ((0, -1, 0), (1, 0, 0), (0, 0, 1)),
    ((0, 0, 1), (1, 0, 0), (0, 1, 0)),
    ((0, 0, -1), (0, 1, 0), (1, 0, 0)),
]


def log(message):

    print(message, flush=True)


def work(name):

    return os.path.join(WORK, name)


def mip_shape(m):

    return H >> m, W >> m


def mip_offset(m):

    return sum((H >> i) * (W >> i) for i in range(m))


# --- Elevation --------------------------------------------------------------------------------------


def assemble_elevation():

    path = work("gebco.i16")

    if os.path.exists(path):

        return np.memmap(path, np.int16, "r", shape=(H, W))

    log("assembling GEBCO")
    out = np.memmap(path + ".part", np.int16, "w+", shape=(H, W))

    for tif in glob.glob(os.path.join(SOURCES, "gebco", "*.tif")):

        n, s, w, e = (float(v) for v in re.search(r"n(-?\d+\.\d)_s(-?\d+\.\d)_w(-?\d+\.\d)_e(-?\d+\.\d)", tif).groups())
        row, col = int((90.0 - n) * 240), int((w + 180.0) * 240)
        out[row:row + 21_600, col:col + 21_600] = tifffile.imread(tif)

    out.flush()
    del out
    os.replace(path + ".part", path)

    return np.memmap(path, np.int16, "r", shape=(H, W))


# --- Water mask -------------------------------------------------------------------------------------


def _water_rows(chunk):

    # Three rows of the source's 256 px tiles (768 rows of its 5" pixels), as water or not.
    tif = tifffile.TiffFile(os.path.join(SOURCES, "water.tif"))
    page = tif.pages[0]
    handle = tif.filehandle
    across = (page.shape[1] + 255) // 256
    rows = np.zeros((768, across * 256), np.uint8)

    for tile_row in range(3):

        index_row = chunk * 3 + tile_row

        if index_row * 256 >= page.shape[0]:

            break

        for tile_col in range(across):

            index = index_row * across + tile_col
            handle.seek(page.dataoffsets[index])
            data = imagecodecs.lzw_decode(handle.read(page.databytecounts[index]), out=65_536)
            rows[tile_row * 256:(tile_row + 1) * 256, tile_col * 256:(tile_col + 1) * 256] = np.frombuffer(data, np.uint8)[:65_536].reshape(256, 256)

    return rows[:, :W * FINE] == 2


def _water_chunk(chunk):

    rows = _water_rows(chunk)
    water = rows.reshape(256, 3, W, 3).sum(axis=(1, 3)) >= 5
    out = np.memmap(work("water15.u8.part"), np.uint8, "r+", shape=(H, W))
    last = min(256, H - chunk * 256)
    out[chunk * 256:chunk * 256 + last] = water[:last]
    out.flush()


def _water5_chunk(chunk):

    rows = _water_rows(chunk)
    out = np.memmap(work("water5.bits.part"), np.uint8, "r+", shape=(H * FINE, W * FINE // 8))
    last = min(768, H * FINE - chunk * 768)
    out[chunk * 768:chunk * 768 + last] = np.packbits(rows[:last], axis=1)
    out.flush()


def decode_water():

    path = work("water15.u8")

    if not os.path.exists(path):

        log("decoding the ESA CCI water mask to 15\"")
        np.memmap(path + ".part", np.uint8, "w+", shape=(H, W)).flush()

        with Pool(16) as pool:

            pool.map(_water_chunk, range((H + 255) // 256))

        os.replace(path + ".part", path)

    return np.memmap(path, np.uint8, "r", shape=(H, W))


def decode_water5():

    # The source at its own 5" pixels, a bit each, for the coast's distance field.
    path = work("water5.bits")

    if not os.path.exists(path):

        log("decoding the ESA CCI water mask at 5\"")
        np.memmap(path + ".part", np.uint8, "w+", shape=(H * FINE, W * FINE // 8)).flush()

        with Pool(16) as pool:

            pool.map(_water5_chunk, range((H + 255) // 256))

        os.replace(path + ".part", path)


# --- Water bodies -----------------------------------------------------------------------------------


def water_bodies(water15):

    path = work("labels30.i32")

    if os.path.exists(path):

        return np.memmap(path, np.int32, "r", shape=(H // 2, W // 2))

    log("labelling water bodies at 30\"")
    h2, w2 = H // 2, W // 2
    water30 = np.zeros((h2, w2), bool)

    for r in range(0, H, 4_320):

        water30[r // 2:r // 2 + 2_160] = water15[r:r + 4_320].reshape(2_160, 2, w2, 2).sum(axis=(1, 3)) >= 2

    # Opening cuts rivers narrower than ~2 km loose from the lakes and seas they feed.
    square = np.ones((3, 3), bool)
    opened = ndimage.binary_opening(water30, structure=square)
    labels, count = ndimage.label(opened, structure=square)
    del opened

    # The grid wraps at the date line: merge bodies that touch across it.
    parent = np.arange(count + 1)

    def find(a):

        while parent[a] != a:

            parent[a] = parent[parent[a]]
            a = parent[a]

        return a

    west, east = labels[:, 0], labels[:, -1]

    for shift in (-1, 0, 1):

        a = west[max(0, shift):h2 + min(0, shift)]
        b = east[max(0, -shift):h2 + min(0, -shift)]

        for x, y in set(zip(a[(a > 0) & (b > 0)].tolist(), b[(a > 0) & (b > 0)].tolist())):

            parent[find(x)] = find(y)

    roots = np.array([find(i) for i in range(count + 1)])
    labels = roots[labels].astype(np.int32)

    # Give back the narrow arms the opening removed, when they touch a body.
    near = ndimage.grey_dilation(labels, footprint=square)
    labels = np.where(water30 & (labels == 0), near, labels).astype(np.int32)
    del near

    out = np.memmap(path + ".part", np.int32, "w+", shape=labels.shape)
    out[:] = labels
    out.flush()
    del out
    os.replace(path + ".part", path)

    return np.memmap(path, np.int32, "r", shape=(h2, w2))


def _segments(ids, values, count):

    order = np.lexsort((values, ids))
    ids, values = ids[order], values[order]

    return values, np.searchsorted(ids, np.arange(count + 1)), np.searchsorted(ids, np.arange(count + 1), side="right")


def lake_levels(elevation, labels):

    path = work("levels.f64")

    if os.path.exists(path):

        return np.fromfile(path)

    log("estimating lake levels")
    h2, w2 = H // 2, W // 2
    mean30 = np.zeros((h2, w2), np.int16)
    max30 = np.zeros((h2, w2), np.int16)

    for r in range(0, H, 4_320):

        block = elevation[r:r + 4_320].reshape(2_160, 2, w2, 2)
        mean30[r // 2:r // 2 + 2_160] = np.round(block.mean(axis=(1, 3), dtype=np.float32)).astype(np.int16)
        max30[r // 2:r // 2 + 2_160] = block.max(axis=(1, 3))

    labels = np.asarray(labels)
    count = int(labels.max())
    sizes = np.bincount(labels.ravel(), minlength=count + 1)
    sizes[0] = 0
    ocean = int(np.argmax(sizes))
    log(f"  {int((sizes > 0).sum())} bodies; ocean is body {ocean}")

    # Lakes without bathymetry are flat in GEBCO at their surface.
    flat = labels.ravel()
    inland = (flat > 0) & (flat != ocean)
    heights, starts, ends = _segments(flat[inland], mean30.ravel()[inland].astype(np.float64), count)
    counts = ends - starts
    has = counts > 0
    median = np.full(count + 1, np.nan)
    median[has] = heights[(starts + counts // 2)[has]]
    ids = np.repeat(np.arange(count + 1), counts)
    flat_share = np.zeros(count + 1)
    flat_share[has] = np.add.reduceat(np.abs(heights - median[ids]) <= 1.0, starts[has]) / counts[has]
    del heights, ids

    # The rest stand where the shore's heights bunch up: the densest 5 m band in its lower part. The low tail
    # is where the mask and the bathymetry disagree, and it is spread thin.
    square = np.ones((3, 3), bool)
    ring = ndimage.binary_dilation(labels > 0, structure=square) & (labels == 0)
    ring_ids = ndimage.grey_dilation(labels, footprint=square)[ring]
    keep = ring_ids != ocean
    heights, starts, ends = _segments(ring_ids[keep], max30[ring][keep].astype(np.float64), count)
    del ring, ring_ids, keep
    shore = np.full(count + 1, np.nan)

    for body in np.nonzero(ends > starts)[0]:

        v = heights[starts[body]:ends[body]]
        v = v[:max(1, int(len(v) * 0.6))]
        reach = np.searchsorted(v, v + 5.0, side="right") - np.arange(len(v))
        best = int(np.argmax(reach))
        shore[body] = np.median(v[best:best + reach[best]])

    levels = np.where(flat_share >= 0.5, median, shore)
    levels[sizes < 2] = np.nan
    levels[np.abs(levels) <= 5.0] = 0.0
    levels[ocean] = 0.0
    levels[0] = np.nan
    levels = np.round(levels)
    levels.tofile(path)

    return levels


def cell_latitudes(rows, count):

    return 90.0 - (np.arange(rows) + 0.5) * 180.0 / count


def hydrolakes(labels, estimate):

    path = work("lakes.f64")
    count = len(estimate) - 1

    if os.path.exists(path):

        levels, depths = np.fromfile(path).reshape(2, count + 1)

        return levels, depths

    log("joining HydroLAKES levels and GLOBathy depths to the water bodies")
    h2, w2 = H // 2, W // 2
    meta, _, _, fields = pyogrio.raw.read(LAKES, columns=["Hylak_id", "Lake_area", "Elevation", "Pour_long", "Pour_lat"], read_geometry=False)
    f = dict(zip(meta["fields"], fields))
    ids = f["Hylak_id"].astype(np.int64)
    area = f["Lake_area"].astype(np.float64)
    elevation = f["Elevation"].astype(np.float64)

    dmax = np.full(ids.max() + 1, np.nan)

    with zipfile.ZipFile(BATHY) as archive:

        name = next(n for n in archive.namelist() if n.endswith("(ALL_LAKES).csv"))

        with archive.open(name) as handle:

            reader = csv.reader(io.TextIOWrapper(handle, "utf-8"))
            header = next(reader)
            id_column, depth_column = header.index("Hylak_id"), header.index("Dmax_use_m")

            for row in reader:

                dmax[int(row[id_column])] = float(row[depth_column])

    cell = (DEGREE / 120.0 / 1_000.0) ** 2 * np.cos(np.radians(cell_latitudes(h2, h2)))
    body_area = np.zeros(count + 1)

    for r in range(0, h2, 1_080):

        block = labels[r:r + 1_080]
        body_area += np.bincount(block.ravel(), weights=np.repeat(cell[r:r + 1_080], w2), minlength=count + 1)

    # A pour point sits on its lake's outlet, often down the river leaving it: of the bodies nearby, the lake is the
    # one whose area fits it best, within a factor of five. Most match within two cells; the rest look out to twelve.
    row = np.clip(((90.0 - f["Pour_lat"]) * 120.0).astype(np.int64), 0, h2 - 1)
    col = np.clip(((f["Pour_long"] + 180.0) * 120.0).astype(np.int64), 0, w2 - 1)
    body = np.zeros(len(ids), np.int64)
    fit = np.full(len(ids), math.log(5.0))

    for reach, stride in ((2, 1), (12, 2)):

        todo = np.nonzero(body == 0)[0]

        for dr in range(-reach, reach + 1, stride):

            for dc in range(-reach, reach + 1, stride):

                candidate = labels[np.clip(row[todo] + dr, 0, h2 - 1), (col[todo] + dc) % w2].astype(np.int64)
                score = np.abs(np.log(np.maximum(area[todo], 1e-9) / np.maximum(body_area[candidate], 1e-9)))
                better = (candidate > 0) & (score < fit[todo])
                body[todo[better]], fit[todo[better]] = candidate[better], score[better]

    # Each body takes the largest lake matched to it.
    order = np.argsort(area)
    rank = np.empty(len(order), np.int64)
    rank[order] = np.arange(len(order))
    largest = np.full(count + 1, -1)
    np.maximum.at(largest, body, rank)
    best = np.where(largest >= 0, order[np.maximum(largest, 0)], -1)
    best[0] = -1
    good = best >= 0
    good[int(np.argmax(body_area))] = False

    # The estimate comes from the survey itself, so the lake always fits its bed and shore; HydroLAKES' level (from
    # a different DEM, off by metres at times) refines it only within the window carve() shapes beds in, and stands
    # alone where the survey gave no estimate.
    surveyed = np.where(good, elevation[np.maximum(best, 0)], np.nan)
    refine = good & ((np.isnan(estimate) & np.isfinite(surveyed)) | ((surveyed >= estimate - 1.0) & (surveyed <= estimate + 5.0)))
    levels = estimate.copy()
    levels[refine] = surveyed[refine]
    depths = np.full(count + 1, np.nan)
    depths[good] = dmax[ids[best[good]]]
    log(f"  {int(good.sum())} bodies matched to HydroLAKES, {int(refine.sum())} take its level")

    np.stack([levels, depths]).tofile(path)

    return levels, depths


def _decreasing(z):

    # Pool-adjacent-violators: the least-squares fit to z that never rises downstream.
    values, sizes = [], []

    for x in z:

        values.append(x)
        sizes.append(1)

        while len(values) > 1 and values[-2] < values[-1]:

            size = sizes[-2] + sizes[-1]
            values[-2] = (values[-2] * sizes[-2] + values[-1] * sizes[-1]) / size
            sizes[-2] = size
            del values[-1], sizes[-1]

    return np.repeat(values, sizes)


def river_network(elevation, labels, body_levels):

    cache = work("rivers.npz")

    if os.path.exists(cache):

        return dict(np.load(cache))

    log("tracing rivers")
    meta, _, geometry, fields = pyogrio.raw.read(RIVERS, columns=["HYRIV_ID", "NEXT_DOWN", "DIS_AV_CMS"], where=f"DIS_AV_CMS >= {MIN_DISCHARGE}")
    f = dict(zip(meta["fields"], fields))
    ids = f["HYRIV_ID"].astype(np.int64)
    discharge = f["DIS_AV_CMS"].astype(np.float64)
    lines = shapely.from_wkb(geometry)
    coords, owner = shapely.get_coordinates(lines, return_index=True)
    reaches = len(ids)
    first = np.searchsorted(owner, np.arange(reaches + 1))
    log(f"  {reaches} reaches, {len(coords)} vertices")

    # Downstream neighbour of each reach, or -1 at an outlet.
    order = np.argsort(ids)
    at = np.searchsorted(ids[order], f["NEXT_DOWN"])
    at = np.minimum(at, reaches - 1)
    down = np.where(ids[order][at] == f["NEXT_DOWN"], order[at], -1)

    # HydroRIVERS follows the 15" grid in steps; two passes of a [1 2 1] filter round them off. Reach ends stay put,
    # so reaches still meet at their confluences.
    lon, lat = coords[:, 0].copy(), coords[:, 1].copy()
    interior = np.ones(len(lon), bool)
    interior[first[:-1]] = False
    interior[first[1:] - 1] = False
    inner = np.nonzero(interior)[0]

    for _ in range(2):

        west = (lon[inner - 1] - lon[inner] + 180.0) % 360.0 - 180.0
        east = (lon[inner + 1] - lon[inner] + 180.0) % 360.0 - 180.0
        lon[inner] = (lon[inner] + 0.25 * (west + east) + 180.0) % 360.0 - 180.0
        lat[inner] = 0.5 * lat[inner] + 0.25 * (lat[inner - 1] + lat[inner + 1])

    # The valley floor under each vertex: the lowest post around it, which finds even a gorge narrower than the posts'
    # spacing, where an interpolated survey would ride its walls and dam every level upstream.
    row = (90.0 - lat) * 240.0 - 0.5
    column = (lon + 180.0) * 240.0 - 0.5
    r, c = np.clip(np.round(row).astype(np.int64), 0, H - 1), np.round(column).astype(np.int64)
    sort = np.argsort(r, kind="stable")
    floor = np.full(len(lon), np.inf)

    for dr in (-1, 0, 1):

        for dc in (-1, 0, 1):

            posts = elevation[np.clip(r[sort] + dr, 0, H - 1), (c[sort] + dc) % W]
            floor[sort] = np.minimum(floor[sort], posts)

    # Inside a lake or the sea, where the ground lies at or under its surface, the river is that body.
    body = labels[np.minimum(r // 2, H // 2 - 1), (c // 2) % (W // 2)]
    surface = body_levels[body]
    fixed = (body > 0) & np.isfinite(surface) & (floor <= surface + 1.0)
    level = np.where(fixed, surface, floor)

    # Outlets first, then upstream: each reach ends at the level its downstream reach starts at, and never rises
    # on the way down.
    depth = np.zeros(reaches, np.int64)

    while True:

        deeper = np.where(down >= 0, depth[np.maximum(down, 0)] + 1, 0)

        if np.array_equal(deeper, depth):

            break

        depth = deeper

    for reach in np.argsort(depth, kind="stable"):

        a, b = first[reach], first[reach + 1]
        fit = _decreasing(level[a:b])
        end = level[first[down[reach]]] if down[reach] >= 0 else fit[-1]
        fit = np.maximum(fit, end)
        fit[-1] = end
        level[a:b] = fit

    width = 7.2 * np.sqrt(discharge)
    channel = 0.27 * discharge ** 0.39
    speed = discharge / (width * channel)

    result = dict(lon=lon, lat=lat, level=level, first=first, width=width, depth=channel, speed=speed, fixed=fixed)
    np.savez(cache, **result)

    return result


def river_down():

    # Downstream neighbour of each reach traced by river_network, or -1 at an outlet; the reaches read in the same order.
    path = work("rivers_down.npy")

    if os.path.exists(path):

        return np.load(path)

    meta, _, _, fields = pyogrio.raw.read(RIVERS, columns=["HYRIV_ID", "NEXT_DOWN", "DIS_AV_CMS"], where=f"DIS_AV_CMS >= {MIN_DISCHARGE}", read_geometry=False)
    f = dict(zip(meta["fields"], fields))
    ids = f["HYRIV_ID"].astype(np.int64)
    order = np.argsort(ids)
    at = np.minimum(np.searchsorted(ids[order], f["NEXT_DOWN"]), len(ids) - 1)
    down = np.where(ids[order][at] == f["NEXT_DOWN"], order[at], -1)
    np.save(path, down)

    return down


def write_rivers(rivers):

    path = os.path.join(OUT, "rivers.bin")

    if os.path.exists(path):

        return

    log("writing rivers")
    lon, lat, level, first = rivers["lon"], rivers["lat"], rivers["level"], rivers["first"]
    reaches, vertices = len(first) - 1, len(lon)
    owner = np.repeat(np.arange(reaches), np.diff(first)).astype(np.int32)
    width = rivers["width"] * TERRA

    # Where reaches meet, the widest flowing in carries on as the main stem through the one below: the curve runs on
    # across the junction there, which both reaches share, instead of turning a corner. A tributary's end, and a
    # source, stand in for their missing neighbour.
    down = river_down()
    feeding = np.nonzero(down >= 0)[0]
    feeding = feeding[np.argsort(width[feeding], kind="stable")]
    upstream = np.full(reaches, -1, np.int64)
    upstream[down[feeding]] = feeding
    length = np.diff(first)
    heads, tails = first[:-1], first[1:] - 1
    prev = np.arange(vertices) - 1
    after = np.arange(vertices) + 1
    prev[heads] = heads
    after[tails] = tails
    joined = (upstream >= 0) & (length >= 2)
    joined[joined] &= length[upstream[joined]] >= 2
    prev[heads[joined]] = first[upstream[joined] + 1] - 2
    main = (down >= 0) & (length >= 2)
    main[main] &= (upstream[down[main]] == np.nonzero(main)[0]) & (length[down[main]] >= 2)
    after[tails[main]] = first[down[main]] + 1

    # A main stem widens and deepens across a junction rather than at it: the vertex both reaches share takes the mean.
    across = np.nonzero(joined)[0]
    vertex_width = width[owner]
    vertex_depth = rivers["depth"][owner] * TERRA
    vertex_width[heads[across]] = vertex_width[tails[upstream[across]]] = 0.5 * (width[across] + width[upstream[across]])
    vertex_depth[heads[across]] = vertex_depth[tails[upstream[across]]] = 0.5 * (rivers["depth"][across] + rivers["depth"][upstream[across]]) * TERRA

    # Each vertex's piece of the curve runs from the midpoint before it to the midpoint after it, so its bounds are those
    # three points, widened by the river's reach into its banks: a floodplain of twice the width, at least 20 m on Terra.
    unwrap = lambda d: (d + 180.0) % 360.0 - 180.0
    lons = np.stack([lon + 0.5 * unwrap(lon[prev] - lon), lon, lon + 0.5 * unwrap(lon[after] - lon)])
    lats = np.stack([0.5 * (lat + lat[prev]), lat, 0.5 * (lat + lat[after])])
    widest = np.maximum(vertex_width, np.maximum(vertex_width[prev], vertex_width[after]))
    reach = (0.5 * widest + np.maximum(2.0 * widest, 20.0)) / (DEGREE * TERRA)
    across = reach / np.maximum(np.cos(np.radians(np.abs(lat) + reach)), 0.01)

    r0 = np.floor((90.0 - (lats.max(axis=0) + reach)) * RIVER_CELLS).astype(np.int64)
    r1 = np.floor((90.0 - (lats.min(axis=0) - reach)) * RIVER_CELLS).astype(np.int64)
    c0 = np.floor((lons.min(axis=0) - across + 180.0) * RIVER_CELLS).astype(np.int64)
    c1 = np.floor((lons.max(axis=0) + across + 180.0) * RIVER_CELLS).astype(np.int64)
    rows, cols = 180 * RIVER_CELLS, 360 * RIVER_CELLS
    cells, entries = [], []

    for dr in range(int((r1 - r0).max()) + 1):

        for dc in range(int((c1 - c0).max()) + 1):

            take = (r0 + dr <= r1) & (c0 + dc <= c1)
            rr = np.clip(r0[take] + dr, 0, rows - 1)
            cells.append(rr * cols + (c0[take] + dc) % cols)
            entries.append(np.nonzero(take)[0])

    cells, entries = np.concatenate(cells), np.concatenate(entries)
    order = np.lexsort((entries, cells))
    cells, entries = cells[order], entries[order].astype(np.uint32)
    keep = np.ones(len(cells), bool)
    keep[1:] = (cells[1:] != cells[:-1]) | (entries[1:] != entries[:-1])
    cells, entries = cells[keep], entries[keep]
    starts = np.searchsorted(cells, np.arange(rows * cols + 1)).astype(np.uint32)
    log(f"  {reaches} reaches, {vertices} vertices, {len(entries)} index entries")

    with open(path + ".part", "wb") as out:

        np.array([RIVER_MAGIC, reaches, vertices, rows, cols, len(entries)], np.int32).tofile(out)
        first.astype(np.int32).tofile(out)
        owner.tofile(out)
        prev.astype(np.int32).tofile(out)
        after.astype(np.int32).tofile(out)
        # Positions as unit vectors in the sim frame (Z up, longitude 0 on +X), fixed point at 2^30.
        phi, lam = np.radians(lat), np.radians(lon)
        unit = np.stack([np.cos(phi) * np.cos(lam), np.cos(phi) * np.sin(lam), np.sin(phi)], axis=-1)
        np.round(unit * 2.0 ** 30).astype(np.int32).tofile(out)
        np.round(level * LEVEL_UNITS).astype(np.int32).tofile(out)
        flags = np.zeros((vertices + 3) // 4 * 4, np.uint8)
        flags[:vertices] = rivers["fixed"]
        flags.tofile(out)
        np.round(vertex_width * 10.0).astype(np.int32).tofile(out)
        np.round(vertex_depth * 100.0).astype(np.int32).tofile(out)
        np.round(rivers["speed"] * 100.0).astype(np.int32).tofile(out)
        starts.tofile(out)
        entries.tofile(out)

    os.replace(path + ".part", path)


def water_levels(elevation, labels, body_levels, rivers):

    path = work("levels30.f32")
    h2, w2 = H // 2, W // 2

    if os.path.exists(path):

        return np.memmap(path, np.float32, "r", shape=(h2, w2))

    log("levelling water, rivers sloping down their valleys")
    lookup = body_levels.astype(np.float32)
    out = np.memmap(path + ".part", np.float32, "w+", shape=(h2, w2))

    # Burn the river vertices that stand above the body they flow through; a cell holds its highest.
    lon, lat, level, first = rivers["lon"], rivers["lat"], rivers["level"], rivers["first"]
    owner = np.repeat(np.arange(len(first) - 1), np.diff(first))
    rr = np.clip(((90.0 - lat) * 120.0).astype(np.int64), 0, h2 - 1)
    cc = ((lon + 180.0) * 120.0).astype(np.int64) % w2
    rise = ~rivers["fixed"]
    burned = np.full((h2, w2), -np.inf, np.float32)
    np.maximum.at(burned, (rr[rise], cc[rise]), level[rise].astype(np.float32))
    half = np.zeros((h2, w2), np.float32)
    np.maximum.at(half, (rr[rise], cc[rise]), (0.5 * rivers["width"][owner[rise]]).astype(np.float32))

    band, halo = 2_160, 8
    cell = DEGREE / 120.0

    for r0 in range(0, h2, band):

        a, b = max(0, r0 - halo), min(h2, r0 + band + halo)
        label = labels[a:b]
        surface = lookup[label]
        mean = elevation[a * 2:b * 2].reshape(b - a, 2, w2, 2).mean(axis=(1, 3), dtype=np.float32)

        # A labelled cell whose ground stands above its body's surface is river on land (a wide river merged
        # into the sea or a lake): it takes the level of the river running through it.
        seeds = np.isfinite(burned[a:b])
        distance, (ir, ic) = ndimage.distance_transform_edt(~seeds, return_indices=True)
        river = burned[a:b][ir, ic]
        reach = half[a:b][ir, ic] / cell + 1.0
        on_land = (label > 0) & (mean >= surface + 1.0) & (distance <= reach) & (river > surface)
        surface = np.where(on_land, river, surface)

        top = r0 - a
        out[r0:r0 + min(band, h2 - r0)] = surface[top:top + min(band, h2 - r0)]
        log(f"  rows {r0}-{r0 + min(band, h2 - r0)}")

    out.flush()
    del out
    os.replace(path + ".part", path)

    return np.memmap(path, np.float32, "r", shape=(h2, w2))


def lake_beds(labels, body_levels, body_depths):

    path = work("beds30.u8")
    h2, w2 = H // 2, W // 2

    if os.path.exists(path):

        return np.memmap(path, np.uint8, "r", shape=(h2, w2))

    log("shaping lake beds")
    out = np.zeros((h2, w2), np.uint8)
    labels = np.asarray(labels)
    objects = ndimage.find_objects(labels)

    for body in np.nonzero(np.isfinite(body_depths))[0]:

        where = objects[body - 1]

        if where is None:

            continue

        # Distance from shore within each lake, normalised to its deepest point, in rounded contours.
        rows, cols = where
        rows = slice(max(0, rows.start - 1), min(h2, rows.stop + 1))
        cols = slice(max(0, cols.start - 1), min(w2, cols.stop + 1))
        lake = labels[rows, cols] == body
        mid = 90.0 - (rows.start + rows.stop) * 0.5 / 120.0
        distance = ndimage.distance_transform_edt(lake, sampling=(1.0, max(math.cos(math.radians(mid)), 0.05)))
        deepest = distance.max()

        if deepest > 0.0:

            share = np.round(distance / deepest * 255.0).astype(np.uint8)
            out[rows, cols] = np.where(lake, share, out[rows, cols])

    out.tofile(path + ".part")
    os.replace(path + ".part", path)

    return np.memmap(path, np.uint8, "r", shape=(h2, w2))


def seen_depth(colour):

    # Metres of water over a bed the satellite sees, or NaN where it sees none: GEBCO interpolates straight across
    # reefs, lagoons and carbonate banks. Clear water over a bed is turquoise, red long gone; silt is brown and rock
    # flour grey-green, so neither passes for a bed. A bed that returns under a tenth of its light is not trusted.
    rgb = LINEAR[colour]
    trip = (rgb[:, 1:] - SEEN_DEEP) / (SEEN_SAND - SEEN_DEEP)
    clear = (rgb[:, 1] > 3.0 * rgb[:, 0]) & (rgb[:, 2] > 0.6 * rgb[:, 1]) & (trip.min(axis=1) > 0.1)
    depth = -np.log(np.clip(trip, 0.1, 1.0)) / (2.0 * SEEN_CLEAR)

    return np.where(clear, np.maximum(depth.mean(axis=1), 0.5), np.nan)


def carve(elevation, water15, labels, cell_levels, beds, body_depths):

    path, levels_path = os.path.join(OUT, "elevation.i16"), os.path.join(OUT, "levels.i16")

    if os.path.exists(path) and os.path.exists(levels_path):

        return

    log("carving lake beds and banking shores")
    out = np.memmap(path + ".part", np.int16, "w+", shape=(mip_offset(MIPS),))
    base = out[:H * W].reshape(H, W)
    cells = np.full((H // 2, W // 2), -np.inf, np.float32)
    band, halo = 2_048, 16
    depths = np.nan_to_num(body_depths, nan=-1.0).astype(np.float32)
    colour = np.memmap(work("colour0.u8"), np.uint8, "r", shape=(H, W, 3))
    coast = np.memmap(os.path.join(OUT, "coast.i8"), np.int8, "r", shape=(H, W))

    for r0 in range(0, H, band):

        a, b = max(0, r0 - halo), min(H, r0 + band + halo)
        e = elevation[a:b].astype(np.float32)
        w = water15[a:b].astype(bool)
        level = np.repeat(np.repeat(cell_levels[a // 2:b // 2], 2, axis=0), 2, axis=1)
        level = np.where(np.isnan(level), -np.inf, level)

        near = ndimage.maximum_filter(level, size=5, mode=("nearest", "wrap"))
        level = np.where(w & ~np.isfinite(level), near, level)
        body = w & np.isfinite(level)

        # Beds where the survey only knows the surface: GLOBathy's depth for a surveyed lake, deepening from shore
        # like an ellipsoid; otherwise a shelf falling 3 m a post from the shore.
        depth = np.minimum(ndimage.distance_transform_edt(body), 16.0)
        dmax = np.repeat(np.repeat(depths[labels[a // 2:b // 2]], 2, axis=0), 2, axis=1)
        share = np.repeat(np.repeat(beds[a // 2:b // 2], 2, axis=0), 2, axis=1) / 255.0
        lake = dmax * np.sqrt(share * (2.0 - share))
        shelf = 1.0 + 3.0 * depth
        bed = body & (e >= level - 1.0) & (e <= level + 5.0)
        e = np.where(bed, level - np.where(dmax > 0.0, np.maximum(lake, 1.0), shelf), e)

        # GEBCO leaves specks of a surveyed lake at its surface, shoals and islets in open water: a cell a few posts from
        # shore standing well over the floor around it takes that floor.
        speck = np.nonzero(body & (depth >= 3.0) & (np.abs(level) >= 0.5) & (e > level - 8.0))

        if speck[0].size:

            dr, dc = (d.ravel() for d in np.mgrid[-2:3, -2:3])
            around = e[np.clip(speck[0][:, None] + dr, 0, len(e) - 1), (speck[1][:, None] + dc) % W]
            floor = np.median(around, axis=1)
            e[speck] = np.where(e[speck] > floor + 20.0, floor, e[speck])

        # The sea stands no deeper than the satellite sees its bed. Only reefs and carbonate banks show one through clear
        # water; toward the poles turquoise water is glacial flour, blooms or ice.
        tropical = np.abs(90.0 - (np.arange(a, b) + 0.5) / 240.0) < SEEN_LATITUDE
        sea = np.nonzero(body & tropical[:, None] & (np.abs(level) < 0.5) & (colour[a:b, :, 1] >= 30))
        seen = level[sea] - seen_depth(colour[a:b][sea])
        e[sea] = np.where(seen > e[sea], seen, e[sea])

        # Beds shelve down from the coast as the coast distance runs, so they follow the coast's shape between the posts.
        offshore = np.asarray(coast[a:b])
        shelf = level - (SHELF_START + SHELF_SLOPE * np.maximum(-offshore.astype(np.float32), 0.0) * COAST_UNIT)
        e = np.where(body & (np.abs(offshore) < 127), np.maximum(e, shelf), e)

        # Land near a body stands at least a metre above its surface, so the water sheet's edge stays buried.
        # A round footprint cannot wrap per axis, so the band is padded across the date line by hand.
        surface = np.pad(np.where(body, level, -np.inf), ((0, 0), (5, 5)), mode="wrap")
        shore = ndimage.maximum_filter(surface, footprint=DISK, mode="nearest")[:, 5:-5]
        bank = ~body & np.isfinite(shore) & (e < shore + 1.0)
        e = np.where(bank, shore + 1.0, e)

        top, bottom = r0 - a, r0 - a + min(band, H - r0)
        base[r0:r0 + bottom - top] = np.round(e[top:bottom]).astype(np.int16)

        surface = np.where(body, level, -np.inf)[top:bottom]
        cells[r0 // 2:(r0 + bottom - top) // 2] = surface.reshape(-1, 2, W // 2, 2).max(axis=(1, 3))

        log(f"  rows {r0}-{r0 + bottom - top}")

    for m in range(1, MIPS):

        rows, cols = mip_shape(m)
        above = out[mip_offset(m - 1):mip_offset(m)].reshape(rows * 2, cols * 2)
        here = out[mip_offset(m):mip_offset(m) + rows * cols].reshape(rows, cols)

        for r in range(0, rows, 1_024):

            block = above[r * 2:(r + 1_024) * 2].astype(np.float32)
            here[r:r + 1_024] = np.round(block.reshape(-1, 2, cols, 2).mean(axis=(1, 3))).astype(np.int16)

    out.flush()
    del out, base, above, here
    os.replace(path + ".part", path)

    # The sheet covers each body and one cell around it; the banks above keep that cell dry land.
    region = ndimage.maximum_filter(cells, size=3, mode=("nearest", "wrap"))
    np.where(np.isfinite(region), np.round(region * LEVEL_UNITS), NONE).astype(np.int16).tofile(levels_path + ".part")
    os.replace(levels_path + ".part", levels_path)


# --- Shore distance and fetch -----------------------------------------------------------------------


def _signed_distance(read, rows, cols, spacing, cap, emit):

    # Metres from each cell centre to the nearest shore, negative in water, capped. Rows are one spacing apart; each
    # band of a degree takes the column spacing at its own latitude, and wraps across the date line.
    band = max(1, rows // 180)
    halo = int(math.ceil(cap / spacing)) + 1

    for r0 in range(0, rows, band):

        a, b = max(0, r0 - halo), min(rows, r0 + band + halo)
        latitude = 90.0 - (r0 + 0.5 * band) * 180.0 / rows
        across = spacing * max(math.cos(math.radians(latitude)), 0.01)
        pad = min(int(math.ceil(cap / across)) + 1, cols // 2)
        block = np.pad(read(a, b), ((0, 0), (pad, pad)), mode="wrap")
        wet = ndimage.distance_transform_edt(block, sampling=(spacing, across))
        dry = ndimage.distance_transform_edt(~block, sampling=(spacing, across))
        half = 0.5 * math.sqrt(spacing * across)
        signed = np.where(block, half - wet, dry - half)
        top, count = r0 - a, min(band, rows - r0)
        emit(r0, np.clip(signed[top:top + count, pad:pad + cols], -cap, cap).astype(np.float32))


def _coarse_mask(water15, scale):

    rows, cols = H // scale, W // scale
    out = np.empty((rows, cols), bool)

    for r in range(0, rows, 540):

        block = np.asarray(water15[r * scale:(r + 540) * scale], dtype=np.float32)
        out[r:r + 540] = block.reshape(-1, scale, cols, scale).mean(axis=(1, 3)) >= 0.5

    return out


def shore_distance(water15):

    path = os.path.join(OUT, "shore.i16")

    if os.path.exists(path):

        return

    log("measuring distance to shore")
    h2, w2 = H // 2, W // 2

    # Near the shore from the 15" mask, averaged onto the 30" grid; farther out from a 2' mask.
    near = np.empty((h2, w2), np.float32)

    def fine(r0, signed):

        near[r0 // 2:r0 // 2 + len(signed) // 2] = signed.reshape(-1, 2, w2, 2).mean(axis=(1, 3))

        if r0 % 2_400 == 0:

            log(f"  row {r0}")

    _signed_distance(lambda a, b: np.asarray(water15[a:b]).astype(bool), H, W, POST, SHORE_FINE, fine)

    coarse = np.empty((H // FETCH_SCALE, W // FETCH_SCALE), np.float32)

    def wide(r0, signed):

        coarse[r0:r0 + len(signed)] = signed

    water = _coarse_mask(water15, FETCH_SCALE)
    _signed_distance(lambda a, b: water[a:b], *water.shape, POST * FETCH_SCALE, 32_767.0 * SHORE_UNIT, wide)

    ratio = FETCH_SCALE // 2
    far = np.repeat(np.repeat(coarse, ratio, axis=0), ratio, axis=1)
    beyond = (np.abs(near) >= SHORE_FINE - POST) & (np.abs(far) > np.abs(near))
    metres = np.where(beyond, np.copysign(np.abs(far), near), near)
    np.clip(np.round(metres / SHORE_UNIT), -32_767, 32_767).astype(np.int16).tofile(path + ".part")
    os.replace(path + ".part", path)


def _coast_band(r0):

    # One band of posts: the bodies the carve banks (water posts holding a level, or two posts from one), and their 5"
    # water pixels plus any in the posts around them, measured from the posts' centres, which are the pixels 3r + 1.
    water15 = np.memmap(work("water15.u8"), np.uint8, "r", shape=(H, W))
    cells = np.memmap(work("levels30.f32"), np.float32, "r", shape=(H // 2, W // 2))
    bits = np.memmap(work("water5.bits"), np.uint8, "r", shape=(H * FINE, W * FINE // 8))
    out = np.memmap(os.path.join(OUT, "coast.i8.part"), np.int8, "r+", shape=(H, W))

    a, b = max(0, r0 - COAST_HALO), min(H, r0 + COAST_BAND + COAST_HALO)
    count = min(COAST_BAND, H - r0)
    level = np.repeat(np.repeat(np.asarray(cells[a // 2:b // 2]), 2, axis=0), 2, axis=1)
    level = np.where(np.isnan(level), -np.inf, level)
    near = ndimage.maximum_filter(level, size=5, mode=("nearest", "wrap"))
    body = np.asarray(water15[a:b]).astype(bool) & (np.isfinite(level) | np.isfinite(near))
    reach = ndimage.maximum_filter(body.view(np.uint8), size=3, mode=("nearest", "wrap")).astype(bool)
    wet = np.unpackbits(np.asarray(bits[a * FINE:b * FINE]), axis=1).astype(bool)
    wet &= np.repeat(np.repeat(reach, FINE, axis=0), FINE, axis=1)

    top = (r0 - a) * FINE + 1

    if not wet.any() or wet.all():

        out[r0:r0 + count] = -127 if wet.all() else 127
        out.flush()

        return

    latitude = 90.0 - (r0 + 0.5 * count) / 240.0
    across = PIXEL * max(math.cos(math.radians(latitude)), 0.01)
    pad = min(int(math.ceil(COAST_REACH / across)) + 1, W * FINE // 2)
    block = np.pad(wet, ((0, 0), (pad, pad)), mode="wrap")
    half = 0.5 * math.sqrt(PIXEL * across)
    posts = (slice(top, top + count * FINE, FINE), slice(pad + 1, pad + W * FINE, FINE))
    inside = ndimage.distance_transform_edt(block, sampling=(PIXEL, across))[posts]
    outside = ndimage.distance_transform_edt(~block, sampling=(PIXEL, across))[posts]
    signed = np.where(block[posts], half - inside, outside - half)

    out[r0:r0 + count] = np.clip(np.round(signed / COAST_UNIT), -127, 127).astype(np.int8)
    out.flush()


def coast_distance():

    path = os.path.join(OUT, "coast.i8")

    if os.path.exists(path):

        return

    decode_water5()
    log("measuring distance to the coast at 5\"")
    np.memmap(path + ".part", np.int8, "w+", shape=(H, W)).flush()
    bands = list(range(0, H, COAST_BAND))

    with Pool(10) as pool:

        for done, _ in enumerate(pool.imap_unordered(_coast_band, bands)):

            if done % 50 == 0:

                log(f"  {done}/{len(bands)} bands")

    os.replace(path + ".part", path)


def _open_water(line, step):

    # Along the array, the open water from each cell back to the last land before it.
    total = np.cumsum(np.broadcast_to(step, line.shape))
    last = np.maximum.accumulate(np.where(line, 0.0, total))

    return np.where(line, total - last, 0.0)


def fetch(water15):

    path = os.path.join(OUT, "fetch.u8")

    if os.path.exists(path):

        return

    log("measuring fetch")
    water = _coarse_mask(water15, FETCH_SCALE)
    rows, cols = water.shape
    step = POST * FETCH_SCALE
    across = step * np.maximum(np.cos(np.radians(cell_latitudes(rows, rows))), 0.01)
    diagonal = np.hypot(step, across)
    out = np.zeros((rows, cols, 8), np.float32)

    # East and west along each row, the row doubled so open water wraps across the date line.
    for r in range(rows):

        line = np.concatenate([water[r], water[r]])
        out[r, :, 4] = _open_water(line, across[r])[cols:]
        out[r, :, 0] = _open_water(line[::-1], across[r])[::-1][:cols]

    # North, south and the diagonals, row by row from the edge each looks toward.
    down, up = range(rows), range(rows - 1, -1, -1)

    for direction, order, shift, length in ((2, down, 0, None), (6, up, 0, None), (1, down, -1, diagonal),
                                            (3, down, 1, diagonal), (5, up, 1, diagonal), (7, up, -1, diagonal)):

        previous = np.zeros(cols, np.float32)

        for r in order:

            here = step if length is None else length[r]
            previous = np.where(water[r], here + np.roll(previous, shift), 0.0).astype(np.float32)
            out[r, :, direction] = previous

    code = 1.0 + 254.0 * np.log(np.clip(out, FETCH_MIN, FETCH_MAX) / FETCH_MIN) / math.log(FETCH_MAX / FETCH_MIN)
    np.where(out > 0.0, np.round(code), 0).astype(np.uint8).tofile(path + ".part")
    os.replace(path + ".part", path)


# --- Sea state --------------------------------------------------------------------------------------


def _era5(name):

    with h5py.File(os.path.join(ERA5, f"{name}.nc"), "r") as f:

        data = f[name][:].astype(np.float64)
        fill = f[name].attrs.get("_FillValue")
        longitudes = f["longitude"][:]

    if fill is not None:

        data[data == fill] = np.nan

    # Monthly means of 1991-2020 as twelve months, columns rolled to start at 180 W.
    months = data.reshape(-1, 12, *data.shape[1:])
    start = int(np.argmin(np.abs(((longitudes + 180.0) % 360.0) - 0.0)))

    return np.roll(months, -start, axis=-1)


def _fill_nearest(field):

    # Coasts and sea ice leave the wave model's cells empty: take the nearest ocean value, across the date line.
    out = field.copy()

    for m in range(field.shape[0]):

        empty = np.isnan(field[m])

        if not empty.any():

            continue

        cols = field.shape[-1]
        padded = np.pad(empty, ((0, 0), (cols // 2, cols // 2)), mode="wrap")
        _, (ir, ic) = ndimage.distance_transform_edt(padded, return_indices=True)
        source = np.pad(field[m], ((0, 0), (cols // 2, cols // 2)), mode="wrap")
        out[m] = source[ir, ic][:, cols // 2:cols // 2 + cols]

    return out


def sea_state():

    path = os.path.join(OUT, "sea_state.bin")

    if os.path.exists(path):

        return

    log("baking the sea-state climatology")
    u, v, speed = (_era5(n).mean(axis=0) for n in ("u10", "v10", "si10"))
    channels = [u, v, speed]

    # Waves: heights and periods averaged; directions averaged as vectors, turned from "coming from" to "going to".
    # ERA5's mean periods are the inverse-moment period, about 0.9 of the peak period for these spectra.
    for height, period, direction in (("shww", "mpww", "mdww"), ("shts", "mpts", "mdts")):

        hs, tm, theta = _era5(height), _era5(period), np.radians(_era5(direction))
        east = np.nanmean(-np.sin(theta), axis=0)
        north = np.nanmean(-np.cos(theta), axis=0)
        norm = np.maximum(np.hypot(east, north), 1e-6)
        channels += [np.nanmean(hs, axis=0), np.nanmean(tm, axis=0) / 0.9, east / norm, north / norm]

    channels.append(np.nanmean(_era5("siconc"), axis=0))
    channels = [_fill_nearest(c) for c in channels]
    scales = [100.0, 100.0, 100.0, 1_000.0, 100.0, 10_000.0, 10_000.0, 1_000.0, 100.0, 10_000.0, 10_000.0, 10_000.0]
    packed = np.stack([np.clip(np.round(c * s), -32_767, 32_767) for c, s in zip(channels, scales)], axis=-1).astype(np.int16)
    months, rows, cols, count = packed.shape

    with open(path + ".part", "wb") as out:

        np.array([SEA_MAGIC, months, rows, cols, count], np.int32).tofile(out)
        packed.tofile(out)

    os.replace(path + ".part", path)


# --- Weather --------------------------------------------------------------------------------------


def _weather_file(file, name):

    # The CDS zips single-level requests that mix instantaneous and accumulated fields, one NetCDF per kind.
    path = os.path.join(ERA5, file)

    if not zipfile.is_zipfile(path):

        return h5py.File(path, "r")

    with zipfile.ZipFile(path) as archive:

        for member in archive.namelist():

            f = h5py.File(io.BytesIO(archive.read(member)), "r")

            if name in f:

                return f

            f.close()

    raise KeyError(f"{name} is not in {file}")


def _weather(file, name):

    with _weather_file(file, name) as f:

        variable = f[name]
        raw = variable[:]
        data = raw.astype(np.float64) * variable.attrs.get("scale_factor", 1.0) + variable.attrs.get("add_offset", 0.0)
        fill = variable.attrs.get("_FillValue")
        longitudes = f["longitude"][:]
        levels = f["pressure_level"][:].astype(np.float64) if "pressure_level" in f else None

    if fill is not None:

        data[raw == fill] = np.nan

    # The one hour, columns rolled to start at 180 W.
    start = int(np.argmin(np.abs(((longitudes + 180.0) % 360.0) - 0.0)))

    return np.roll(data[0], -start, axis=-1), levels


def _walk(cloudy, start, step):

    # The last level of the unbroken run of cloudy levels from start (per column), stepping by step.
    count = cloudy.shape[0]
    index = start.copy()
    alive = np.take_along_axis(cloudy, start[None], 0)[0]

    for _ in range(count):

        after = np.clip(index + step, 0, count - 1)
        grow = alive & (after != index) & np.take_along_axis(cloudy, after[None], 0)[0]
        index = np.where(grow, after, index)
        alive = grow

    return index


def clouds():

    path = os.path.join(OUT, "clouds.bin")

    if os.path.exists(path):

        return

    log("baking the weather")
    low, _ = _weather("weather_single.nc", "lcc")
    base, _ = _weather("weather_single.nc", "cbh")
    cape, _ = _weather("weather_single.nc", "cape")
    rain, _ = _weather("weather_single.nc", "cp")
    ground = _weather("weather_single.nc", "z")[0] / 9.80665
    cover, pressures = _weather("weather_levels.nc", "cc")

    # Levels from the ground up, at their heights in the standard atmosphere, and each level's slab between midpoints.
    order = np.argsort(-pressures)
    cover = np.nan_to_num(cover[order])
    heights = 44_330.8 * (1.0 - (pressures[order] / 1_013.25) ** 0.190263)
    edges = np.concatenate([[heights[0] - 0.5 * (heights[1] - heights[0])], 0.5 * (heights[1:] + heights[:-1]),
                            [heights[-1] + 0.5 * (heights[-1] - heights[-2])]])
    cloudy = cover > 0.1
    rows, cols = low.shape

    # Low cloud: ERA5 gives its cover and base; its top is where the cloudy levels rising from the base end. A deck too
    # thin to reach a level keeps a few hundred metres.
    above = np.nan_to_num(base, nan=1_000.0)
    low = np.where(above <= 3_000.0, np.nan_to_num(low), 0.0)
    first = np.clip(np.searchsorted(heights, (ground + above).ravel() - 250.0), 0, len(heights) - 1).reshape(rows, cols)
    starts = np.take_along_axis(cloudy, first[None], 0)[0] & (heights[first] <= ground + above + 1_500.0)
    last = _walk(cloudy, first, 1)
    thickness = np.where(starts, np.maximum(edges[last + 1] - (ground + above), 400.0), 400.0)
    convection = np.maximum(np.clip((cape - 100.0) / 1_400.0, 0.0, 1.0), np.clip((rain - 1e-4) / 1.9e-3, 0.0, 1.0))
    bottom = ground * TERRA + above

    # Anvils: the upper cloud over and around deep convection, from the densest upper level out to its run's ends.
    deep = (thickness > 5_000.0) & (convection > 0.3) & (low > 0.05)
    near = ndimage.gaussian_filter(ndimage.maximum_filter(deep.astype(np.float64), size=(9, 9), mode=("nearest", "wrap")), 2.0, mode=("nearest", "wrap"))
    upper = heights >= 6_000.0
    offset = int(np.argmax(upper))
    peak = offset + np.argmax(cover[upper], axis=0)
    anvil = np.take_along_axis(cover, peak[None], 0)[0] * np.clip(2.0 * near, 0.0, 1.0)
    anvil_top = edges[_walk(cloudy, peak, 1) + 1]
    anvil_bottom = np.maximum(edges[np.maximum(_walk(cloudy, peak, -1), offset)], anvil_top - 4_000.0)
    anvil_bottom = np.minimum(anvil_bottom, anvil_top - 1_000.0)
    lowered = ground * (1.0 - TERRA)

    # Tops step from pressure level to pressure level and the defaults from cell to cell; softened over a cell or so,
    # neighbouring columns blend instead of standing as blocks. Cover is ERA5's own smooth field, barely touched.
    def soften(field, sigma):

        return ndimage.gaussian_filter(field, sigma, mode=("nearest", "wrap"))

    low = soften(low, 0.6)
    convection = soften(convection, 1.2)
    bottom = soften(bottom, 1.2)
    thickness = soften(thickness, 1.2)
    anvil = soften(anvil, 0.8)
    anvil_bottom = soften(anvil_bottom, 1.2)
    anvil_top = soften(anvil_top, 1.2)

    channels = [
        low,
        convection,
        bottom / CLOUD_BASE_RANGE,
        (bottom + thickness) / CLOUD_TOP_RANGE,
        anvil,
        (anvil_bottom - lowered) / CLOUD_TOP_RANGE,
        (anvil_top - lowered) / CLOUD_TOP_RANGE,
        np.zeros_like(low),
    ]

    # ERA5's 721 rows sit on the grid lines, poles included; averaging neighbours puts them at the cells' centres.
    packed = []

    for channel in channels:

        centred = 0.5 * (channel[1:] + channel[:-1])
        centred = 0.5 * (centred + np.roll(centred, -1, axis=1))
        packed.append(np.round(np.clip(centred, 0.0, 1.0) * 255.0).astype(np.uint8))

    packed = np.stack(packed, axis=-1)

    with open(path + ".part", "wb") as out:

        np.array([CLOUD_MAGIC, packed.shape[0], packed.shape[1], packed.shape[2]], np.int32).tofile(out)
        packed.tofile(out)

    os.replace(path + ".part", path)


# --- Colour -----------------------------------------------------------------------------------------


def _decode_marble(name):

    col = "ABCD".index(name[0]) * 21_600
    row = (int(name[1]) - 1) * 21_600
    Image.MAX_IMAGE_PIXELS = None
    pixels = np.asarray(Image.open(os.path.join(SOURCES, f"bluemarble.{name}.png")).convert("RGB"))
    out = np.memmap(work("colour0.u8.part"), np.uint8, "r+", shape=(H, W, 3))
    out[row:row + 21_600, col:col + 21_600] = pixels
    out.flush()


def colour_mips():

    if all(os.path.exists(work(f"colour{m}.u8")) for m in range(MIPS)):

        return

    if not os.path.exists(work("colour0.u8")):

        log("decoding Blue Marble")
        np.memmap(work("colour0.u8.part"), np.uint8, "w+", shape=(H, W, 3)).flush()

        with Pool(4) as pool:

            pool.map(_decode_marble, [f"{c}{r}" for c in "ABCD" for r in "12"])

        os.replace(work("colour0.u8.part"), work("colour0.u8"))

    for m in range(1, MIPS):

        rows, cols = mip_shape(m)
        above = np.memmap(work(f"colour{m - 1}.u8"), np.uint8, "r", shape=(rows * 2, cols * 2, 3))
        here = np.memmap(work(f"colour{m}.u8.part"), np.uint8, "w+", shape=(rows, cols, 3))

        for r in range(0, rows, 1_024):

            block = LINEAR[above[r * 2:(r + 1_024) * 2]].reshape(-1, 2, cols, 2, 3).mean(axis=(1, 3))
            srgb = np.where(block <= 0.0031308, block * 12.92, 1.055 * np.power(block, 1.0 / 2.4) - 0.055)
            here[r:r + 1_024] = np.clip(np.round(srgb * 255.0), 0, 255).astype(np.uint8)

        here.flush()
        del here
        os.replace(work(f"colour{m}.u8.part"), work(f"colour{m}.u8"))


def tile_index(face, level, ty, tx):

    before = sum(4 ** l for l in range(LEVELS))

    return face * before + sum(4 ** l for l in range(level)) + ty * (1 << level) + tx


def _tile_row(task):

    face, level, ty = task
    normal, right, up = (np.array(v, np.float64) for v in FACES[face])
    m = LEVELS - 1 - level
    rows, cols = mip_shape(m)
    source = np.memmap(work(f"colour{m}.u8"), np.uint8, "r", shape=(rows, cols, 3))
    out = np.memmap(os.path.join(OUT, "colour.raw.part"), np.uint8, "r+")
    size = TILE << level

    for tx in range(1 << level):

        # Texel centres, row 0 at the bottom (b = -1), reaching BORDER texels past the tile.
        i = (np.arange(TEXELS) - BORDER + 0.5 + tx * TILE) / size * 2.0 - 1.0
        j = (np.arange(TEXELS) - BORDER + 0.5 + ty * TILE) / size * 2.0 - 1.0
        a, b = np.meshgrid(np.tan(i * math.pi / 4.0), np.tan(j * math.pi / 4.0))
        d = normal[None, None, :] + a[..., None] * right + b[..., None] * up
        d /= np.linalg.norm(d, axis=-1, keepdims=True)

        lat = np.degrees(np.arcsin(np.clip(d[..., 2], -1.0, 1.0)))
        lon = np.degrees(np.arctan2(d[..., 1], d[..., 0]))
        y = (90.0 - lat) / 180.0 * rows - 0.5
        x = (lon + 180.0) / 360.0 * cols - 0.5

        # Crop around the samples; columns wrap at the date line, and a crop spanning it is unwrapped.
        if x.max() - x.min() > cols / 2:

            x = np.where(x < cols / 2, x + cols, x)

        y0, y1 = max(0, int(np.floor(y.min())) - 4), min(rows, int(np.ceil(y.max())) + 5)
        x0, x1 = int(np.floor(x.min())) - 4, int(np.ceil(x.max())) + 5
        crop = np.take(source[y0:y1], np.arange(x0, x1) % cols, axis=1).astype(np.float32)

        texels = np.empty((TEXELS, TEXELS, 3), np.uint8)

        for c in range(3):

            sampled = ndimage.map_coordinates(crop[..., c], [y - y0, x - x0], order=3, mode="nearest")
            texels[..., c] = np.clip(np.round(sampled), 0, 255).astype(np.uint8)

        record = TEXELS * TEXELS * 3
        start = tile_index(face, level, ty, tx) * record
        out[start:start + record] = texels.ravel()

    out.flush()


def colour_tiles():

    path = os.path.join(OUT, "colour.raw")

    if os.path.exists(path) or os.path.exists(os.path.join(OUT, "colour.tiles")):

        return

    log("sampling colour tiles")
    total = 6 * sum(4 ** l for l in range(LEVELS)) * TEXELS * TEXELS * 3
    np.memmap(path + ".part", np.uint8, "w+", shape=(total,)).flush()
    tasks = [(f, l, ty) for f in range(6) for l in reversed(range(LEVELS)) for ty in range(1 << l)]

    with Pool(24) as pool:

        for done, _ in enumerate(pool.imap_unordered(_tile_row, tasks)):

            if done % 200 == 0:

                log(f"  {done}/{len(tasks)} tile rows")

    os.replace(path + ".part", path)


def main():

    os.makedirs(WORK, exist_ok=True)
    os.makedirs(OUT, exist_ok=True)

    elevation = assemble_elevation()
    water15 = decode_water()
    labels = water_bodies(water15)
    estimate = lake_levels(elevation, labels)
    body_levels, body_depths = hydrolakes(labels, estimate)
    rivers = river_network(elevation, labels, body_levels)
    write_rivers(rivers)
    cell_levels = water_levels(elevation, labels, body_levels, rivers)
    beds = lake_beds(labels, body_levels, body_depths)
    colour_mips()
    coast_distance()
    carve(elevation, water15, labels, cell_levels, beds, body_depths)
    shore_distance(water15)
    fetch(water15)
    sea_state()
    clouds()
    colour_tiles()
    log("bake complete")


if __name__ == "__main__":

    sys.exit(main())
