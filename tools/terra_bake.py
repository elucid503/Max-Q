"""Bake Terra's surface from the sources in Data/Sources into Data/Terra.

elevation.i16  GEBCO 2024 heights in real metres on the 15" equirectangular grid (row 0 at the north
               edge), lake beds carved and shores banked, followed by six 2x2 box mips.
water.i16      water surface level in real metres on a 30" grid; -32768 where there is no water.
colour.raw     Blue Marble June 2004 as cube-face tiles (levels 0-6, 256 px: 248 plus a 4 px border),
               uncompressed; the Unity bake (Max-Q > Bake Terra Tiles) compresses it to colour.tiles.

Every step caches its output, so a rerun resumes where the last one stopped.
"""

import glob
import math
import os
import re
import sys
from multiprocessing import Pool

import imagecodecs
import numpy as np
import tifffile
from PIL import Image
from scipy import ndimage

DATA = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "Data")
SOURCES = os.path.join(DATA, "Sources")
WORK = os.path.join(SOURCES, "work")
OUT = os.path.join(DATA, "Terra")

W, H = 86_400, 43_200
MIPS = 7
NONE = -32_768

TILE = 248
BORDER = 4
TEXELS = TILE + 2 * BORDER
LEVELS = 7

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


def _water_chunk(chunk):

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

    water = (rows[:, :W * 3] == 2).reshape(256, 3, W, 3).sum(axis=(1, 3)) >= 5
    out = np.memmap(work("water15.u8.part"), np.uint8, "r+", shape=(H, W))
    last = min(256, H - chunk * 256)
    out[chunk * 256:chunk * 256 + last] = water[:last]
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


def carve(elevation, water15, labels, levels):

    path, water_path = os.path.join(OUT, "elevation.i16"), os.path.join(OUT, "water.i16")

    if os.path.exists(path) and os.path.exists(water_path):

        return

    log("carving lake beds and banking shores")
    out = np.memmap(path + ".part", np.int16, "w+", shape=(mip_offset(MIPS),))
    base = out[:H * W].reshape(H, W)
    cells = np.full((H // 2, W // 2), -np.inf, np.float32)
    band, halo = 2_048, 16
    lookup = np.where(np.isnan(levels), -np.inf, levels).astype(np.float32)

    for r0 in range(0, H, band):

        a, b = max(0, r0 - halo), min(H, r0 + band + halo)
        e = elevation[a:b].astype(np.float32)
        w = water15[a:b].astype(bool)
        level = np.repeat(np.repeat(lookup[labels[a // 2:b // 2]], 2, axis=0), 2, axis=1)

        near = ndimage.maximum_filter(level, size=5, mode=("nearest", "wrap"))
        level = np.where(w & ~np.isfinite(level), near, level)
        body = w & np.isfinite(level)

        # Euclidean distance from shore, so beds deepen in rounded contours rather than square steps.
        depth = np.minimum(ndimage.distance_transform_edt(body), 16.0)
        bed = body & (e >= level - 1.0) & (e <= level + 5.0)
        e = np.where(bed, level - (1.0 + 3.0 * depth), e)

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
    np.where(np.isfinite(region), region, NONE).astype(np.int16).tofile(water_path)


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

    linear = ((np.arange(256) / 255.0 + 0.055) / 1.055) ** 2.4
    linear[:11] = np.arange(11) / 255.0 / 12.92
    linear = linear.astype(np.float32)

    for m in range(1, MIPS):

        rows, cols = mip_shape(m)
        above = np.memmap(work(f"colour{m - 1}.u8"), np.uint8, "r", shape=(rows * 2, cols * 2, 3))
        here = np.memmap(work(f"colour{m}.u8.part"), np.uint8, "w+", shape=(rows, cols, 3))

        for r in range(0, rows, 1_024):

            block = linear[above[r * 2:(r + 1_024) * 2]].reshape(-1, 2, cols, 2, 3).mean(axis=(1, 3))
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
    levels = lake_levels(elevation, labels)
    carve(elevation, water15, labels, levels)
    colour_mips()
    colour_tiles()
    log("bake complete")


if __name__ == "__main__":

    sys.exit(main())
