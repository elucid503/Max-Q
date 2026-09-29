#!/usr/bin/env bash
# Download Selene's source (LRO LOLA's LDEM_64, ~530 MB) into Data/Sources and bake the survey into Data/Selene
# (~800 MB). Every step resumes.

set -euo pipefail

TOOLS="$(cd "$(dirname "$0")" && pwd)"
SOURCES="$TOOLS/../Data/Sources"

mkdir -p "$SOURCES"

fetch() {

    [[ -f "$SOURCES/$2" ]] && return
    curl -fSL --retry 5 -C - -o "$SOURCES/$2.part" "$1"
    mv "$SOURCES/$2.part" "$SOURCES/$2"

}

fetch https://pds-geosciences.wustl.edu/lro/lro-l-lola-3-rdr-v1/lrolol_1xxx/data/lola_gdr/cylindrical/img/ldem_64.img ldem_64.img

python -m pip install --quiet --user numpy scipy
python -u "$TOOLS/selene_bake.py"
