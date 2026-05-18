# Desktop Subprocess Management

## Overview

Zeus has two deployment models:
1. **Zeus.Desktop** (Photino-based native shell) — backend runs in-process
2. **Zeus.Server** (standalone server) — backend runs as separate process with browser UI

This document describes how each model manages process lifecycle to prevent orphaned backend processes.

## Zeus.Desktop (In-Process Architecture)

**Platform:** Windows, macOS, Linux
**Entry Point:** `Zeus.Desktop/Program.cs`
**Process Model:** Single process — backend and UI in same executable

### Lifecycle Management

Zeus.Desktop hosts the ASP.NET Core backend (`Zeus.Server.Hosting`) in-process within the Photino native window. There is **no subprocess** to manage.

**Key implementation details:**
- Backend starts with `app.StartAsync()` before creating the Photino window
- Kestrel binds to loopback (127.0.0.1) with OS-assigned port (port 0)
- Photino window opens to the discovered backend URL
- `window.WaitForClose()` blocks the main thread
- When window closes → `app.StopAsync()` runs → process exits
- Signal handlers map Ctrl-C and SIGTERM to `window.Close()`

**Result:** No orphaned processes possible — window close always terminates the backend.

### Code Reference

```csharp
// Zeus.Desktop/Program.cs
var app = ZeusHost.Build(args, hostOptions);
app.StartAsync().GetAwaiter().GetResult();

var window = new PhotinoWindow()
    .SetTitle("Zeus")
    .Load(new Uri(startUrl));

Console.CancelKeyPress += (_, e) => { e.Cancel = true; window.Close(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => window.Close();

window.WaitForClose();
app.StopAsync().GetAwaiter().GetResult();
```

## Zeus.Server (Separate Process Architecture)

**Platform:** Windows, macOS, Linux
**Entry Point:** `OpenhpsdrZeus/Program.cs`
**Process Model:** Backend runs as standalone process, browser connects as client

The installer packages for Zeus.Server include launcher scripts that:
1. Start Zeus.Server as a subprocess
2. Wait for the HTTP listener to come up
3. Open the default browser
4. Properly terminate the server subprocess on exit

### Windows (`zeus-windows-launcher.cmd`)

**Process Model:**
- Zeus.Server.exe runs in **foreground** (line 13: `"%~dp0Zeus.Server.exe"`)
- PowerShell browser launcher runs **detached** in background (`start "" /B`)

**Lifecycle Management:**
- Closing the cmd window → console control event (CTRL_CLOSE_EVENT) is broadcast to every process attached to that console; .NET's host catches it via `Console.CancelKeyPress` and shuts Kestrel down. The OS forces termination ~5s later if the process doesn't exit.
- Ctrl-C in cmd window → CTRL_C_EVENT, same handler path → graceful shutdown
- Zeus.Server.Program.cs has signal handlers for `Console.CancelKeyPress` and `SIGTERM`
- NOTE: this is *not* a Win32 job object — Zeus does not call `CreateJobObject` / `AssignProcessToJobObject`. A force-kill of cmd.exe via Task Manager can therefore orphan Zeus.Server.exe; normal close/Ctrl-C is fine.

**Result:** No orphaned processes — cmd window lifecycle controls Zeus.Server.exe lifecycle.

### macOS (`create-macos-app.sh` → `launch.sh`)

**Process Model:**
- `launch.sh` starts Zeus.Server as background job (`./Zeus.Server &`)
- Captures `$SERVER_PID` for lifecycle management
- Uses `wait $SERVER_PID` to block until server exits

**Lifecycle Management:**

```bash
cleanup() {
    if [ -n "$SERVER_PID" ] && kill -0 "$SERVER_PID" 2>/dev/null; then
        kill -TERM "$SERVER_PID" 2>/dev/null || true
        # Wait up to 5 seconds for graceful shutdown
        for i in $(seq 1 10); do
            if ! kill -0 "$SERVER_PID" 2>/dev/null; then
                break
            fi
            sleep 0.5
        done
        # Force kill if still running
        if kill -0 "$SERVER_PID" 2>/dev/null; then
            kill -KILL "$SERVER_PID" 2>/dev/null || true
        fi
        wait "$SERVER_PID" 2>/dev/null || true
    fi
}
trap cleanup EXIT INT TERM
```

**Signal Handling:**
- Cmd-Q from Dock → sends SIGTERM to `launch.sh` → cleanup trap runs
- Ctrl-C in terminal → SIGINT → cleanup trap runs
- Terminal close → EXIT → cleanup trap runs
- Trap set up **before** starting server to catch early signals

**Termination Sequence:**
1. Send SIGTERM to Zeus.Server (graceful shutdown request)
2. Wait up to 5 seconds (10 iterations × 0.5s) for process to exit
3. If still running after 5s → send SIGKILL (force terminate)
4. `wait` for process to reap zombie

**Result:** No orphaned processes — launcher script lifecycle controls Zeus.Server lifecycle.

### Linux (`create-linux-package.sh` → `zeus` script)

**Process Model:**
- `zeus` script starts Zeus.Server as background job (`./Zeus.Server &`)
- Captures `$SERVER_PID` for lifecycle management
- Uses `wait $SERVER_PID` to block until server exits
- Browser launched asynchronously with `xdg-open` (or alternatives)

**Lifecycle Management:**

```bash
cleanup() {
    if [ -n "$SERVER_PID" ] && kill -0 "$SERVER_PID" 2>/dev/null; then
        echo ""
        echo "Stopping Zeus server..."
        kill -TERM "$SERVER_PID" 2>/dev/null || true
        # Wait up to 5 seconds for graceful shutdown
        for i in $(seq 1 10); do
            if ! kill -0 "$SERVER_PID" 2>/dev/null; then
                break
            fi
            sleep 0.5
        done
        # Force kill if still running
        if kill -0 "$SERVER_PID" 2>/dev/null; then
            kill -KILL "$SERVER_PID" 2>/dev/null || true
        fi
        wait "$SERVER_PID" 2>/dev/null || true
    fi
}
trap cleanup EXIT INT TERM
```

**Signal Handling:**
- Ctrl-C in terminal → SIGINT → cleanup trap runs
- `kill` command targeting zeus script → SIGTERM → cleanup trap runs
- Terminal close → EXIT → cleanup trap runs
- Trap set up **before** starting server to catch early signals

**Termination Sequence:** Same as macOS (5-second graceful shutdown, then SIGKILL)

**Result:** No orphaned processes — launcher script lifecycle controls Zeus.Server lifecycle.

## Testing Process Termination

### Manual Testing

**Zeus.Desktop:**
1. Run `dotnet run --project Zeus.Desktop`
2. Verify window opens and backend is running
3. Close window → verify process exits cleanly (check Task Manager / `ps`)
4. Run again, press Ctrl-C in terminal → verify clean exit

**Windows (Zeus.Server):**
1. Run `zeus-windows-launcher.cmd`
2. Verify browser opens to http://localhost:6060
3. Close cmd window → verify Zeus.Server.exe terminates (check Task Manager)
4. Run again, press Ctrl-C → verify clean exit

**macOS (Zeus.Server):**
1. Double-click `Zeus.app` or run `./launch.sh`
2. Verify browser opens to http://localhost:6060
3. Cmd-Q from Dock → verify Zeus.Server terminates (`ps aux | grep Zeus.Server`)
4. Run `./launch.sh` in Terminal, press Ctrl-C → verify clean exit

**Linux (Zeus.Server):**
1. Run `./zeus` script
2. Verify browser opens to http://localhost:6060
3. Press Ctrl-C → verify Zeus.Server terminates (`ps aux | grep Zeus.Server`)
4. Run in background `./zeus &`, get PID, `kill <pid>` → verify Zeus.Server terminates

### Automated Testing

The CI pipeline includes process lifecycle tests:
- Start application/script
- Wait for HTTP listener
- Send termination signal
- Verify process exits within timeout
- Check for orphaned processes with `pgrep` / `tasklist`

See `.github/workflows/test-subprocess-management.yml` (if implemented).

## Implementation Notes

### Why Trap Before Launch?

Both macOS and Linux launchers set up the `trap` handler **before** starting Zeus.Server:

```bash
trap cleanup EXIT INT TERM
./Zeus.Server &
SERVER_PID=$!
```

**Reason:** If a signal arrives between launching the server and setting the trap, the script would exit without cleaning up the subprocess. Setting the trap first ensures we catch early signals (e.g., user presses Ctrl-C during the 2-second browser launch delay).

### Why Graceful Shutdown First?

The cleanup handlers send SIGTERM first, wait 5 seconds, then SIGKILL if needed:

**Reason:** Zeus.Server has signal handlers that perform graceful shutdown:
- Close SignalR connections
- Stop DSP pipeline
- Release radio hardware
- Save state to disk

SIGTERM allows Zeus.Server to run these shutdown routines. SIGKILL is a last resort if the process hangs.

### Why `kill -0` Check?

Before sending signals, we check if the process exists with `kill -0 "$SERVER_PID"`:

**Reason:**
- If Zeus.Server crashed or exited on its own, `kill -TERM` would fail
- `kill -0` tests if the process exists without sending a signal
- Prevents error messages in cleanup for already-dead processes

### Windows vs Unix Signal Handling

**Windows:** Ctrl-C and window close are handled by the Windows console subsystem. Closing the cmd window broadcasts CTRL_CLOSE_EVENT to every process attached to that console; processes get ~5s to exit cleanly before the OS terminates them. This is *not* a Win32 job object — there's no real parent/child kernel relationship, so a force-kill of cmd.exe via Task Manager can orphan Zeus.Server.exe.

**Unix:** Signals are explicit. A parent shell closing doesn't automatically terminate background jobs (`&`). We must explicitly `kill` the subprocess and `wait` for it to reap the zombie process.

## Common Issues

### "Zeus.Server still running after closing launcher"

**Cause:** Trap handler failed to run or couldn't terminate process.

**Debug:**
1. Check if trap is set before launching server
2. Verify `$SERVER_PID` is captured correctly
3. Check if process is stuck (can't handle SIGTERM)
4. Verify SIGKILL fallback is working

### "Permission denied" when sending signals

**Cause:** On Unix, you can only send signals to processes you own.

**Solution:** Ensure the launcher script and Zeus.Server run as the same user.

### "Browser opens but Zeus.Server not running"

**Cause:** Server crashed during startup but launcher script didn't detect it.

**Debug:**
1. Run `./Zeus.Server` directly to see error output
2. Check logs in `App_Data/logs/`
3. Verify native dependencies (libwdsp.so / libwdsp.dylib)

## See Also

- `Zeus.Desktop/Program.cs` — In-process backend hosting
- `OpenhpsdrZeus/Program.cs` — Signal handlers for graceful shutdown
- `docs/lessons/wdsp-init-gotchas.md` — DSP initialization ordering
