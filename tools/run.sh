#!/usr/bin/env bash
# Run the game without the editor, rebuilding the player first if the sources changed since the
# last build. Extra args go to the player (e.g. -capture <dir>, -screen-fullscreen 0).

set -euo pipefail

TOOLS="$(cd "$(dirname "$0")" && pwd)"
PROJECT="$(cd "$TOOLS/.." && pwd)"
EXE="$PROJECT/Builds/Windows/Max-Q.exe"
STAMP="$PROJECT/Builds/Windows/.sources"

# Content, not timestamps: opening the project in Unity rewrites files without changing them.
sources() {

    cd "$PROJECT"
    find Assets ProjectSettings Packages/manifest.json -type f -print0 | sort -z | xargs -0 md5sum | md5sum | cut -d' ' -f1

}

if [[ ! -f "$EXE" || "$(cat "$STAMP" 2> /dev/null)" != "$(sources)" ]]; then

    "$TOOLS/build.sh"
    sources > "$STAMP"

fi

exec "$EXE" "$@"
