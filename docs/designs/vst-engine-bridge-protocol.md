# Zeus ↔ VSTHost Engine Bridge Protocol

**Protocol version: 1**

The exact contract between the Zeus backend (.NET) and the headless
`KlayaR/VSTHost` engine (`--zeus-bridge`). Both repos implement this doc; keep
the two copies in sync. Companion: `docs/designs/vst-out-of-process-engine.md`.

Two independent planes:

- **Control plane** — newline-delimited JSON over the engine's stdin/stdout
  (existing `IPCBridge`). Not realtime.
- **Audio plane** — a shared-memory double-buffer + two named events. The only
  thing on the realtime path.

---

## 1. Process lifecycle

### 1.1 Launch

Zeus's `VstEngineProcess` launches the downloaded engine:

```
VSTHostEngine.exe --zeus-bridge \
    --shm   zeus-vst-<pid>-<nonce>   \   # base name for the SHM + events
    --frames 512                     \   # max frames per block (ring capacity)
    --rate   48000                   \   # sample rate (Hz)
    --channels 1                         # channel count (1 = mono mic/TX)
```

- Zeus **creates** the shared memory + both events *before* launch, then passes
  the base name. The engine **opens** them (never creates).
- `--frames` is the **maximum** block size; actual frames per block are carried
  per-block in the header (§3.2) and may be ≤ `--frames`.
- `stdin`/`stdout` are redirected to Zeus for the control plane. `stderr` is
  captured to a Zeus log.

### 1.2 Handshake

On startup the engine emits exactly one `ready` event (§2.3). Zeus MUST verify
`protocol == 1` and that `frames`/`rate`/`channels` echo the launch args. On
mismatch Zeus kills the engine, surfaces an error, and falls back to Native.
Until `ready` is seen, `engineReady = false` and the realtime path passes
through (§3.4).

### 1.3 Shutdown

- **Graceful:** Zeus closes the engine's stdin → engine's `IPCBridge` sees EOF
  → `JUCEApplication::quit()`. Zeus waits a bounded time, then kills if needed.
- **Zeus dies:** stdin closes from the OS → engine quits the same way. (No
  orphan engines.)

### 1.4 Crash recovery

- Zeus runs a **supervisor** (non-realtime thread) watching the child handle.
  On unexpected exit it sets `engineReady = false` *immediately* (realtime path
  passthroughs from the next block), then relaunches and replays `load_chain`.
- The engine's `PluginScanner` dead-man's-pedal blacklists any plugin that
  crashed a scan/load, so a crash-looping plugin is skipped on relaunch.
- Relaunch is rate-limited (e.g. exponential backoff, cap N attempts/minute);
  after the cap Zeus stays in passthrough and surfaces a `warning`.

---

## 2. Control plane (stdio newline-JSON)

One JSON object per line. Zeus→engine objects have a `"cmd"` key; engine→Zeus
objects have an `"event"` key. Commands are dispatched on the engine's message
thread (JUCE-safe). This is the existing VSTHost command set **minus all audio
device commands** (`get_devices`, `set_input/output_device`, `set_sample_rate`,
`set_buffer_size`, `set_backend`, `set_virtual_output`, `set_input/output_channel`,
`set_mute`, `set_monitor_muted`, mic/output gain, limiter) — the bridge owns
audio config via launch args, not commands.

### 2.1 Commands (Zeus → engine)

| `cmd` | fields | effect |
|---|---|---|
| `ping` | — | engine replies `ok {cmd:"ping"}` |
| `scan_plugins` | `paths:[string]` | async scan; emits `scan_progress`* then `plugins_scanned` |
| `clear_blacklist` | — | clears blacklist + dead-man's-pedal; `ok` |
| `add_plugin` | `file`, `uid`, `index?` | async-load + insert; emits `chain` |
| `remove_plugin` | `index` | closes its editor, removes; `chain` |
| `move_plugin` | `from`, `to` | reorder; `chain` |
| `set_plugin_enabled` | `index`, `value:bool` | `chain` |
| `set_plugin_bypassed` | `index`, `value:bool` | `chain` |
| `set_slot_gain` | `index`, `gainDb:number` | (no echo) |
| `set_plugin_state` | `index`, `state:base64` | restore; `chain` |
| `bypass_all` | `value:bool` | whole-chain bypass; `chain` |
| `set_param` | `slotIndex`, `paramIndex`, `value:0..1` | (no echo) |
| `load_chain` | `chain:[slot]` | clears + parallel-loads; emits `load_progress`*, `load_done`, `chain` |
| `get_chain` | — | `chain` |
| `open_editor` | `index` | opens native editor window; `editor_opened` |
| `close_editor` | `index` | closes editor window |

`slot` objects in `load_chain` match the `chain` event's plugin objects (§2.2):
`{file, identifier, enabled, bypassed, gainDb, state|parameters}`.

### 2.2 Events (engine → Zeus)

| `event` | key fields |
|---|---|
| `ready` | `protocol`, `name`, `version`, `frames`, `rate`, `channels`, `shm` |
| `chain` | `plugins:[…]`, `bypassAll` |
| `scan_progress` | `plugin`, `progress:0..1` |
| `plugins_scanned` | `plugins:[…]`, `blacklist:[file]` |
| `load_progress` | `index`, `total`, `name` |
| `load_done` | — |
| `editor_opened` / `editor_closed` | `uid` |
| `levels` | `slots:[float]` (per-slot), `in`, `out` |
| `modified` | — (a hosted param changed, incl. from an editor) |
| `ok` | `cmd` |
| `warning` / `error` | `message` |

Each `plugins[]` entry: `{uid, name, manufacturer, format, category, enabled,
bypassed, gainDb, latency, file, identifier, state:base64, parameters:[{index,
name, value, label, text}]}`.

### 2.3 `ready` example

```json
{"event":"ready","protocol":1,"name":"VSTHostEngine","version":"1.4.0",
 "frames":512,"rate":48000,"channels":1,"shm":"zeus-vst-12345-ab12"}
```

---

## 3. Audio plane (shared memory + events)

A **single-block double-buffer**, not a streaming ring: each block is a
synchronous request/response (Zeus writes input → engine processes → Zeus reads
output). Lowest latency, simplest correctness.

### 3.1 Named objects

- **Shared memory:** named `<shm>` (e.g. `Local\zeus-vst-12345-ab12` on Windows).
- **Events** (auto-reset, initially non-signaled):
  - `<shm>.in`  — signaled by **Zeus** when an input block is ready.
  - `<shm>.out` — signaled by **engine** when the output block is ready.

### 3.2 Layout

All little-endian. Header is 64 bytes; regions follow, 64-byte aligned.

```
offset  size                       field
0       u32   magic = 0x5A565342   'ZVSB'
4       u32   protocol = 1
8       u32   maxFrames            (= launch --frames; ring capacity)
12      u32   channels             (= launch --channels)
16      u32   rate                 (= launch --rate)
20      u32   framesThisBlock      written by Zeus each block, ≤ maxFrames
24      u64   inSeq                incremented by Zeus before signaling .in
32      u64   outSeq               set by engine = inSeq it just processed
40      u32   engineState          0=init 1=running 2=draining (engine-written)
44      u32   flags                bit0 = engine bypassed/empty chain
48      16                         reserved
64      F     input  region        float32 interleaved, maxFrames*channels
64+F    F     output region        float32 interleaved, maxFrames*channels
        where F = maxFrames * channels * 4, rounded up to 64
```

Sample format: **float32, interleaved** by frame
(`[f0c0,f0c1,…,f1c0,…]`). For mono (`channels=1`) it is a plain float array.
Each side converts to/from its internal layout (JUCE planar on the engine side).

### 3.3 Per-block protocol

**Zeus realtime thread** (`VstEngineClient.Process(input, output, ctx)`):

```
if (!engineReady)                         → input.CopyTo(output); return
if (ctx.Frames > maxFrames ||
    ctx.Channels != channels)             → input.CopyTo(output); return   // mismatch
write framesThisBlock = ctx.Frames
copy input → input region
inSeq++ ; write inSeq
Set(<shm>.in)
if (!Wait(<shm>.out, budgetMicros))       → input.CopyTo(output); degraded++; return
if (outSeq != inSeq)                       → input.CopyTo(output); return   // stale/late
copy output region → output
```

**Engine audio thread** (`ZeusAudioBridge`):

```
loop:
  Wait(<shm>.in)
  if (shutting down) break
  n = framesThisBlock
  de-interleave input region[0..n*channels] → JUCE AudioBuffer (channels × n)
  PluginChain::processBlock(buffer, midi)        // empty chain ⇒ passthrough copy
  interleave buffer → output region[0..n*channels]
  outSeq = inSeq
  Set(<shm>.out)
```

### 3.4 Failure & degradation semantics

- **Engine not ready / mode off:** Zeus never touches the SHM; pure passthrough.
- **Timeout (`budgetMicros`):** Zeus passes the *input* through unprocessed for
  that block and counts a `degraded` tick (surfaced for diagnostics). It does
  **not** read the output region (may be mid-write). The `outSeq != inSeq` guard
  makes a *late* writer harmless on the next block — Zeus only trusts output
  whose seq matches the input it just sent.
- **Engine crash:** supervisor flips `engineReady=false`; from the next block
  Zeus passthroughs. No realtime thread ever waits on a dead engine beyond one
  `budgetMicros`.
- **`budgetMicros`** is a fraction of the block period (512f @ 48 kHz ≈ 10.6 ms),
  e.g. **2–4 ms**, tunable. A wedged plugin costs ≤ one block of passthrough,
  never a dropped radio.

### 3.5 Realtime rules (Zeus side)

- SHM view + both event handles are acquired **once** at mode-enable, released on
  mode-disable — **never** per block.
- Per block: one memcpy in, one set, one bounded wait, one memcpy out. **No
  allocation, no managed lock, no GC-triggering call** on this path.
- Mode enable/disable and engine (re)launch happen on the **control thread**,
  between blocks; the realtime path only reads an `engineReady` flag.

---

## 4. Versioning

`protocol` is bumped on any breaking change to §2 fields or §3 layout. The
`ready` handshake (§1.2) rejects a mismatch. Additive control-plane fields
(new optional keys) do **not** bump `protocol`; layout or semantic changes do.
