"""Bake Selene's survey from Data/Sources/ldem_64.img (LRO LOLA) into Data/Selene.

Grids are equirectangular, row 0 at the north edge and column 0 at 180 W as Terra's are, each followed by 2x2 mips.

height.i16     LOLA's heights, real metres over 1737.4 km, at 64 to the degree (23040 x 11520), five mips.
maria.i16      how far the ground is mare, 0 to 1000, at 16 to the degree (5760 x 2880), four mips: smooth lowlands.
steepness.i16  share of each cell steep enough to shed its regolith, 0 to 1000, on the same cells: bright crater walls.
"""

import os

import numpy as np
from scipy import ndimage

DATA = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "Data")
SOURCE = os.path.join(DATA, "Sources", "ldem_64.img")
OUT = os.path.join(DATA, "Selene")

W, H = 23_040, 11_520
HEIGHT_MIPS = 6
CELL_MIPS = 5
POST = 1_737_400.0 * np.pi / 180.0 / 64.0

# Relief (m, rms over ~20 km) below which ground reads as mare, fully below MARE_SMOOTH; smoothed ground this high (m)
# and higher is never mare, and from MARE_LOW down fully so.
MARE_ROUGH = 125.0
MARE_SMOOTH = 70.0
MARE_HIGH = 500.0
MARE_LOW = -1_000.0

# Noise (in share of the cut) that makes the maria's shores wander, at scales (cells) from embayments tens of kilometres
# across down to a few, weighted so the broad ones lead; and the last blur (cells) that softens the shores. It works only
# along the shores, so no stray maria sprout in the highlands.
MARE_WANDER = 0.3
WANDER_SCALES = ((24.0, 0.6), (8.0, 0.3), (3.0, 0.1))
MARE_SOFTEN = 7.0

# Cells: smooth lowlands smaller than this are only crater floors, not maria, and highland gaps smaller than this within a
# mare are flooded craters, whose walls the steepness brightens instead.
MARE_SMALLEST = 5_000
HOLE_LARGEST = 2_000

# Slopes (rise over run) over which regolith starts and finishes sliding off.
STEEP_START = 0.12
STEEP_FULL = 0.4


def wrapped(filter, array, **kwargs):

    pad = 64

    return filter(np.pad(array, ((0, 0), (pad, pad)), mode="wrap"), mode="nearest", **kwargs)[:, pad:-pad]


def smoothstep(a, b, x):

    t = np.clip((x - a) / (b - a), 0.0, 1.0)

    return t * t * (3.0 - 2.0 * t)


def pooled(array, factor):

    rows, columns = array.shape

    return array.reshape(rows // factor, factor, columns // factor, factor).mean(axis=(1, 3))


def maria(height):

    coarse = pooled(height, 4)
    detail = coarse - wrapped(ndimage.gaussian_filter, coarse, sigma=2.0)
    rough = np.sqrt(wrapped(ndimage.gaussian_filter, detail * detail, sigma=8.0))
    low = wrapped(ndimage.gaussian_filter, coarse, sigma=12.0)
    latitude = 90.0 - (np.arange(coarse.shape[0]) + 0.5) * 180.0 / coarse.shape[0]

    # Near the poles a cell spans little ground east to west, so the relief reads smooth; no maria lie there anyway.
    share = smoothstep(MARE_ROUGH, MARE_SMOOTH, rough) * smoothstep(MARE_HIGH, MARE_LOW, low) * smoothstep(72.0, 62.0, np.abs(latitude))[:, None]

    # Blurred then cut again: craters punched through a mare stay mare. Cut through noise, its shores wander into bays and
    # headlands as lava flooding the lowlands leaves them, instead of the blur's rounded blobs.
    blurred = wrapped(ndimage.gaussian_filter, share, sigma=7.0)
    shore = 4.0 * blurred * (1.0 - blurred)
    mare = smoothstep(0.22, 0.5, blurred + MARE_WANDER * shore * wander(share.shape)) > 0.5
    mare = without_small(mare, MARE_SMALLEST)
    mare = ~without_small(~mare, HOLE_LARGEST)

    return wrapped(ndimage.gaussian_filter, mare.astype(np.float32), sigma=MARE_SOFTEN)


def without_small(mask, cells):

    labels, count = ndimage.label(mask)
    sizes = np.bincount(labels.ravel(), minlength=count + 1)
    keep = sizes >= cells
    keep[0] = False

    return keep[labels]


def wander(shape):

    random = np.random.default_rng(11)
    field = np.zeros(shape, dtype=np.float32)

    for sigma, weight in WANDER_SCALES:

        noise = wrapped(ndimage.gaussian_filter, random.standard_normal(shape).astype(np.float32), sigma=sigma)
        field += weight * noise / noise.std()

    return field


def steepness(height):

    latitude = np.radians(90.0 - (np.arange(H) + 0.5) / 64.0)
    east = (np.roll(height, -1, 1) - np.roll(height, 1, 1)) / (2.0 * POST * np.maximum(np.cos(latitude), 0.05))[:, None]
    north = (np.roll(height, -1, 0) - np.roll(height, 1, 0)) / (2.0 * POST)

    return pooled(smoothstep(STEEP_START, STEEP_FULL, np.hypot(east, north)), 4)


def write(name, level, mips, scale):

    with open(os.path.join(OUT, name + ".part"), "wb") as out:

        for mip in range(mips):

            np.round(level * scale).astype("<i2").tofile(out)
            level = pooled(level, 2)

    os.replace(os.path.join(OUT, name + ".part"), os.path.join(OUT, name))


def main():

    os.makedirs(OUT, exist_ok=True)

    # LOLA's first column is at 0 E.
    height = np.roll(np.fromfile(SOURCE, dtype="<i2").reshape(H, W).astype(np.float32) * 0.5, -W // 2, axis=1)

    write("height.i16", height, HEIGHT_MIPS, 1.0)
    write("maria.i16", maria(height), CELL_MIPS, 1_000.0)
    write("steepness.i16", steepness(height), CELL_MIPS, 1_000.0)


if __name__ == "__main__":

    main()
