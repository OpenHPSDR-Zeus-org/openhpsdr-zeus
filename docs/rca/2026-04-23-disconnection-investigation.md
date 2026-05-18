# Disconnection Investigation — 2026-04-23

## Issue Summary

User reports disconnections after some periods of use. Suspected causes:
1. Development environment (Vite reloads)
2. Underlying protocol issues
3. Hermes Lite 2 / Protocol1 integration reliability

## Investigation Findings

### Protocol1Client Timeout Behavior

**Location:** `Zeus.Protocol1/Protocol1Client.cs:261-295`

The RX loop has a timeout mechanism:
```csharp
private const int RxSocketTimeoutMs = 100;
private const int ConsecutiveTimeoutsBeforeGiveUp = 10;
```

**Analysis:**
- Socket timeout is set to 100ms on receive operations
- After **10 consecutive timeouts** (~1 second of no packets), the RX thread exits with:
  ```
  "RX: {N} consecutive socket timeouts — radio gone"
  ```
- At 192 kSps, packets arrive at ~1.2 kHz, so missing 10×100ms windows indicates genuine radio loss
- **However**, during system load spikes or Vite reloads, this could trigger false positives

**Evidence this is load-bearing:**
- The consecutive counter resets on *any* packet (line 295)
- This prevents transient single-packet drops from killing the connection
- The 1-second total timeout is reasonable for hardware failure detection

**Comparison with Thetis:**
- Thetis uses similar socket timeout logic but values need verification from source
- Zeus follows the same "consecutive timeouts" pattern rather than absolute time

### WebSocket Layer Disconnection Handling

**Location:** `zeus-web/src/realtime/ws-client.ts:133-304`

**Robust reconnection logic:**
```typescript
const INITIAL_BACKOFF_MS = 1000;
const MAX_BACKOFF_MS = 8000;
```

**Behavior on disconnect:**
1. Exponential backoff reconnection (1s → 2s → 4s → 8s max)
2. **Safety feature:** MOX is forced off on WS close (line 163):
   ```typescript
   if (useTxStore.getState().moxOn) useTxStore.getState().setMoxOn(false);
   ```
3. Display marked disconnected
4. Auto-reconnects when backend comes back

**Development vs Production:**

| Scenario | WebSocket | Protocol1 Connection | Expected? |
|----------|-----------|---------------------|-----------|
| Vite reload | Drops | Stays alive | Yes - normal dev |
| Backend restart | Drops | Destroyed | Yes - expected |
| Network hiccup | May drop | RX timeout after 1s | Depends on duration |
| Radio power-off | Stays up | RX timeout after 1s | Yes - correct |

### RadioService Connection Lifecycle

**Location:** `Zeus.Server.Hosting/RadioService.cs:156-240`

**Connection flow:**
1. `ConnectAsync`: Creates Protocol1Client, sets state to Connecting
2. Calls `client.ConnectAsync` → binds UDP socket
3. Calls `client.StartAsync` → sends Metis start, spawns RX/TX threads
4. Sets state to Connected, fires `Connected` event
5. `DisconnectAsync`: Tears down client gracefully

**Potential issue identified:**
- If the RX loop exits due to timeout (line 282 in Protocol1Client), the `RadioService` is **not notified**
- The service still thinks it's connected until explicit `DisconnectAsync` is called
- UI may show "connected" while the backend RX loop has died

**Recommendation:** Add a `Disconnected` event to Protocol1Client that fires when RX loop exits, and have RadioService subscribe to it.

### StreamingHub Session Management

**Location:** `Zeus.Server.Hosting/StreamingHub.cs:86-107`

**Per-client lifecycle:**
- Each WebSocket client gets a unique session
- Sessions are removed from `_clients` dictionary when RunAsync completes
- If a client's WS drops, that specific session ends but doesn't affect other clients or the radio connection

**This is correct:** The radio connection (Protocol1) and WebSocket sessions (display streaming) are properly decoupled.

### Hermes Lite 2 Specific Considerations

**Start/Stop sequence:**
- Zeus sends **3× start frames** on macOS (line 451-457) to work around first-UDP-drop
- This is good and matches community best practices
- On disconnect, sends stop frame and drains socket for 100ms (doc 02 §3)

**HL2 Dither:**
- Protocol1Client has `EnableHl2Dither` property (line 111-115)
- This is HL2-specific and shows awareness of HL2 quirks

**No HL2-specific timeout issues found** in the current implementation.

## Root Cause Analysis

### Development Disconnects (High Confidence)

**Cause:** Vite hot-reload kills the frontend WebSocket, but backend Protocol1 connection stays alive.

**Why it feels like a disconnect:**
- UI shows "disconnected" (correct - WS is down)
- Backend is still receiving from radio
- On Vite finish, WS reconnects and display resumes

**This is expected behavior** - not a bug. The decoupled architecture is working as designed.

### Production Disconnects (Needs More Data)

**Possible causes:**

1. **Network packet loss exceeds 1-second threshold**
   - If WiFi/Ethernet drops packets for >1s, RX loop exits
   - Radio continues transmitting but Zeus thinks it's gone
   - Mitigation: Increase `ConsecutiveTimeoutsBeforeGiveUp` to 20 (2 seconds)

2. **System load spikes during WDSP operations**
   - If WDSP blocks the RX thread for >1s, timeouts accumulate
   - Less likely because RX loop is dedicated thread
   - Would show up in logs as timeout warnings

3. **Radio firmware bugs or hardware resets**
   - HL2 could reset its network stack
   - Would manifest as genuine packet loss
   - Not a Zeus bug - need radio-side diagnostics

4. **Silent RadioService state desync**
   - RX loop exits, but RadioService doesn't know
   - UI shows "connected" when backend is dead
   - **This is a real bug** - see recommendation below

## Comparison with Thetis

### Thetis Architecture (TAPR/OpenHPSDR-Thetis)

Reviewed `Project Files/Source/Console/HPSDR/NetworkIO.cs` (SHA: c16f8b21):

**Discovery timeout:** Thetis uses `socket.Poll(100000, SelectMode.SelectRead)` in discovery loop, which is 100ms (100,000 microseconds). This matches Zeus's RX socket timeout value.

**Key differences:**
1. **Thetis uses native C code for RX loop** — NetworkIO.cs only handles discovery. The actual packet reception is in `nativeInitMetis()` and managed by native code, making direct comparison difficult.
2. **Discovery-only timeout visible** — NetworkIO.cs shows 100ms polling in discovery with 5 retry limit before timeout (`time_out > 5`), giving ~500ms total discovery timeout.
3. **Zeus's managed RX loop** — Zeus uses fully managed C# code with 100ms socket timeout and 10 consecutive failures (1s total), which is MORE tolerant than Thetis discovery (2× the retry count).

**Conclusion:** Zeus's 1-second RX timeout is reasonable and actually more permissive than Thetis's observable discovery behavior. The native Thetis RX loop timeout values would require inspecting `Project Files/Source/ChannelMaster/networkproto1.c`, which wasn't directly reviewable via search.

**No protocol implementation issues found** when comparing with Thetis patterns.

## Recommendations

### 1. Add Protocol1Client Disconnected Event (Medium Priority)

**Problem:** When RX loop exits due to timeout, RadioService doesn't know.

**Solution:**
```csharp
// In IProtocol1Client.cs
public event Action? Disconnected;

// In Protocol1Client.cs RxLoop, line 283:
_log.LogWarning("RX: {N} consecutive socket timeouts — radio gone", consecutiveTimeouts);
try { Disconnected?.Invoke(); } catch { }
return;

// In RadioService.cs ConnectAsync:
client.Disconnected += OnRadioDisconnected;

// New method in RadioService:
private void OnRadioDisconnected()
{
    _log.LogWarning("Protocol1 RX loop exited - radio connection lost");
    _ = DisconnectAsync(CancellationToken.None); // Async fire-and-forget cleanup
}
```

### 2. Make RX Timeout Configurable (Low Priority)

**Current:** Hardcoded 10×100ms = 1s
**Proposed:** Config option to increase to 20×100ms = 2s for flaky networks

**Implementation:**
```csharp
public sealed class Protocol1Client : IProtocol1Client
{
    // Allow construction-time override for tests/tolerance tuning
    private readonly int _timeoutThreshold;

    public Protocol1Client(..., int consecutiveTimeoutThreshold = 10)
    {
        _timeoutThreshold = consecutiveTimeoutThreshold;
    }
}
```

### 3. Add Telemetry for Timeout Events (Low Priority)

**Track:**
- Total number of timeouts observed (not just consecutive)
- Longest consecutive timeout streak that recovered
- Average packet receive rate

**Why:** Helps distinguish "flaky network" from "radio gone" scenarios in user reports.

### 4. Document Development Disconnect Behavior (High Priority)

**Location:** `docs/lessons/dev-conventions.md` or new `docs/lessons/disconnection-troubleshooting.md`

**Content:**
- Explain that Vite reloads kill WS but not radio connection
- Clarify expected vs unexpected disconnects
- Add troubleshooting steps for operators

### 5. Verify Thetis Timeout Values (Medium Priority)

**Action:**
- Clone https://github.com/ramdor/Thetis
- Search for Protocol1 socket timeout configuration
- Compare with Zeus implementation
- Document any differences

## Development vs Production Distinction

| Symptom | Development | Production |
|---------|-------------|------------|
| Disconnects every few minutes | Likely Vite reloads | Investigate network |
| Disconnect on heavy DSP load | Less likely | Check system resources |
| Disconnect then reconnect in 1-8s | Yes - WS backoff | Check WS logs |
| Disconnect stays stuck | No (WS retries) | Radio or network down |

## Conclusion

**Development disconnects:** Expected behavior due to Vite reloads. Not a bug.

**Production disconnects:**
- Likely network packet loss exceeding 1-second RX timeout threshold
- Possible state desync bug where RadioService doesn't detect RX loop exit (fixable)
- No evidence of Protocol1 or HL2-specific implementation bugs

**Priority fixes:**
1. Add `Disconnected` event to Protocol1Client (prevents state desync)
2. Document expected disconnect behavior for developers
3. Make timeout threshold configurable for operators with flaky networks

**Further investigation needed:**
- Verify Thetis timeout values for comparison
- Collect real-world disconnect logs from production users
- Monitor timeout statistics to distinguish patterns
