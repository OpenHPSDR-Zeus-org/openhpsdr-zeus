#!/usr/bin/env bash
# run.sh — start the Zeus full stack (frontend build + Vite dev + .NET backend)
# for local development on macOS or Linux. Mirrors the /run Claude Code skill.
#
# Usage:
#   ./run.sh                 # Vite 5173, backend 6060
#   ./run.sh 10              # Vite 5183, backend 6070
#   ./run.sh 100             # Vite 5273, backend 6160
#   ./run.sh /desktop        # OpenhpsdrZeus --desktop (Photino) — no Vite, OS-assigned port
#
# Both servers run in the background. Logs and PIDs are written under .run/.
# To stop: kill $(cat .run/vite.pid) $(cat .run/backend.pid)
# In /desktop mode the Photino window owns the lifecycle — close it (or Ctrl-C)
# to shut Zeus down.

set -euo pipefail

# --- 1. OS detection -------------------------------------------------------
case "$(uname -s)" in
  Darwin) PLATFORM=macos ;;
  Linux)  PLATFORM=linux ;;
  *)      echo "run.sh: unsupported OS: $(uname -s) (need macOS or Linux)" >&2; exit 1 ;;
esac

# --- 2. Parse args (desktop flag + optional port offset) -------------------
DESKTOP=0
OFFSET=0
for arg in "$@"; do
  case "$arg" in
    --desktop|/desktop|-desktop) DESKTOP=1 ;;
    ''|*[!0-9]*) echo "run.sh: unknown argument '$arg' (expected /desktop or non-negative port offset)" >&2; exit 1 ;;
    *) OFFSET="$arg" ;;
  esac
done
FRONTEND_PORT=$((5173 + OFFSET))
BACKEND_PORT=$((6060 + OFFSET))

# --- 3. cd to repo root (the script's own directory) -----------------------
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

# --- 4. Kill existing listeners on the target ports ------------------------
kill_port() {
  local port="$1" pids
  if command -v lsof >/dev/null 2>&1; then
    pids="$(lsof -ti :"$port" 2>/dev/null || true)"
    if [ -n "$pids" ]; then
      # shellcheck disable=SC2086
      kill -9 $pids 2>/dev/null || true
    fi
  elif [ "$PLATFORM" = linux ] && command -v fuser >/dev/null 2>&1; then
    fuser -k "${port}/tcp" 2>/dev/null || true
  else
    echo "run.sh: need 'lsof' (or 'fuser' on Linux) to free port $port" >&2
    exit 1
  fi
}

if [ "$DESKTOP" -eq 0 ]; then
  echo "→ freeing ports $FRONTEND_PORT and $BACKEND_PORT"
  kill_port "$FRONTEND_PORT"
  kill_port "$BACKEND_PORT"
  sleep 1
fi

# --- 5. Build the frontend into wwwroot ------------------------------------
echo "→ building frontend (npm --prefix zeus-web run build)"
npm --prefix zeus-web run build

# --- 5b. Desktop mode: Photino owns the lifecycle, run in foreground -------
if [ "$DESKTOP" -eq 1 ]; then
  echo "→ starting OpenhpsdrZeus --desktop (Photino) — close the window or Ctrl-C to stop"
  exec dotnet run --project OpenhpsdrZeus -- --desktop
fi

# --- 6. Start the servers in the background --------------------------------
mkdir -p .run
VITE_LOG="$SCRIPT_DIR/.run/vite.log"
BACKEND_LOG="$SCRIPT_DIR/.run/backend.log"
: > "$VITE_LOG"
: > "$BACKEND_LOG"

echo "→ starting Vite dev on :$FRONTEND_PORT (proxy → :$BACKEND_PORT)"
BACKEND_PORT="$BACKEND_PORT" \
  nohup npm --prefix zeus-web run dev -- --port "$FRONTEND_PORT" --strictPort \
  >"$VITE_LOG" 2>&1 &
VITE_PID=$!
echo "$VITE_PID" > .run/vite.pid

echo "→ starting OpenhpsdrZeus on :$BACKEND_PORT"
ZEUS_PORT="$BACKEND_PORT" \
  nohup dotnet run --project OpenhpsdrZeus \
  >"$BACKEND_LOG" 2>&1 &
BACKEND_PID=$!
echo "$BACKEND_PID" > .run/backend.pid

disown "$VITE_PID"    2>/dev/null || true
disown "$BACKEND_PID" 2>/dev/null || true

# --- 7. Wait for both ports to come up -------------------------------------
wait_for_port() {
  local port="$1" name="$2" tries=0
  while [ $tries -lt 30 ]; do
    if lsof -iTCP:"$port" -sTCP:LISTEN -P >/dev/null 2>&1; then
      return 0
    fi
    sleep 1
    tries=$((tries + 1))
  done
  echo "run.sh: $name did not bind to :$port within 30s" >&2
  return 1
}

OK=1
wait_for_port "$FRONTEND_PORT" "Vite"        || OK=0
wait_for_port "$BACKEND_PORT"  "OpenhpsdrZeus" || OK=0

# --- 8. Report -------------------------------------------------------------
echo
if [ $OK -eq 1 ]; then
  cat <<EOF
Zeus is running ($PLATFORM):
  Vite dev:  http://localhost:$FRONTEND_PORT   (proxies /api,/ws → :$BACKEND_PORT)
  Backend:   http://localhost:$BACKEND_PORT
  wwwroot:   built from zeus-web

Logs:
  tail -f .run/vite.log
  tail -f .run/backend.log

PIDs:
  vite=$VITE_PID  backend=$BACKEND_PID

Stop:
  kill \$(cat .run/vite.pid) \$(cat .run/backend.pid)
EOF
else
  cat <<EOF >&2

Zeus failed to start. Inspect the logs:
  .run/vite.log
  .run/backend.log
EOF
  exit 1
fi
