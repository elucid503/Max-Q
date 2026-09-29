"""Bake the tileable regolith material (3 m repeat, 1024 texels) into Assets/Game/Art/Ground: grey albedo with height in
alpha, and OpenGL normals. Hummocks, craters from 1 to 30 cm, clods and grain.
"""

import os
import zlib
import struct

import numpy as np
from scipy import ndimage

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "Assets", "Game", "Art", "Ground")

SIZE = 1024
TILE = 3.0
TEXEL = TILE / SIZE

# Metres of relief the alpha spans; must match the regolith's parallax depth in Regolith.shader.
HEIGHT_RANGE = 0.06

# Hummocks: rms height (m), and the spectrum's slope and longest wavelength (tiles).
HUMMOCK_RMS = 0.006
HUMMOCK_SLOPE = 1.6
HUMMOCK_LONGEST = 0.5

# Craters: octaves of diameter halving from the largest, each covering this share of the ground (saturated regolith);
# a fresh one a fifth as deep as it is wide under a rim a twenty-fifth as high, ejecta to REACH radii.
CRATER_LARGEST = 0.3
CRATER_OCTAVES = 5
CRATER_COVER = 0.14
DEPTH_RATIO = 0.18
RIM_RATIO = 0.035
REACH = 2.2

# Rims and walls are ragged by this share of each crater's relief.
RAGGED = 0.25

# Grain: rms height (m) of the texel-scale roughness that makes the powder read as powder.
GRAIN_RMS = 0.0006

# Clods and fragments per tile and their sizes (m).
CLODS = 1400
CLOD_SMALLEST = 0.008
CLOD_LARGEST = 0.045

rng = np.random.default_rng(1969)


def spectral(slope, longest, rms):

    k = np.fft.fftfreq(SIZE, d=1.0 / SIZE)
    kk = np.sqrt(k[:, None] ** 2 + k[None, :] ** 2)
    shape = np.where(kk >= 1.0 / longest, np.maximum(kk, 1e-6) ** -(slope + 1.0) / 2.0, 0.0)
    field = np.real(np.fft.ifft2(np.fft.fft2(rng.standard_normal((SIZE, SIZE))) * shape))

    return field / field.std() * rms


def wrapped_offsets(cx, cy, radius):

    """Texel offsets (metres) from a centre to every texel within radius, wrapping at the tile's edges."""

    reach = int(np.ceil(radius / TEXEL)) + 1
    col = int(cx / TEXEL)
    row = int(cy / TEXEL)
    rows = (np.arange(row - reach, row + reach + 1)) % SIZE
    cols = (np.arange(col - reach, col + reach + 1)) % SIZE
    dy = (np.arange(row - reach, row + reach + 1) + 0.5) * TEXEL - cy
    dx = (np.arange(col - reach, col + reach + 1) + 0.5) * TEXEL - cx

    return np.ix_(rows, cols), dx[None, :], dy[:, None]


def craters(height, albedo):

    ragged = spectral(1.0, 0.05, 1.0)
    diameter = CRATER_LARGEST

    for octave in range(CRATER_OCTAVES):

        count = int(CRATER_COVER * TILE * TILE / (np.pi * (0.75 * diameter / 2.0) ** 2))

        for _ in range(count):

            r = 0.5 * diameter * 2.0 ** (-np.sqrt(rng.random()))
            age = np.sqrt(rng.random())
            depth = 2.0 * r * DEPTH_RATIO * (1.0 - 0.85 * age)
            rim = 2.0 * r * RIM_RATIO * (1.0 - 0.9 * age)
            where, dx, dy = wrapped_offsets(rng.random() * TILE, rng.random() * TILE, REACH * r)
            x = np.sqrt(dx * dx + dy * dy) / r
            apron = REACH ** -3.0
            sharp = np.where(x < 1.0, -depth + (depth + rim) * x * x, rim * (np.maximum(x, 1.0) ** -3.0 - apron) / (1.0 - apron))
            t = np.clip(x / 1.3, 0.0, 1.0)
            soft = -depth * (1.0 - t * t * (3.0 - 2.0 * t))
            profile = np.where(x < REACH, sharp + (soft - sharp) * age, 0.0)

            height[where] += profile * (1.0 + RAGGED * ragged[where])

            # Young craters dig up fresher, brighter soil.
            if age < 0.25:

                albedo[where] *= 1.0 + 0.25 * (1.0 - age / 0.25) * np.exp(-(x / 1.2) ** 4)

        diameter *= 0.5


def clods(height, albedo):

    for _ in range(CLODS):

        size = CLOD_SMALLEST * (CLOD_LARGEST / CLOD_SMALLEST) ** (rng.random() ** 2.5)
        where, dx, dy = wrapped_offsets(rng.random() * TILE, rng.random() * TILE, 0.6 * size)
        stretch = 0.7 + 0.6 * rng.random()
        angle = rng.random() * np.pi
        u = (dx * np.cos(angle) + dy * np.sin(angle)) / stretch
        v = -dx * np.sin(angle) + dy * np.cos(angle)
        d = np.sqrt(u * u + v * v) / (0.5 * size)
        dome = np.sqrt(np.clip(1.0 - d * d, 0.0, 1.0))
        rock = rng.random() < 0.35

        height[where] += dome * size * (0.35 if rock else 0.2)

        # Fragments stand out bright or dark; clods are a shade darker.
        tone = rng.choice([1.45, 0.7]) if rock else 0.9
        albedo[where] = albedo[where] * (1.0 - dome) + albedo[where] * tone * dome


def encode_png(path, rgba):

    raw = b"".join(b"\x00" + row.tobytes() for row in rgba)

    def chunk(kind, data):

        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    header = struct.pack(">IIBBBBB", SIZE, SIZE, 8, 6, 0, 0, 0)

    with open(path, "wb") as out:

        out.write(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def srgb(linear):

    return np.where(linear <= 0.0031308, 12.92 * linear, 1.055 * np.power(np.maximum(linear, 0.0), 1.0 / 2.4) - 0.055)


def main():

    height = spectral(HUMMOCK_SLOPE, HUMMOCK_LONGEST, HUMMOCK_RMS)
    albedo = np.exp(spectral(1.0, 0.25, 0.06))

    craters(height, albedo)
    clods(height, albedo)
    height += spectral(0.0, 0.01, GRAIN_RMS)

    # Fine grain the texels can just carry.
    albedo *= np.exp(spectral(0.3, 0.02, 0.05))

    # Cavity darkening for shadow the lighting cannot resolve.
    cavity = ndimage.gaussian_filter(height, 3.0, mode="wrap") - height
    albedo *= np.clip(1.0 - cavity / 0.004 * 0.15, 0.7, 1.0)

    linear = 0.3 * albedo / albedo.mean()
    alpha = np.clip(0.5 + (height - np.median(height)) / HEIGHT_RANGE, 0.0, 1.0)

    # OpenGL normals: green rises toward the image's top, where rows fall.
    dx = (np.roll(height, -1, axis=1) - np.roll(height, 1, axis=1)) / (2.0 * TEXEL)
    dy = -(np.roll(height, -1, axis=0) - np.roll(height, 1, axis=0)) / (2.0 * TEXEL)
    normal = np.stack([-dx, -dy, np.ones_like(dx)], axis=-1)
    normal /= np.linalg.norm(normal, axis=-1, keepdims=True)

    grey = np.round(np.clip(srgb(np.clip(linear, 0.0, 1.0)), 0.0, 1.0) * 255.0).astype(np.uint8)
    rgba = np.stack([grey, grey, grey, np.round(alpha * 255.0).astype(np.uint8)], axis=-1)
    encoded = np.round((normal * 0.5 + 0.5) * 255.0).astype(np.uint8)
    normals = np.concatenate([encoded, np.full((SIZE, SIZE, 1), 255, np.uint8)], axis=-1)

    encode_png(os.path.join(OUT, "regolith_albedo_height.png"), rgba)
    encode_png(os.path.join(OUT, "regolith_normal.png"), normals)

    stored = (grey / 255.0)
    mean = np.where(stored <= 0.04045, stored / 12.92, ((stored + 0.055) / 1.055) ** 2.4).mean()
    print(f"mean linear albedo {mean:.4f}, alpha clipped {np.mean((alpha <= 0.0) | (alpha >= 1.0)):.4f}, height sd {height.std() * 1000:.1f} mm")


if __name__ == "__main__":

    main()
