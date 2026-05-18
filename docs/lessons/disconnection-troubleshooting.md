# Disconnection Troubleshooting Guide

## Overview

This guide helps distinguish between expected and unexpected disconnection behavior in Zeus, covering both development and production scenarios.

## Expected Disconnections

### During Development (Vite Hot Reload)

**Symptom:** Frequent disconnects every time you save a file in `zeus-web/`

**Cause:** Vite's hot-reload mechanism restarts the frontend WebSocket connection while the backend stays running.

**Why this happens:**
1. You save a file in `zeus-web/src/`
2. Vite detects the change and rebuilds the frontend
3. The WebSocket connection drops (by design)
4. Your radio connection on the backend stays alive
5. WebSocket reconnects automatically within 1-8 seconds
6. Display resumes

**This is normal and expected.** The architecture decouples the radio connection (Protocol1Client in backend) from the display connection (WebSocket).

**Verification:**
- Check backend logs: No "RX: {N} consecutive socket timeouts" message
- WebSocket reconnects within seconds
- Display resumes showing live spectrum

### Network Configuration Changes

**Symptom:** Disconnect when switching WiFi networks or unplugging Ethernet

**Cause:** Your PC's network adapter changed, breaking the UDP connection to the radio.

**Resolution:** Click **Disconnect**, then **Discover** and reconnect. Zeus doesn't auto-reconnect after network changes.

## Unexpected Disconnections

### RX Timeout (Radio Gone)

**Symptom:** Backend logs show:
```
RX: 10 consecutive socket timeouts — radio gone
```

**Cause:** The radio stopped sending UDP packets for more than 1 second (10 × 100ms timeout).

**Possible reasons:**
1. **Radio power cycled** or firmware crashed
2. **Network packet loss** exceeding 1-second threshold (WiFi congestion, switch overload)
3. **System load** blocked RX thread for >1s (very rare on modern CPUs)

**Diagnosis:**
```bash
# Check if radio is still reachable
ping <radio-ip>

# If radio responds to ping but Zeus sees timeouts:
# - Likely UDP packet loss on your network
# - Try wired Ethernet instead of WiFi
# - Check for network congestion (other devices, streaming, etc.)
```

**Resolution:**
- Power cycle the radio if ping fails
- Switch to wired Ethernet if using WiFi
- Reduce network load (pause large downloads, disable other UDP-heavy apps)

### Backend Crash or Restart

**Symptom:**
- WebSocket shows "disconnected"
- Reconnection fails or times out
- Browser console shows WebSocket errors

**Cause:** The Zeus.Server backend process crashed or was stopped.

**Diagnosis:**
Check backend terminal for:
- Uncaught exceptions
- WDSP native errors (double-free, segfault)
- Out of memory errors

**Resolution:**
1. Restart `dotnet run --project OpenhpsdrZeus`
2. If it crashes immediately, check WDSP wisdom:
   - Did you wait for wisdom to complete on first run?
   - See [README.md](../../README.md) "First run — wait for WDSP wisdom"
3. Report the crash with full logs if reproducible

### State Desync Bug (Potential)

**Symptom:**
- UI shows "Connected" but no spectrum updates
- Backend RX thread exited but RadioService doesn't know
- No timeout message in logs

**Cause:** Known potential bug where RX loop exit doesn't propagate to RadioService.

**Diagnosis:**
```bash
# Check backend logs for:
grep "RX:" zeus-server.log
```

If you see "RX: N consecutive socket timeouts" but UI still shows "Connected", you've hit the state desync bug.

**Resolution:**
- Click **Disconnect** manually to force cleanup
- Reconnect
- Report this scenario (helps prioritize the fix)

## Troubleshooting Decision Tree

```
┌─────────────────────────────┐
│  Zeus disconnected          │
└──────────┬──────────────────┘
           │
           ├─ Development mode (npm run dev)?
           │   └─ YES → Expected Vite reload
           │       └─ Wait 1-8s, reconnects automatically
           │
           ├─ Backend logs show "RX timeout"?
           │   └─ YES → Radio or network problem
           │       ├─ Ping radio: works → Network loss
           │       └─ Ping radio: fails → Radio down
           │
           ├─ WebSocket keeps failing to reconnect?
           │   └─ YES → Backend crash
           │       └─ Check terminal for errors
           │
           └─ UI shows "Connected" but no display?
               └─ YES → Possible state desync bug
                   └─ Disconnect manually, report issue
```

## Development Best Practices

### Minimizing Vite Reload Disruption

**Option 1: Build once, run production build**
```bash
cd zeus-web && npm run build
dotnet run --project OpenhpsdrZeus
# Open http://localhost:6060 (no Vite dev server)
```
No hot-reload, but no WebSocket disconnects either.

**Option 2: Accept the disconnects**
Vite disconnects are <10 seconds and don't affect the radio. Just wait for reconnect.

### Continuous Operation Testing

To test long-running stability without Vite:
```bash
# Terminal 1: Build frontend once
cd zeus-web && npm run build

# Terminal 2: Run backend only
cd ..
dotnet run --project OpenhpsdrZeus

# Connect via http://localhost:6060
# No Vite = no dev disconnects
```

## Network Diagnostics

### Check UDP Packet Loss

```bash
# On a separate machine, send test UDP traffic
# Replace <zeus-ip> with your PC's IP running Zeus
nc -u <zeus-ip> 9999

# On Zeus PC, listen
nc -u -l 9999

# Send lines of text, verify they arrive
# Dropped lines = network loss
```

### WiFi vs Wired Ethernet

Protocol 1 runs over UDP at high packet rates (~1200 packets/sec at 192 kSps). WiFi introduces:
- Variable latency (10-100ms)
- Packet loss during contention
- Interference from other 2.4/5 GHz devices

**Recommendation:** Use wired Gigabit Ethernet for production operation.

## Log Analysis

### Healthy Connection Logs

```
info: Zeus.Protocol1.Protocol1Client[0]
      Protocol1 bound local=0.0.0.0:54321 remote=192.168.1.100:1024
```
No "RX timeout" messages = good.

### Unhealthy Connection Logs

```
warn: Zeus.Protocol1.Protocol1Client[0]
      RX: 10 consecutive socket timeouts — radio gone
```
Indicates radio stopped sending or network dropped packets.

### WebSocket Reconnection Logs

Frontend console (browser DevTools):
```
ws://localhost:6060/ws closed, reconnecting in 1000ms
ws://localhost:6060/ws closed, reconnecting in 2000ms
ws://localhost:6060/ws connected
```
Exponential backoff is working correctly.

## Reporting Issues

When reporting disconnection problems, include:

1. **Scenario:** Development (Vite) or production (built frontend)?
2. **Frequency:** How often? Every few minutes, or hours?
3. **Backend logs:** Copy the full terminal output, especially around disconnect time
4. **Browser console:** F12 → Console tab, copy any errors
5. **Network:** WiFi or Ethernet?
6. **Radio:** Hermes Lite 2 firmware version? (from discovery)

## Reference

- **RCA:** [2026-04-23-disconnection-investigation.md](../rca/2026-04-23-disconnection-investigation.md)
- **README:** [README.md](../../README.md) — First-run wisdom requirements
- **Dev conventions:** [dev-conventions.md](./dev-conventions.md) — Port allocations, dev setup
