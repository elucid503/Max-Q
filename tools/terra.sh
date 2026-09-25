#!/usr/bin/env bash
# Download Terra's sources (GEBCO 2024, Blue Marble NG June 2004, ESA CCI water bodies; ~6.5 GB) into Data/Sources,
# bake the survey and colour into Data/Terra (~20 GB), then compress the colour tiles in Unity. Every step resumes.

set -euo pipefail

TOOLS="$(cd "$(dirname "$0")" && pwd)"
SOURCES="$TOOLS/../Data/Sources"
MARBLE="https://eoimages.gsfc.nasa.gov/images/imagerecords/76000/76487/world.200406.3x21600x21600"

mkdir -p "$SOURCES"

fetch() {

    [[ -f "$SOURCES/$2" ]] && return
    curl -fSL --retry 5 -C - -o "$SOURCES/$2.part" "$1"
    mv "$SOURCES/$2.part" "$SOURCES/$2"

}

fetch https://www.bodc.ac.uk/data/open_download/gebco/gebco_2024/geotiff/ gebco_2024_geotiff.zip
fetch https://dap.ceda.ac.uk/neodc/esacci/land_cover/data/water_bodies/v4.0/ESACCI-LC-L4-WB-Map-150m-P13Y-2000-v4.0.tif water.tif

for tile in A1 B1 C1 D1 A2 B2 C2 D2; do

    fetch "$MARBLE.$tile.png" "bluemarble.$tile.png"

done

[[ -d "$SOURCES/gebco" ]] || python -m zipfile -e "$SOURCES/gebco_2024_geotiff.zip" "$SOURCES/gebco"

python -m pip install --quiet --user numpy scipy pillow tifffile imagecodecs
python -u "$TOOLS/terra_bake.py"

if [[ -f "$TOOLS/../Data/Terra/colour.raw" ]]; then

    "$TOOLS/build.sh" --terra-tiles

fi
