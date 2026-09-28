"""Bake Terra's survey from Data/Sources into Data/Terra.

Every grid is equirectangular with row 0 at the north edge and column 0 at 180 W; heights and distances are real Earth
metres, which the sim scales to Terra. One grid family, so land and water cannot disagree: the coast is wherever the
ground crosses the water's level.

height.i16          GEBCO 2024 on 2.5' posts (8640 x 4320), then five 2x2 mips. Posts the ESA CCI mask calls water lie
                    under their body's level, land that shares a level cell's reach stands over it, and each mip keeps
                    its posts on the side of the level their majority is on, bathymetry clipped to what shading needs.
water.i16           water level on 5' cells (4320 x 2160), whole metres: the sea at zero, lakes at HydroLAKES' levels,
                    reaching REACH past each body's cells; -32768 beyond. Then four mips, one to each height mip past
                    the first, each reaching two of its cells past the water.
shore_distance.i16  signed distance to the shore on the 5' cells, in 32 m units, negative over water.
moisture.i16        how wet the climate keeps the land, 0 to 1000, on 0.25 degree cells (1440 x 720): rain belts by
                    latitude, fed by prevailing winds that dry out over land and over mountains.

Steps cache their results in Data/Sources/work, so a rerun resumes.
"""

import glob
import math
import os
import re
import sys
from multiprocessing import Pool

import imagecodecs
import numpy as np
import pyogrio
import tifffile
from scipy import ndimage, sparse
from scipy.sparse import csgraph

DATA = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "Data")
SOURCES = os.path.join(DATA, "Sources")
WORK = os.path.join(SOURCES, "work")
OUT = os.path.join(DATA, "Terra")

WATER_MASK = os.path.join(SOURCES, "water.tif")
LAKES = "/vsizip/" + os.path.join(SOURCES, "HydroLAKES_points_v10_shp.zip").replace("\\", "/") + "/HydroLAKES_points_v10_shp/HydroLAKES_points_v10.shp"

W, H = 8_640, 4_320
MIPS = 6
NONE = -32_768

# Metres of Earth per degree of latitude, per 2.5' post and per 5' cell.
DEGREE = 111_195.0
POST = DEGREE / 24.0
CELL = 2.0 * POST

# Land that shares a level cell's reach stands this far over the water (m), and no lake may lift it further than
# PERCHED to get there.
MARGIN = 1.0
PERCHED = 150.0

# Lakes this many posts or larger (~5,000 square km) are never left dry as perched; their shores are the mask's to fix.
GREAT_LAKE = 500

# Water posts whose survey stands this far over their body are rivers or the mask's mistakes, and stay land.
RIVER = 10.0

# How far past its own cells a body's level reaches (m); the shore distance's unit and range (m).
REACH = 30_000.0
SHORE_UNIT = 32.0
SHORE_RANGE = 1_000_000.0

# The mask's 5" pixels in each 2.5' post, and its 256 px tiles in each band of posts.
PIXELS = 30
BAND_TILES = 15
BAND_POSTS = BAND_TILES * 256 // PIXELS


def log(message):

    print(message, flush=True)


def work(name):

    return os.path.join(WORK, name)


def cached(name, shape, dtype, make):

    path = work(name)

    if not os.path.exists(path):

        make().astype(dtype).tofile(path + ".part")
        os.replace(path + ".part", path)

    return np.fromfile(path, dtype).reshape(shape)


def pooled(array, factor, reduce=np.mean):

    rows, cols = array.shape

    return reduce(array.reshape(rows // factor, factor, cols // factor, factor), axis=(1, 3))


def wrapped(filter, array, size, **kwargs):

    # Filters wrap across the date line; rows stop at the poles.
    pad = size // 2
    padded = np.pad(array, ((pad, pad), (pad, pad)), mode="edge")
    padded[:, :pad] = padded[:, -2 * pad:-pad]
    padded[:, -pad:] = padded[:, pad:2 * pad]

    return filter(padded, size=size, mode="nearest", **kwargs)[pad:-pad, pad:-pad]


# --- Sources ----------------------------------------------------------------------------------------


def gebco():

    def make():

        log("averaging GEBCO onto 2.5' posts")
        out = np.empty((H, W), np.float32)

        for tif in glob.glob(os.path.join(SOURCES, "gebco", "*.tif")):

            n, _, w, _ = (float(v) for v in re.search(r"n(-?\d+\.\d)_s(-?\d+\.\d)_w(-?\d+\.\d)_e(-?\d+\.\d)", tif).groups())
            row, col = int((90.0 - n) * 24), int((w + 180.0) * 24)
            out[row:row + 2_160, col:col + 2_160] = pooled(tifffile.imread(tif).astype(np.float32), 10)

        return out

    return cached("gebco_2.5.f32", (H, W), np.float32, make)


def _water_band(band):

    # One band of posts: the share of each post's 5" pixels the mask calls water (value 2), out of 255.
    tif = tifffile.TiffFile(WATER_MASK)
    page = tif.pages[0]
    handle = tif.filehandle
    across = -(-page.shape[1] // 256)
    down = -(-page.shape[0] // 256)
    rows = min(BAND_POSTS, H - band * BAND_POSTS)
    out = np.zeros((rows, W), np.uint8)
    block = np.zeros((BAND_TILES * 256, BAND_TILES * 256), np.uint8)

    for c0 in range(0, across, BAND_TILES):

        block[:] = 0

        for i in range(min(BAND_TILES, down - band * BAND_TILES)):

            for j in range(min(BAND_TILES, across - c0)):

                index = (band * BAND_TILES + i) * across + c0 + j
                handle.seek(page.dataoffsets[index])
                data = imagecodecs.lzw_decode(handle.read(page.databytecounts[index]), out=65_536)
                block[i * 256:(i + 1) * 256, j * 256:(j + 1) * 256] = np.frombuffer(data, np.uint8)[:65_536].reshape(256, 256)

        share = pooled((block == 2).astype(np.float32), PIXELS)
        first = c0 * 256 // PIXELS
        count = min(BAND_POSTS, W - first)
        out[:, first:first + count] = np.round(share[:rows, :count] * 255.0)

    return out


def water_share():

    def make():

        log("measuring the ESA CCI water mask on 2.5' posts")

        with Pool(12) as pool:

            return np.concatenate(pool.map(_water_band, range(-(-H // BAND_POSTS))))

    return cached("water_2.5.u8", (H, W), np.uint8, make)


def hydrolakes():

    # Lakes under 2 km^2 cannot fit a body of 2.5' posts within a factor of five.
    meta, _, _, fields = pyogrio.raw.read(LAKES, columns=["Lake_area", "Elevation", "Pour_long", "Pour_lat"], read_geometry=False, where="Lake_area >= 2")
    f = dict(zip(meta["fields"], fields))
    row = np.clip(((90.0 - f["Pour_lat"]) * 24.0).astype(np.int64), 0, H - 1)
    col = ((f["Pour_long"] + 180.0) * 24.0).astype(np.int64) % W

    return row, col, f["Lake_area"].astype(np.float64), f["Elevation"].astype(np.float64)


# --- Water bodies -----------------------------------------------------------------------------------


def bodies(wet):

    # Connected water, 8-connected, joined across the date line.
    labels, count = ndimage.label(wet, structure=np.ones((3, 3), bool))
    a, b = [], []

    for shift in (-1, 0, 1):

        west = labels[max(0, shift):H + min(0, shift), 0]
        east = labels[max(0, -shift):H + min(0, -shift), -1]
        touch = (west > 0) & (east > 0)
        a.append(west[touch])
        b.append(east[touch])

    a, b = np.concatenate(a), np.concatenate(b)
    graph = sparse.coo_matrix((np.ones(len(a)), (a, b)), shape=(count + 1, count + 1))
    _, merged = csgraph.connected_components(graph, directed=False)
    _, merged = np.unique(merged[1:], return_inverse=True)
    lookup = np.concatenate([[0], merged + 1]).astype(np.int32)

    return lookup[labels], int(merged.max()) + 1


def body_levels(labels, count, height):

    log("levelling lakes")
    sizes = np.bincount(labels.ravel(), minlength=count + 1)
    ocean = int(np.argmax(sizes[1:])) + 1

    # Most lakes are flat in GEBCO at their surface, which stands where it is. The rest show their bathymetry and take
    # HydroLAKES' level; unmatched, those under the sea's level are its pieces the mask cuts off behind straits narrower
    # than a post, and the others are the mask's mistakes, left dry.
    median = np.full(count + 1, np.nan)
    median[1:] = ndimage.median(height, labels, np.arange(1, count + 1))
    flat = np.bincount(labels.ravel(), weights=(np.abs(height - median[labels]) <= 2.0).ravel(), minlength=count + 1) >= 0.5 * sizes
    levels = np.where(flat, median, np.where(median < -5.0, 0.0, np.nan))

    # A pour point sits at its lake's outlet, often a few posts down the river leaving it: of the bodies around it, the
    # lake is the one whose area fits it best, within a factor of five. A body takes the largest lake matched to it.
    row, col, area, elevation = hydrolakes()
    latitude = np.cos(np.radians(90.0 - (np.arange(H) + 0.5) / 24.0))
    body_area = np.bincount(labels.ravel(), weights=np.repeat(latitude, W), minlength=count + 1) * (POST / 1_000.0) ** 2
    body = np.zeros(len(row), np.int64)
    fit = np.full(len(row), math.log(5.0))

    for dr in range(-12, 13):

        for dc in range(-12, 13):

            candidate = labels[np.clip(row + dr, 0, H - 1), (col + dc) % W]
            score = np.abs(np.log(area / np.maximum(body_area[candidate], 1e-6)))
            better = (candidate > 0) & (candidate != ocean) & (score < fit)
            body[better], fit[better] = candidate[better], score[better]

    keep = (body > 0) & ~flat[body]
    order = np.lexsort((area[keep], body[keep]))[::-1]
    matched, first = np.unique(body[keep][order], return_index=True)
    levels[matched] = elevation[keep][order][first]
    log(f"  {count} bodies, {int(flat.sum())} flat in the survey, {len(matched)} more matched to HydroLAKES")

    levels[np.abs(levels) <= 3.0] = 0.0
    levels[ocean] = 0.0
    levels[0] = np.nan

    return np.round(levels)


def widened(labels, levels, wet, share, height):

    # The mask calls posts only partly under a lake land, though the survey often shows its bed there; left dry, they
    # would rise over the level as walls. Posts at least a quarter wet beside a body and under its level join it:
    # they line the shore, so two rings of posts reach them.
    for _ in range(2):

        grown = wrapped(ndimage.maximum_filter, np.where(wet & np.isfinite(levels[labels]), labels, 0), 3)
        low = ~wet & (share >= 64) & (grown > 0) & (height < levels[grown] - RIVER)
        labels = np.where(low, grown, labels)
        wet = wet | low

    return labels, wet


def signed_distance(wet, spacing, reach):

    # Metres from each cell to the nearest shore, negative in water, out to reach. Bands of rows take the column spacing
    # of their own latitude and wrap across the date line.
    rows, cols = wet.shape
    out = np.empty(wet.shape, np.float32)
    band = max(1, rows // 180)
    halo = int(math.ceil(reach / spacing)) + 2

    for r0 in range(0, rows, band):

        a, b = max(0, r0 - halo), min(rows, r0 + band + halo)
        across = spacing * max(math.cos(math.radians(90.0 - (r0 + 0.5 * band) * 180.0 / rows)), 0.01)
        pad = min(int(math.ceil(reach / across)) + 2, cols // 2)
        block = np.pad(wet[a:b], ((0, 0), (pad, pad)), mode="wrap")

        if not block.any() or block.all():

            out[r0:r0 + band] = reach if not block.any() else -reach

            continue

        inside = ndimage.distance_transform_edt(block, sampling=(spacing, across))
        outside = ndimage.distance_transform_edt(~block, sampling=(spacing, across))
        half = 0.5 * math.sqrt(spacing * across)
        signed = np.where(block, half - inside, outside - half)
        out[r0:r0 + band] = signed[r0 - a:r0 - a + band, pad:pad + cols]

    return np.clip(out, -reach, reach)


def water_cells(labels, levels, wet, height):

    log("reaching levels over the 5' cells")

    # A post is water only where its survey does not stand well over its body; a cell takes the lowest level among its
    # water posts, so no water stands over another's bed, and every cell within REACH of one takes the nearest's, save
    # where its ground lies below it, a polder behind the dikes, or falls far below it within reach of its posts, a
    # valley beside a high lake.
    post_level = np.where(wet, levels[labels], np.nan)
    post_level[post_level + RIVER < height] = np.nan
    water = np.isfinite(post_level)
    cells = pooled(np.where(water, post_level, np.inf), 2, np.min)
    own = np.isfinite(cells)
    shore = signed_distance(pooled(water.astype(np.float32), 2) >= 0.5, CELL, SHORE_RANGE)
    pad = 64
    near = np.pad(~own, ((0, 0), (pad, pad)), mode="wrap")
    nearest = ndimage.distance_transform_edt(near, return_distances=False, return_indices=True)[:, :, pad:-pad]
    reached = cells[nearest[0], (nearest[1] - pad) % (W // 2)]
    lowest = pooled(wrapped(ndimage.minimum_filter, height, 5), 2, np.min)
    cells = np.where(own | ((shore <= REACH) & (pooled(height, 2) >= reached) & (lowest >= reached - PERCHED)), reached, np.nan)

    return water, post_level, cells, shore


# --- Ground ---------------------------------------------------------------------------------------


def shape_ground(height, water, post_level, cells):

    log("laying beds under the water and land over it")
    e = height.copy()

    # Beds shelve from the shore, 2 m down a post in; the survey stands where it is deeper.
    inside = ndimage.distance_transform_edt(water)
    e = np.where(water, np.minimum(e, post_level - 2.0 * inside), e)

    # Land stands over the highest level of the cells whose ground it shapes: a point in a cell blends the posts up to two
    # beyond the cell's own.
    level = np.repeat(np.repeat(cells, 2, axis=0), 2, axis=1)
    over = wrapped(ndimage.maximum_filter, np.nan_to_num(level, nan=-np.inf), 5)

    return np.where(~water & np.isfinite(over), np.maximum(e, over + MARGIN), e)


def level_pool(levels, reduce, fill):

    pooled_levels = pooled(np.nan_to_num(levels, nan=fill), 2, reduce)

    return np.where(np.isfinite(pooled_levels), pooled_levels, np.nan)


def mips(e, water, post_level, cells):

    # Each mip keeps its posts on the side of the level their majority is on: water posts under it, no deeper than
    # shading needs, and land within two posts of water over it, save land below it, which the level leaves out. The
    # level reaches as far at each mip, so no coarse coast ends at a cell's edge.
    log("building mips")
    heights = [np.round(e).astype(np.int16)]
    levels = [cells]
    share = water.astype(np.float32)
    own = post_level

    for m in range(1, MIPS):

        # Metres a mip's posts keep from the level; beds lie no deeper than thirty times that, still deeper than any
        # water shows its bed.
        margin = 2.5 * 2 ** m
        e = pooled(e, 2)
        share = pooled(share, 2)
        own = level_pool(own, np.min, np.inf)
        near = cells

        if m > 1:

            near = wrapped(ndimage.maximum_filter, np.nan_to_num(own, nan=-np.inf), 5)
            near = np.where(np.isfinite(near) & (np.isfinite(own) | (e >= near)), near, np.nan)

        wet = np.isfinite(own) & (share >= 0.5)
        e = np.where(wet, np.clip(e, own - 30.0 * margin, own - margin), e)
        e = np.where(~wet & np.isfinite(near), np.maximum(e, near + margin), e)
        heights.append(np.round(e).astype(np.int16))

        if m > 1:

            levels.append(np.where(wet, own, near))

    return heights, levels


# --- Climate --------------------------------------------------------------------------------------


def _carried(sea, ground, step, heading, reach):

    # The share of the sea's water the wind still carries over each cell, blowing east (heading 1) or west (-1) twice
    # round the world so it arrives settled, losing it over land across reach metres.
    rows, cols = sea.shape
    carried = np.ones(rows)
    out = np.zeros((rows, cols))

    for k in range(2 * cols):

        c = (heading * k) % cols
        climb = np.maximum(ground[:, c] - ground[:, (c - heading) % cols], 0.0)
        dried = carried * np.exp(-step / reach - climb / 2_500.0)
        recharged = 1.0 - (1.0 - carried) * np.exp(-step / 600_000.0)
        carried = sea[:, c] * recharged + (1.0 - sea[:, c]) * dried
        out[:, c] = carried

    return out


def moisture(height, sea):

    log("raining on the land")
    rows = H // 6
    sea = pooled(sea.astype(np.float32), 6)
    ground = pooled(np.maximum(height, 0.0), 6)
    latitude = 90.0 - (np.arange(rows) + 0.5) * 180.0 / rows
    step = DEGREE * 0.25 * np.maximum(np.cos(np.radians(latitude)), 0.05)

    # Trade winds and polar easterlies blow west, the westerlies east, handing over across a band ten degrees wide on
    # either side of the subtropical highs. Air takes up water over the sea, and loses it over land and as it climbs;
    # the westerlies' storms carry it further than the trades, the more so toward the poles where the cold air loses it
    # slower, and also wet the coasts downwind of a continent.
    band = np.abs(latitude - 2.5)
    westerly = (np.clip((band - 27.5) / 10.0, 0.0, 1.0) * np.clip((70.0 - band) / 10.0, 0.0, 1.0))[:, None]
    coast = np.exp(-ndimage.distance_transform_edt(sea < 0.5, sampling=DEGREE * 0.25) / 800_000.0)
    trades = _carried(sea, ground, step, -1, 2_000_000.0)
    storms = np.maximum(_carried(sea, ground, step, 1, 3_500_000.0 + 5_000_000.0 * np.clip((np.abs(latitude) - 45.0) / 20.0, 0.0, 1.0)), coast)
    moist = (1.0 - westerly) * trades + westerly * storms

    # The tropical rain belt, near 3 N in the mean, rains on its own water whatever the wind; elsewhere rain falls from
    # the air the wind brings, most readily under the storm tracks of the mid-latitudes.
    tropical = np.exp(-((latitude - 3.0) / 14.0) ** 4)[:, None]
    readily = 0.75 + 0.25 * np.exp(-((np.abs(latitude) - 45.0) / 20.0) ** 2)[:, None]
    wet = wrapped(ndimage.uniform_filter, 0.9 * tropical + (1.0 - 0.9 * tropical) * readily * moist, 9)

    return np.round(np.clip(wet, 0.0, 1.0) * 1_000.0).astype(np.int16)


# --- Output -----------------------------------------------------------------------------------------


def write(name, arrays):

    path = os.path.join(OUT, name)

    with open(path + ".part", "wb") as out:

        for array in arrays:

            array.tofile(out)

    os.replace(path + ".part", path)
    log(f"  {name}: {os.path.getsize(path) / 1e6:.1f} MB")


def main():

    os.makedirs(WORK, exist_ok=True)
    os.makedirs(OUT, exist_ok=True)

    height = gebco()
    share = water_share()
    wet = share >= 128
    labels, count = bodies(wet)
    levels = body_levels(labels, count, height)
    labels, wet = widened(labels, levels, wet, share, height)

    # A lake perched over ground far below it, which no post between could climb to its level, is left dry: the lakes
    # standing that far over the survey of land raised beside them, not every water near the cliff.
    while True:

        water, post_level, cells, shore = water_cells(labels, levels, wet, height)
        ground = shape_ground(height, water, post_level, cells)
        raised = np.where(ground - height > PERCHED, height, np.inf)
        below = wrapped(ndimage.minimum_filter, raised, 7)
        perched = np.unique(labels[water & (post_level - below > PERCHED)])
        perched = perched[(levels[perched] != 0.0) & (np.bincount(labels.ravel(), minlength=count + 1)[perched] < GREAT_LAKE)]

        if len(perched) == 0:

            break

        log(f"  leaving {len(perched)} perched lakes dry")
        levels[perched] = np.nan

    log("writing Data/Terra")
    heights, levels = mips(ground, water, post_level, cells)
    write("height.i16", heights)
    write("water.i16", [np.where(np.isfinite(level), level, NONE).astype(np.int16) for level in levels])
    write("shore_distance.i16", [np.clip(np.round(shore / SHORE_UNIT), -32_767, 32_767).astype(np.int16)])
    # The sea that waters the land: the oceans and seas, not the lagoons and lakes that stand near sea level.
    write("moisture.i16", [moisture(ground, (post_level == 0.0) & (np.bincount(labels.ravel())[labels] >= 5_000))])
    log("bake complete")


if __name__ == "__main__":

    sys.exit(main())
