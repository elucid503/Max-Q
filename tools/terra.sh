#!/usr/bin/env bash
# Download Terra's sources (GEBCO 2024 and the ESA CCI water bodies, ~4.5 GB) into Data/Sources and bake the survey into
# Data/Terra (~145 MB). HydroLAKES sits behind a bot check: download it by hand from the page printed when it is missing.
# Every step resumes.

set -euo pipefail

TOOLS="$(cd "$(dirname "$0")" && pwd)"
SOURCES="$TOOLS/../Data/Sources"

mkdir -p "$SOURCES"

fetch() {

    [[ -f "$SOURCES/$2" ]] && return
    curl -fSL --retry 5 -C - -o "$SOURCES/$2.part" "$1"
    mv "$SOURCES/$2.part" "$SOURCES/$2"

}

fetch https://www.bodc.ac.uk/data/open_download/gebco/gebco_2024/geotiff/ gebco_2024_geotiff.zip
fetch https://dap.ceda.ac.uk/neodc/esacci/land_cover/data/water_bodies/v4.0/ESACCI-LC-L4-WB-Map-150m-P13Y-2000-v4.0.tif water.tif

if [[ ! -f "$SOURCES/HydroLAKES_points_v10_shp.zip" ]]; then

    echo "Missing Data/Sources/HydroLAKES_points_v10_shp.zip: download it from https://www.hydrosheds.org/products/hydrolakes"
    exit 1

fi

[[ -d "$SOURCES/gebco" ]] || python -m zipfile -e "$SOURCES/gebco_2024_geotiff.zip" "$SOURCES/gebco"

python -m pip install --quiet --user numpy scipy tifffile imagecodecs pyogrio
python -u "$TOOLS/terra_bake.py"
