---
name: run
description: Build the Zeus frontend into wwwroot and launch the OpenhpsdrZeus backend. Defaults to desktop mode (Photino webview, no Vite). Pass `--mode=web` (or `-m web`) to run the web-dev stack (Vite dev server + backend on :6060). Optional portOffset (web mode only, e.g. `/run --mode=web 10`) shifts both ports. Optional `fresh` flag points the backend at a throw-away prefs DB.
---

# /run — start Zeus

Bring up Zeus for local development. Two modes:

- **desktop** (default) — builds `wwwroot`, launches `OpenhpsdrZeus --desktop`. The Photino webview loads from an OS-assigned loopback port; LAN HTTPS lives on `:6443`. No Vite dev server (no HMR).
- **web** — builds `wwwroot`, starts Vite dev server on `:5173` (with HMR), starts backend on `:6060`. This is the original `/run` behaviour.

## Project layout (don't get this wrong)

The host executable and its support library have similar names — read this before editing the skill or running `dotnet run`:

- **`OpenhpsdrZeus/`** — the only executable project (`OutputType=Exe`, `net10.0`). `OpenhpsdrZeus/Program.cs` reads `ZEUS_PORT` and routes on `--desktop` / `--server` flags. This is what `dotnet run --project ...` targets.
- **`Zeus.Server.Hosting/`** — a class library referenced by the host. Owns `PrefsDbPath.cs` (reads `ZEUS_PREFS_PATH`) and the `wwwroot/` directory that Vite builds into. Trying to `dotnet run` this fails with "OutputType is Library".
- Solution file is `Zeus.slnx` (no `.sln`).

## Arguments

Args can appear in any order. The skill scans each one and routes by type:

- **`--mode=desktop` / `--mode=web`** (also accepted: `-m=desktop`, `-m desktop`, `-m=web`, `-m web`)
  - Default: **`desktop`**.
  - `desktop`: launches `OpenhpsdrZeus --desktop` (Photino webview). Vite is not started.
  - `web`: starts Vite dev server on `:5173` + backend on `:6060`.
- **portOffset** (optional, non-negative integer, **web mode only**): shifts both ports.
  - `/run --mode=web` → Vite **5173**, backend **6060**
  - `/run --mode=web 10` → Vite **5183**, backend **6070**
  - `/run --mode=web 100` → Vite **5273**, backend **6160**
  - Reject negative values — tell the user, don't proceed.
  - Ignored (with a warning) in desktop mode: the desktop backend picks an OS-assigned loopback port and binds LAN HTTPS on the fixed `:6443`.
- **`fresh`** (optional literal flag, both modes): runs the backend against a unique-per-launch throw-away `zeus-prefs.db` at `/tmp/zeus-fresh-$$.db` (`$$` = shell PID). Persisted state from a prior session does not leak into the dev run; the file is recreated empty on each `/run fresh`.
  - `/run fresh` → desktop mode + throw-away DB
  - `/run --mode=web fresh 10` and `/run 10 fresh --mode=web` are equivalent (web mode, offset 10, throw-away DB).
  - Without this flag, the backend uses the platform default DB path (production prefs).

## Port + DB configuration (how the env vars / flags work)

No source patching needed — everything is wired through env vars and CLI flags already in place:

- Backend service mode (`OpenhpsdrZeus/Program.cs` → `RunService`): reads `ZEUS_PORT`, defaults to 6060, uses `ListenAnyIP` so LAN access is preserved. This is the path used in **web** mode.
- Backend desktop mode (`OpenhpsdrZeus/Program.cs` → `RunDesktop`, triggered by the `--desktop` arg): hardcodes loopback HTTP to port 0 (OS-assigned) and LAN HTTPS to `:6443`. **`ZEUS_PORT` is not consulted here** — portOffset has no effect on desktop mode.
- Frontend (`zeus-web/vite.config.ts`): `/api` and `/ws` proxy target reads `BACKEND_PORT` env var, defaults to 6060. Vite's own listen port is set via `--port` on the CLI. Only relevant in **web** mode.
- Backend prefs DB (`Zeus.Server.Hosting/PrefsDbPath.cs`): reads `ZEUS_PREFS_PATH` env var. When set, every store (PaSettings, DspSettings, RadioState, Display, …) writes to that single file instead of the platform default. The `fresh` flag wires this to a `/tmp` path. Honoured by both modes.

## Steps

### 1. Parse args (mode + portOffset + fresh)

Args can appear in any order. Scan all of them. `-m` / `--mode` may be `--mode=X`, `-m=X`, `--mode X`, or `-m X`. The literal `fresh` enables the throw-away DB. The first non-negative integer is the portOffset.

```bash
MODE=desktop
OFFSET=0
FRESH=0

# pre-process to merge "--mode X" / "-m X" into "--mode=X" so the case below is simple
ARGS=()
while [ $# -gt 0 ]; do
  case "$1" in
    --mode|-m)
      [ $# -ge 2 ] || { echo "missing value for $1 (expected desktop|web)"; exit 1; }
      ARGS+=("--mode=$2"); shift 2 ;;
    *) ARGS+=("$1"); shift ;;
  esac
done
set -- "${ARGS[@]}"

for arg in "$@"; do
  case "$arg" in
    --mode=desktop|-m=desktop) MODE=desktop ;;
    --mode=web|-m=web)         MODE=web ;;
    --mode=*|-m=*)             echo "invalid mode '$arg' (expected desktop|web)"; exit 1 ;;
    fresh)                     FRESH=1 ;;
    ''|*[!0-9]*)               echo "unrecognized arg '$arg' (expected non-negative integer, 'fresh', or --mode=desktop|web)"; exit 1 ;;
    *)                         OFFSET="$arg" ;;
  esac
done

FRONTEND_PORT=$((5173 + OFFSET))
BACKEND_PORT=$((6060 + OFFSET))

if [ "$FRESH" = "1" ]; then
  FRESH_DB="/tmp/zeus-fresh-$$.db"
  rm -f "$FRESH_DB"   # make sure it really starts empty
fi

if [ "$MODE" = "desktop" ] && [ "$OFFSET" != "0" ]; then
  echo "note: portOffset is ignored in desktop mode (backend picks an OS-assigned loopback port; LAN HTTPS is fixed at :6443)"
fi
```

### 2. Kill existing listeners

```bash
if [ "$MODE" = "web" ]; then
  lsof -ti :"$FRONTEND_PORT" | xargs kill -9 2>/dev/null; \
  lsof -ti :"$BACKEND_PORT"  | xargs kill -9 2>/dev/null; \
  sleep 1
else
  # desktop mode: free :6443 (LAN HTTPS) in case a previous desktop session is still bound.
  # The loopback port is OS-assigned so there's nothing fixed to free for it.
  lsof -ti :6443 | xargs kill -9 2>/dev/null; \
  sleep 1
fi
```

Do NOT use `fuser` (not default on macOS).

### 3. Build the frontend into wwwroot (both modes)

```bash
npm --prefix zeus-web run build
```

This runs `tsc -b && vite build` and writes to `Zeus.Server.Hosting/wwwroot/` (`emptyOutDir: true`, configured in `zeus-web/vite.config.ts`). Must complete before the backend starts so served assets aren't stale. If this fails, stop — do not start the servers.

### 4. (web mode only) Start the Vite dev server (background)

Skip this step entirely in desktop mode.

```bash
BACKEND_PORT=$BACKEND_PORT npm --prefix zeus-web run dev -- --port $FRONTEND_PORT --strictPort
```

- Run with `run_in_background: true` on the Bash tool.
- `BACKEND_PORT` tells the Vite proxy where to forward `/api` and `/ws`.
- `--strictPort` makes Vite fail loudly rather than silently picking another port.

### 4b. (desktop mode only) Clear the stale webview bundle cache

Skip this step entirely in web mode (Vite serves fresh assets with HMR).

**Why:** the desktop webview (WebKit on macOS/Linux, WebView2 on Windows) registers a
service worker that **precaches the built bundle and serves it stale across restarts**
— so a freshly rebuilt `wwwroot` does NOT show up until that precache is cleared. This
is the #1 "my frontend change isn't showing" trap in desktop mode. Clear it on every
desktop `/run` so the webview always loads the bundle just built in step 3.

The app must be stopped first (step 2 already freed `:6443`; this also `pkill`s any
straggler so the webview isn't holding the cache files). This is a **surgical** clear:
remove only the bundle caches (the service-worker precache + HTTP cache, where the
stale bytes live). **Do NOT** touch `LocalStorage` (UI prefs) or `IndexedDB` — and the
layout/PS/calibration state is server-side (LiteDB) regardless, so nothing is lost; the
only cost is the webview re-fetching the bundle from the local backend on next launch.

**macOS:**

```bash
pkill -f 'OpenhpsdrZeus' 2>/dev/null; sleep 1
CA="$HOME/Library/Caches/OpenhpsdrZeus/WebKit"
# The stale bundle lives in the HTTP cache (NetworkCache, ~tens of MB) + the
# service-worker Cache-Storage API. Clearing just these forces a fresh bundle;
# the origin store (IndexedDB, SW registration) and LocalStorage are left intact.
rm -rf "$CA/NetworkCache" "$CA/CacheStorage" 2>/dev/null
echo "cleared desktop webview bundle cache (LocalStorage + IndexedDB preserved)"
```

**Windows / Linux** (adapt — same intent, different cache home; keep `Local Storage`
and `IndexedDB`):

- **Windows (WebView2):** find the `EBWebView` user-data folder for `OpenhpsdrZeus`
  (next to the executable as `OpenhpsdrZeus.exe.WebView2\EBWebView`; for `dotnet run`
  that's under `OpenhpsdrZeus\bin\Debug\net10.0\`, for an installed build next to the
  installed exe — otherwise search `%LOCALAPPDATA%` for `EBWebView`). Inside
  `EBWebView\Default\`, delete only `Service Worker`, `Cache`, and `Code Cache`. **Keep
  `Local Storage` and `IndexedDB`.**
- **Linux (WebKitGTK):** clear only the app's WebKit HTTP/Cache-Storage cache under
  `~/.cache/OpenhpsdrZeus` (leave `~/.local/share/OpenhpsdrZeus`, which holds
  LocalStorage / IndexedDB).

If a stale bundle ever *survives* this surgical clear, the heavier fallback is to also
remove the origin store (`~/Library/WebKit/OpenhpsdrZeus/WebsiteData/Default` on macOS)
— that drops IndexedDB too, so only reach for it if needed.

After clearing, the next launch re-fetches the freshly built bundle from the backend.

### 5. Start the .NET backend (background)

**Desktop mode:**

```bash
if [ "$FRESH" = "1" ]; then
  ZEUS_PREFS_PATH="$FRESH_DB" dotnet run --project OpenhpsdrZeus -- --desktop
else
  dotnet run --project OpenhpsdrZeus -- --desktop
fi
```

**Web mode:**

```bash
if [ "$FRESH" = "1" ]; then
  ZEUS_PORT=$BACKEND_PORT ZEUS_PREFS_PATH="$FRESH_DB" dotnet run --project OpenhpsdrZeus
else
  ZEUS_PORT=$BACKEND_PORT dotnet run --project OpenhpsdrZeus
fi
```

- Run with `run_in_background: true`.
- The host project is **`OpenhpsdrZeus`** (the only `OutputType=Exe` in the solution). `Zeus.Server.Hosting` is a class library — do not pass it to `dotnet run`.
- Note the `--` separator before `--desktop`: it tells `dotnet run` to forward the flag to the app rather than parsing it itself.
- `ZEUS_PORT` is read in `OpenhpsdrZeus/Program.cs` `RunService` (web mode). In desktop mode the backend uses port 0 / `:6443` — `ZEUS_PORT` is intentionally not passed.
- `ZEUS_PREFS_PATH` (set only when `fresh` was passed) is read in `Zeus.Server.Hosting/PrefsDbPath.cs` and routes every LiteDB-backed store to the throw-away file. Honoured by both modes.

### 6. Verify, then report

**Web mode** — probe both ports after a short wait:

```bash
lsof -iTCP:"$FRONTEND_PORT" -sTCP:LISTEN -P | tail -n +2
lsof -iTCP:"$BACKEND_PORT"  -sTCP:LISTEN -P | tail -n +2
```

**Desktop mode** — probe LAN HTTPS and confirm the host process is alive. The loopback port is OS-assigned and printed by the backend (look for `OpenhpsdrZeus (desktop) hosting backend at …` in the background task output):

```bash
lsof -iTCP:6443 -sTCP:LISTEN -P | tail -n +2
pgrep -f 'OpenhpsdrZeus' | head -1
```

If the expected listeners aren't up after ~10 seconds, read the background task output and report the failure honestly — don't claim success.

Final message must name the mode and the relevant URLs. When `fresh` was passed, surface the throw-away DB path so the user knows their production prefs aren't being touched.

**Desktop mode template:**

```
Zeus is running (desktop):
  Photino:   loopback HTTP on OS-assigned port (see backend output for exact URL)
  LAN HTTPS: https://<host>:6443
  wwwroot:   built from zeus-web into Zeus.Server.Hosting/wwwroot
  prefs DB:  <FRESH_DB>   (throw-away — clean slate for this run)   # only when fresh
```

**Web mode template:**

```
Zeus is running (web):
  Vite dev:  http://localhost:<FRONTEND_PORT>   (proxies /api,/ws → :<BACKEND_PORT>)
  Backend:   http://localhost:<BACKEND_PORT>    (OpenhpsdrZeus)
  wwwroot:   built from zeus-web into Zeus.Server.Hosting/wwwroot
  prefs DB:  <FRESH_DB>   (throw-away — clean slate for this run)   # only when fresh
```

## Do NOT

- Do **not** edit `Program.cs`, `vite.config.ts`, or any other source file — the env-var / flag plumbing is already in place.
- Do **not** run tests or a separate `dotnet build` — `dotnet run` compiles and step 3 already builds the frontend.
- Do **not** foreground either server — both must be backgrounded so control returns to the user.
- Do **not** start the backend before the frontend build completes, or `wwwroot` may be stale/empty.
- Do **not** start Vite in desktop mode — the Photino webview loads from the embedded backend, not from `:5173`. Running both wastes resources and confuses the operator about which UI they're testing.
