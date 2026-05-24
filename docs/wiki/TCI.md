# TCI (Transceiver Control Interface)

Zeus exposes an **ExpertSDR3-compatible TCI server** so amateur radio loggers, digital-mode apps, CW skimmers, and SDR display tools can drive Zeus and receive RX/IQ streams over a WebSocket. This page tracks **what is implemented, what is partial, and what is missing** against the official Expert Electronics TCI 2.0 specification (12 January 2024).

---

## Quick start

TCI is **disabled by default** for security — there is no authentication, so the bind address is the security boundary. Enable it in `appsettings.json` (or via the `/api/tci/config` REST endpoint):

```json
{
  "Tci": {
    "Enabled": true,
    "BindAddress": "127.0.0.1",
    "Port": 40001
  }
}
```

Then point your client at:

```
ws://127.0.0.1:40001/
```

The server pushes the TCI handshake immediately after the WebSocket upgrade, ending with `ready;`. All commands are ASCII text frames, semicolon-terminated:

```
command:arg1,arg2,...;
```

### Configuration options

| Option | Default | Notes |
|---|---|---|
| `Enabled` | `false` | Master switch |
| `BindAddress` | `"127.0.0.1"` | Localhost only by default — change to `0.0.0.0` only on trusted LANs |
| `Port` | `40001` | ExpertSDR3 standard |
| `RateLimitMs` | `50` | VFO/DDS coalescing interval (20 Hz default) |
| `SendInitialStateOnConnect` | `true` | Push current state burst after handshake |
| `CwBecomesCwuAbove10MHz` | `false` | Legacy CW mode mapping shim |
| `LimitPowerLevels` | `false` | Clamp drive to 50%, tune-drive to 25% (unattended-operation safety) |

### Supported clients

- **Loggers:** Log4OM, N1MM+ (via TCI bridge)
- **Digital modes:** JTDX, WSJT-X (via TCI bridge), FT8/FT4 decoders
- **CW skimmers:** Morse decoders that consume the IQ stream — note that **TX-side CW** through Zeus is not yet implemented (see roadmap below)
- **SDR display tools:** Third-party spectrum analyzers and remote consoles

### REST control

TCI server status and pending configuration are exposed over the standard Zeus REST API:

- `GET /api/tci/status` — current state, client count, port availability, restart-required flag
- `POST /api/tci/config` — update pending configuration (requires app restart to take effect)
- `POST /api/tci/test` — probe whether a given address/port is bindable

---

## Status at a glance

| Area | Status |
|---|---|
| Handshake (init commands + `ready;`) | ✅ Complete |
| VFO / mode / filter control | ✅ Complete |
| MOX, TUN, drive | ✅ Complete |
| AGC gain | ✅ Complete |
| RX S-meter polling + push | ✅ Complete |
| TX power / SWR / ALC push | ✅ Complete |
| Outbound IQ stream (panadapter, skimmers) | ✅ Complete |
| Outbound RX audio stream | ✅ Complete |
| **Inbound TX audio upload (`TRX:0,true,tci`)** | ✅ **New in this branch** |
| **TX_CHRONO pacing emitter** | ✅ **New in this branch** |
| NR / NB / ANF / ANC | ✅ Wired |
| Preamp / attenuator | ✅ Complete |
| DX cluster spots (in/out) | ✅ Stored, not rendered on panadapter yet |
| `RX_SENSORS_ENABLE` / `TX_SENSORS_ENABLE` | ✅ Spec-correct shape `bool[,interval]` |
| `RX_CHANNEL_SENSORS` (TCI 2.0) | ✅ Sent to opted-in clients |
| `VFO_LOCK` (TCI 2.0) | 🟡 Echo-only stub |
| `DIGL_OFFSET` / `DIGU_OFFSET` | 🟡 Stored per session, not yet applied |
| Mute / squelch / RIT / XIT / split | 🟡 Echo-only stubs |
| **CW (`cw_msg`, `cw_macros`, `cw_macros_speed`, `keyer`)** | ✅ Wired into CwEngine; `cw_macros_empty` notification on queue drain |
| `cw_macros_delay`, `cw_macros_speed_up/down`, `cw_macros_stop`, `cw_terminal`, `callsign_send` | ❌ Out of scope for the first CW pass |
| `AGC_MODE` / `RX_NB_PARAM` | ❌ Backend missing |
| `LINE_OUT_*` (server-side recording) | ❌ Not planned |

Legend: ✅ working · 🟡 partial / stub-only · ❌ missing

---

## What's implemented (TCI 2.0 surface)

### §4.1 — Initialization (server → client on connect)

| Command | Notes |
|---|---|
| `protocol:ExpertSDR3,2.0;` | Advertises TCI 2.0 |
| `device:Zeus;` | |
| `receive_only:false;` | |
| `trx_count:1;` | Single transceiver |
| `channels_count:1;` | Single VFO (no VFO B yet) |
| `vfo_limits:0,61440000;` | 0 – 61.44 MHz |
| `if_limits:±halfRate;` | Mirrors radio sample rate |
| `modulations_list` | AM, SAM, DSB, LSB, USB, CWL, CWU, FM, DIGL, DIGU, SPEC, DRM |
| `iq_samplerate` / `audio_samplerate` | 192k IQ / 48k audio defaults |
| `audio_stream_*` | float32, 2-ch, 2048 samples, 50 ms buffering |
| Initial state burst | VFO, mode, filter, drive, MOX, TUN, mute, RX-enable |
| `start;` `ready;` | Sentinel — client begins streaming subscription |

### §4.2 — Bidirectional control commands

**Working with backend:**

- `vfo`, `dds`, `if`, `modulation`, `rx_filter_band`, `tx_filter_band_ex`
- `trx` (now parses 3rd arg `signal_source` — `tci`, `mic1`, `mic2`, `micPC`, `ecoder2`)
- `tune`, `drive`, `tune_drive`
- `agc_gain`
- `rx_nb_enable` / `nb_enable` (NB1)
- `rx_nb2_enable` (NB2)
- `rx_anf_enable` / `anf_enable`
- `rx_anc_enable` / `anc_enable` (SNB)
- `rx_nr_enable` / `nr_enable` (NR1) and `rx_nr_enable_ex` (NR1/NR2/NR3 with level)
- `volume`, `rx_volume` (both wired to `RxAfGainDb`)
- `preamp`, `attenuator`
- `rx_antenna` (acked-only — Zeus has no switchable antenna API yet)
- `rx_enable`, `rx_channel_enable` (TCI 2.0 3-arg form)
- `start`, `stop`, `run_cat_ex` (PS0/PS1/ZZTX0)

**Stubs only — echoed but no backend:**

- `mute`, `rx_mute`
- `lock`, `vfo_lock`
- `sql_enable`, `sql_level`
- `mon_enable`, `mon_volume`
- `split_enable`
- `rit_enable`, `rit_offset`
- `xit_enable`, `xit_offset`
- `rx_bin_enable`
- `digl_offset`, `digu_offset` (per-session storage; not yet routed to passband)
- `tx_profile_ex`, `tx_profiles_ex` (Zeus has no profile system; returns `Default`)

### §4.3 — Unidirectional control

- `iq_start`, `iq_stop`, `iq_samplerate`
- `audio_start`, `audio_stop`, `audio_samplerate`
- `audio_stream_sample_type` / `_channels` / `_samples`, `tx_stream_audio_buffering` (echoed; only `audio_stream_channels` actually drives behaviour)
- `spot`, `spot_delete`, `spot_clear`

### §4.4 — Notification commands (server → client)

**Sent:**

- `rx_smeter:rx,chan,dBm` — broadcast on every meter update
- `rx_channel_sensors` — sent only to clients that opted in via `rx_sensors_enable`
- `tx_sensors` — sent only to clients that opted in via `tx_sensors_enable`
- `tx_power`, `tx_forward_power`, `tx_swr`, `swr`, `tx_alc` — standalone meter pushes
- `tx_frequency` — on state change
- `rx_step_att_ex`, `rx_preamp_att_ex` — when changed
- `start;` / `stop;` — on radio connect / disconnect
- **`tx_chrono` (binary, type=3)** — periodic 50 ms pacing during MOX, sent only to sessions whose TRX source = TCI

**Inbound notification handlers:**

- `rx_sensors_enable:bool[,interval]` — TCI 2.0 spec shape (no rx index). `interval` clamped 30–1000 ms.
- `tx_sensors_enable:bool[,interval]` — same.

### §7 — Binary streams

| Stream | Direction | Type | Status |
|---|---|---|---|
| IQ panadapter | S→C | 0 | ✅ FLOAT32 at radio native rate (192 kHz default) |
| RX audio | S→C | 1 | ✅ Stereo (L=R) FLOAT32 @ 48 kHz |
| **TX audio** | **C→S** | **2** | ✅ **Now routed: stereo→mono mixdown, 960-sample mic blocks into WDSP** |
| **TX_CHRONO** | **S→C** | **3** | ✅ **50 ms cadence while MOX is on** |
| LINE_OUT | S→C | 4 | ❌ Not implemented |

Header layout matches TCI v1.x §7.1 (64-byte little-endian); dispatch keys off `type` at offset 24 per markdown spec quirk #7.

---

## TX audio path (new in this branch)

When a client sends `TRX:0,true,tci;`:

1. The session latches `_txSourceIsTci = true`.
2. MOX is asserted.
3. The TCI server starts the `TX_CHRONO` timer (50 ms cadence).
4. Each tick emits a `type=3` binary frame to this session, signalling the client to upload another audio block.
5. The client uploads `type=2` binary frames (stereo FLOAT32 at 48 kHz, default 2048-sample blocks).
6. `TciTxAudioReceiver` parses the header, validates sample rate / type, mixes stereo to mono (averaging L+R), and chunks into 960-sample / 20 ms / 48 kHz f32le blocks.
7. Each block is forwarded to `TxAudioIngest.OnMicPcmBytes` — the same hot-path the browser microphone feeds.
8. `TxAudioIngest` accumulates into WDSP TX block size, runs `ProcessTxBlock`, and pushes the resulting modulated IQ to the radio.

On `TRX:0,false;` (or any change of TRX source away from `tci`), the receiver's accumulator is reset and the chrono timer stops.

**Limitations of the current TX path:**

- Inbound sample rate must be 48 kHz (resampling not implemented). Frames at any other rate are dropped with a debug log.
- Inbound sample type must be FLOAT32. Int16/24/32 frames are dropped (no real client emits them).
- A single TCI client at a time can hold the TX source. If two clients both `TRX:0,true,tci;`, both feed the same WDSP TX bus — last-writer wins per WDSP block. Multi-client TX arbitration is **not** implemented.
- The 200 ms first-writer-wins lockout from spec §3.5 is **not** implemented.

---

## Known gaps / "missing" inventory

This is the explicit "what's not done" list, organised by spec section. Issues maintained against this list are welcomed.

### CW — first pass landed; remaining gaps below

The four highest-leverage CW commands route through Zeus's host-side CW
engine (see `Zeus.Server.Hosting/CwEngine.cs`) — both macros (mapped
against the same `CwSettingsStore` the on-screen macro pad edits) and
free-form text reach the air, and the engine raises `cw_macros_empty`
when the queue drains so loggers can detect end-of-message:

- ✅ `cw_msg:rx,part[,part…][,repeat]` — concatenates parts, optional
  small-integer trailing repeat count
- ✅ `cw_macros:slot` — 1-based slot index into the persisted macro array
- ✅ `cw_macros_speed:wpm` — sets the operator's persisted WPM (shared
  with the UI); query form echoes current
- ✅ `keyer:rx,bool[,durationMs]` — manual key-down / key-up, with the
  optional TCI 1.9.1 auto-release timer
- ✅ `cw_macros_empty` (server → client) — broadcast on Sending → Idle
  transitions

The remaining CW commands stay out of scope for this pass:

- `cw_macros_delay`, `cw_macros_speed_up/down`, `cw_macros_stop`
- `cw_terminal` mode
- `callsign_send` notification (mid-message callsign correction)
- Per-receiver `cw_keyer_speed` (Zeus tracks one WPM, not per-RX)

These can land alongside future contest-logger work as needed.

### Control commands missing entirely

| Command | Reason | Recommendation |
|---|---|---|
| `agc_mode:rx,(normal\|fast\|off)` | RadioService only exposes `SetAutoAgc(bool)`; no normal/fast/off API yet | Add `AgcMode` enum and `RadioService.SetAgcMode` then wire here |
| `rx_nb_param:rx,threshold,duration` | `NbConfig` needs `Threshold` / `Duration` parameters | Extend NbConfig |
| `rx_apf_enable` | Audio peak filter — CW-specific, no Zeus backend | Defer with CW engine |
| `rx_dse_enable` | Digital surround for CW — niche | Defer |
| `rx_nf_enable` | Manual notch module — separate from ANF | Future |
| `rx_balance` | L/R balance — Zeus is mono | Skip |
| `clicked_on_spot` / `rx_clicked_on_spot` | Server-side notification when operator clicks a spot | Needs panadapter-click flow |
| `tx_footswitch` | Footswitch state notification | No footswitch wiring on Zeus |
| `app_focus` / `set_in_focus` | ExpertSDR3 main-window focus — meaningless for headless | Skip |
| `line_out_*` | Server-side WAV/MP3 recording | Niche; defer indefinitely |

### Stubs that need backends

These commands are spec-conformant on the wire (handshake / echo) but currently lie about their state — the SET is acked, but no radio behaviour changes. To promote a stub to "wired":

1. `mute` / `rx_mute` — needs `RadioService.SetRxMute(bool)`
2. `sql_enable` / `sql_level` — needs squelch backend
3. `lock` / `vfo_lock` — needs frequency-lock state
4. `split_enable` + dual-VFO — large feature
5. `rit_enable` / `rit_offset`, `xit_enable` / `xit_offset` — large feature
6. `mon_enable` / `mon_volume` — needs sidetone path
7. `rx_bin_enable` — needs binaural / pseudo-stereo demod option in WDSP

### Multi-client behaviour gap

Spec §3.5 mandates a 200 ms "first-writer-wins" lockout per parameter, so two clients fighting over (e.g.) a VFO change don't oscillate. Zeus broadcasts every SET to every client immediately with **no lockout**. With a single TCI client (the common case) this is invisible; with multiple loggers it could cause UI thrash.

---

## Protocol-version advertised vs. implemented

Zeus emits `protocol:ExpertSDR3,2.0;` to advertise TCI 2.0 surface. Most third-party clients only check that the version is ≥ their minimum (1.8 / 1.9). If a client whitelists exact version strings ("1.9" only), the version string is configurable in `Zeus.Server.Hosting/Tci/TciProtocol.cs` (`ProtocolVersion`).

---

## Reference

- **TCI 2.0 official spec PDF:** `docs/references/TCI/TCI Protocol.pdf` (Expert Electronics, 12 Jan 2024)
- **Markdown derivative spec** (from the v2.5.1 client): https://github.com/brianbruff/TCI_SunSdr_Specification
- **Test plan:** `docs/tci-test-plan.md`
- **Source:** `Zeus.Server.Hosting/Tci/`
