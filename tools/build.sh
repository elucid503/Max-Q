#!/usr/bin/env bash
# Build the Windows player into Builds/Windows, or run the sim tests with --tests [results.xml].
# Unity's output goes to Logs/unity.log. Override the engine with UNITY=/path/to/Unity.exe.

set -euo pipefail

UNITY="${UNITY:-C:/Program Files/Unity/Hub/Editor/6000.5.10f1/Editor/Unity.exe}"
PROJECT="$(cd "$(dirname "$0")/.." && pwd)"
LOG="$PROJECT/Logs/unity.log"
RESULTS="${2:-$PROJECT/Logs/tests.xml}"

if [[ "${1:-}" == "--tests" ]]; then

    TASK="Testing"
    ARGS=(-runTests -testPlatform EditMode -testResults "$RESULTS")

else

    TASK="Building player"
    ARGS=(-quit -executeMethod MaxQ.Game.Editor.Build.Windows)

    # A stopped or failed build may leave a broken player; make run.sh rebuild it.
    rm -f "$PROJECT/Builds/Windows/.sources"

fi

mkdir -p "$PROJECT/Logs"
"$UNITY" -batchmode -projectPath "$PROJECT" "${ARGS[@]}" -logFile "$LOG" &
PID=$!

# Unity is a native Windows process: a terminal Ctrl+C never reaches it, so stop its whole tree here.
stop() {

    taskkill //F //T //PID "$(cat "/proc/$PID/winpid")" > /dev/null 2>&1 || true
    printf '\n%s stopped.\n' "$TASK"
    exit 130

}

trap stop INT TERM

START=$SECONDS

while kill -0 "$PID" 2> /dev/null; do

    printf '\r%s... %ds' "$TASK" $((SECONDS - START))
    sleep 1

done

STATUS=0
wait "$PID" || STATUS=$?
printf '\r%s... %ds\n' "$TASK" $((SECONDS - START))

if [[ "$TASK" == "Testing" && -f "$RESULTS" ]]; then

    grep -oE '<test-run [^>]*' "$RESULTS" | grep -oE '(passed|failed)="[0-9]+"' | tr '\n' ' '
    echo

fi

if (( STATUS != 0 )); then

    grep -E "error CS|Error|Aborting|another Unity instance|Failed" "$LOG" | sort -u | tail -20 || true
    echo "$TASK failed; full log in $LOG"

fi

exit "$STATUS"
