# TCI Test Plan — feature/tci_additional_support

This is the verification plan for the TCI 2.0 changes on the `feature/tci_additional_support` branch:

- TX audio upload path (inbound `type=2` binary frames + outbound `type=3` TX_CHRONO)
- `TRX:0,true,tci` 3rd-arg parsing
- Off-spec command-name routing (`rx_nb_enable`, `rx_anf_enable`, `rx_anc_enable`, `rx_nr_enable`, `rx_channel_enable`)
- `RX_SENSORS_ENABLE` / `TX_SENSORS_ENABLE` argument-shape fix
- `DIGL_OFFSET` / `DIGU_OFFSET` per-session storage
- `rx_nb2_enable` (NB2 backend wiring)

CW remains out of scope and on the roadmap — see the [TCI wiki page](wiki/TCI.md) for the parity matrix.

---

## 1. Automated tests (already wired)

```bash
dotnet build Zeus.slnx
dotnet test Zeus.slnx --no-build --filter "FullyQualifiedName~Tci"
```

**Pass criteria:** all TCI tests green. Current count on this branch:

| Suite | Tests |
|---|---|
| `TciHandshakeTests` | 18 |
| `TciProtocolTests` | many existing |
| `TciStreamPayloadTests` | existing + 5 new for `TryParseHeader` / `BuildTxChrono` |
| `TciTxAudioReceiverTests` | **new — 9 cases** |
| `TciRateLimiterTests` | 5 (skipped, timing-flaky) |

The new test cases under `TciTxAudioReceiverTests` cover:

- Stereo 48 kHz full block → one mic block forwarded
- Stereo L≠R → mono mixdown averages
- Non-aligned 2048-frame uploads → remainder buffered across calls
- Mono 48 kHz → pass through
- Wrong sample rate (24 kHz) → frame dropped, drop counter increments
- Wrong sample type (Int16) → frame dropped
- Invalid channel count (6) → frame dropped
- `Reset()` → accumulator cleared (new TX starts from silence)
- Lying length field → trusts actual payload size, no over-read

**Pass criteria for the full solution:** `dotnet test Zeus.slnx --no-build` reports 0 failed across all projects.

---

## 2. Manual test scenarios

### Setup

1. **Backend:** `dotnet run --project OpenhpsdrZeus` (listens on :6060 by default; TCI on :40001 when enabled).
2. **Frontend:** `npm --prefix zeus-web run dev` (Vite on :5173).
3. **Enable TCI** in `appsettings.Development.json`:
   ```json
   { "Tci": { "Enabled": true, "BindAddress": "127.0.0.1", "Port": 40001 } }
   ```
4. **Connect to a radio** via the Zeus web UI (HL2 on the bench is the standard test target).
5. Verify TCI server bind: log line `tci.listening bind=127.0.0.1 port=40001` on backend startup.

A quick TCI smoke client (Python `websockets` or any browser DevTools console) is sufficient for §2.1–2.3:

```python
import asyncio, websockets
async def go():
    async with websockets.connect("ws://127.0.0.1:40001/") as ws:
        # Print every text frame; ignore binary
        async for msg in ws:
            if isinstance(msg, str): print(msg.strip())
            else: print(f"[binary {len(msg)}B]")
asyncio.run(go())
```

---

### 2.1 Handshake against a real client

| Test | Action | Pass criteria |
|---|---|---|
| **2.1.1** Thetis | Open Thetis with TCI client pointed at `ws://localhost:40001/`. | Thetis TCI client shows "Connected". Zeus log shows `tci.handshake sent client=… commands=…`. |
| **2.1.2** Log4OM | Configure Log4OM CAT as TCI on `127.0.0.1:40001`. | Log4OM shows VFO/mode in real time and tracks Zeus VFO scrolls within 50 ms. |
| **2.1.3** Required init frames | Connect any client; tail backend log. | Handshake includes `protocol:ExpertSDR3,2.0;`, `device:Zeus;`, `vfo_limits`, `if_limits`, `modulations_list`, `ready;`. |

---

### 2.2 RX-side parity (regression — already worked, verify still does)

| Test | Action | Pass criteria |
|---|---|---|
| **2.2.1** VFO push | Spin VFO in Zeus web UI. | Connected TCI client receives `vfo:0,0,<hz>;` updates rate-limited to ≈20 Hz. |
| **2.2.2** Mode change | Toggle USB/LSB in Zeus. | Client receives `modulation:0,USB;` then `modulation:0,LSB;`. |
| **2.2.3** S-meter | Tune to a signal. | Client receives `rx_smeter:0,0,<dBm>;` regularly. |
| **2.2.4** IQ stream | Client sends `iq_start:0;`. | Binary frames begin (type=0, header receiver=0, samplerate=192000). Volume of frames matches Zeus IQ rate. |
| **2.2.5** RX audio stream | Client sends `audio_start:0;`. | Binary frames begin (type=1, header samplerate=48000). Frames decode as stereo FLOAT32. |

---

### 2.3 Off-spec command-name routing (new)

For each, send the spec-conformant command and verify the radio's NR/NB/ANF/SNB state actually changes (not just an echo).

| Test | Send | Expected backend effect |
|---|---|---|
| **2.3.1** | `rx_nb_enable:0,true;` | Zeus log: `radio.nr.cfg NbMode=Nb1`. UI reflects NB1 on. |
| **2.3.2** | `rx_nb_enable:0,false;` | NB1 off. NB2 untouched if it was on. |
| **2.3.3** | `rx_nb2_enable:0,true;` | NbMode=Nb2. NB1 turns off (mutually exclusive). |
| **2.3.4** | `rx_anf_enable:0,true;` | NrConfig.AnfEnabled=true. UI reflects ANF on. |
| **2.3.5** | `rx_anc_enable:0,true;` | NrConfig.SnbEnabled=true. |
| **2.3.6** | `rx_nr_enable:0,true;` | NrMode=Anr (NR1). |
| **2.3.7** | `rx_channel_enable:0,1,true;` | Echo only (Zeus single-RX). Reply: `rx_channel_enable:0,1,true;`. No backend change expected. |

---

### 2.4 Sensors-enable arg shape (regression target)

Older Zeus accepted `rx_sensors_enable:0,true;` (rx,bool) — wrong shape. Now spec-correct.

| Test | Send | Expected reply |
|---|---|---|
| **2.4.1** | `rx_sensors_enable:true;` | `rx_sensors_enable:true;` echo. Server begins emitting `rx_channel_sensors:0,0,<dBm>;` to this session. |
| **2.4.2** | `rx_sensors_enable:true,200;` | `rx_sensors_enable:true,200;` echo. Interval stored (informational; cadence still tied to internal meter rate). |
| **2.4.3** | `rx_sensors_enable:false;` | `rx_sensors_enable:false;` echo. `rx_channel_sensors` pushes stop to this session. |
| **2.4.4** | `tx_sensors_enable:true;` | Echo. Server emits `tx_sensors:0,0,<fwd>,<rev>,<swr>;` while transmitting. |

---

### 2.5 DIGL_OFFSET / DIGU_OFFSET

Per-session storage only — not yet routed to a passband shift. Verifies the protocol handshake doesn't error on these.

| Test | Send | Expected reply |
|---|---|---|
| **2.5.1** | `digl_offset:1500;` | `digl_offset:1500;` |
| **2.5.2** | `digl_offset;` (GET) | `digl_offset:1500;` (recalls last-set value) |
| **2.5.3** | `digl_offset:99999;` | `digl_offset:4000;` (clamped) |
| **2.5.4** | `digu_offset:2200;` | `digu_offset:2200;` |
| **2.5.5** | Disconnect / reconnect | Values reset to 0 (per-session, not persisted). |

---

### 2.6 TX audio upload (the big test)

This is the headline feature. Setup:

- HL2 on the bench, dummy load attached.
- TX power kept low (drive 10–20%) for first-light.
- `appsettings.Development.json` has TCI enabled.

#### 2.6.1 Smoke test — synthetic sine via custom client

Use a small Python script that:

1. Connects to `ws://127.0.0.1:40001/`.
2. Waits for `ready;`.
3. Sends `audio_stream_channels:2;`, `audio_stream_samples:2048;`, `audio_stream_sample_type:float32;`, `audio_samplerate:48000;`, `tx_stream_audio_buffering:50;`.
4. Sends `trx:0,true,tci;`.
5. On each inbound binary frame with `type==3` (TX_CHRONO at offset 24), sends a binary frame with `type=2`, payload = 2048 stereo FLOAT32 samples of a 1 kHz sine wave at -6 dBFS.
6. After 5 seconds, sends `trx:0,false;` and disconnects.

**Pass criteria:**

- Backend log shows MOX rising edge (`tx.mox on=True`), then `tci.tx.chrono started interval=50ms`.
- `tx.peaks` log line shows `mic≈0.50` and `iq≈<non-zero>` while transmitting.
- A spectrum analyzer / second receiver picks up a clean 1 kHz tone offset from VFO carrier (USB: `VFO + 1000 Hz`).
- TX power meter shows ≈10–15 W into a dummy load (depends on drive setting).
- After `trx:0,false;`: MOX falls, chrono timer stops (`tci.tx.chrono stopped` log), and silence resumes.
- `_txAudioReceiver.TotalFramesAccepted` ≈ TotalSamplesForwarded / 960 (within rounding).

#### 2.6.2 Source-gating

| Test | Action | Pass criteria |
|---|---|---|
| **2.6.2.a** | Send TX audio binary frame **without** prior `trx:0,true,tci;` | Frame is dropped silently (debug log: `tci.tx.audio dropped (TRX source != tci)`). No RF emitted. |
| **2.6.2.b** | Send `trx:0,true,mic1;` then upload TX audio | Same: dropped (`source != tci`). MOX is on, but TX path uses local mic. |
| **2.6.2.c** | `trx:0,true,tci;` then audio frames, then `trx:0,false;`, then more frames | Pre-falling-edge frames TX. Post-falling-edge frames dropped (`MOX off`). |
| **2.6.2.d** | After (c), `trx:0,true,tci;` again with new audio | Receiver accumulator was reset on the falling edge — first new sample doesn't replay tail of previous TX. |

#### 2.6.3 WSJT-X / JTDX integration

If a digital-mode app supports TCI mic upload (JTDX TCI build, ExpertSDR3 mediated, etc.):

- Configure: rig=TCI, host=127.0.0.1:40001.
- Tune to 14.074 USB.
- Press **Enable TX** + transmit a CQ.
- Verify on a second receiver that the FT8 burst is on-frequency and decodes cleanly.

**Pass criteria:** at least one round-trip exchange with a real station, OR a clean self-decode on a separate receiver.

#### 2.6.4 TX_CHRONO emitter

| Test | Action | Pass criteria |
|---|---|---|
| **2.6.4.a** | `trx:0,true,tci;` | Backend log: `tci.tx.chrono started interval=50ms`. Client receives binary frames with `type=3` at offset 24 every ~50 ms. Frame body length = 0. |
| **2.6.4.b** | `trx:0,false;` | `tci.tx.chrono stopped`. No more chrono frames. |
| **2.6.4.c** | Two clients, one with TCI source, one without | Only the TCI-source client gets chrono frames. |
| **2.6.4.d** | No clients with TCI source | Timer still runs while MOX is on (cheap), but no frames emitted. |

#### 2.6.5 Backpressure / overflow

Synthetic stress:

- Connect a client and send TX audio at 4× the normal rate (e.g. 200 frames/s of 2048 samples).
- Observe backend.

**Pass criteria:** Eventually a `tci.tx.audio overflow fill=… cap=16384` warning fires. The accumulator is dropped and reset; the radio doesn't crash; no audio glitch propagates downstream beyond the dropped batch.

---

### 2.7 Regressions to watch

These passed on `release/0.5.0` and must still pass on this branch:

| Test | Pass criteria |
|---|---|
| **2.7.1** Local mic TX still works | Web UI MOX → speak → measurable RF output. (TCI changes did not touch the browser-mic path.) |
| **2.7.2** Two-tone test still works | UI two-tone → IMD products visible on second RX, no spurious behaviour. |
| **2.7.3** Tune button still works | UI tune → carrier emitted at tune-drive %, falls back to mic on tune-off. |
| **2.7.4** RX audio in the web UI | Web speaker plays audio while a TCI client is also subscribed to RX audio. (Single producer, multiple consumers.) |
| **2.7.5** IQ stream to a panadapter mirror (e.g. CW Skimmer Server) | Skimmer continues to render with no missed frames during a TCI-keyed TX (chrono timer doesn't compete with IQ broadcast for the binary queue). |

---

## 3. What this branch does NOT test

By design, the following are **out of scope** and remain on the roadmap:

- **Any CW path.** `cw_macros`, `cw_msg`, `cw_keyer_speed`, `keyer`, `cw_terminal` — all still ack-only stubs.
- `agc_mode` / `rx_nb_param` / `rx_nf_enable` / `rx_apf_enable` / `rx_dse_enable` / `rx_balance` — not implemented.
- `mute`, `sql_*`, `lock`, `vfo_lock`, `split_enable`, `rit_*`, `xit_*`, `mon_*` — still echo-only stubs.
- `line_out_*` recording family — niche, deferred.
- Multi-client 200 ms first-writer-wins lockout from spec §3.5.
- Inbound TX audio at sample rates other than 48 kHz, or sample types other than FLOAT32.

---

## 4. Sign-off checklist

Before merging this branch:

- [ ] §1 automated tests: `dotnet test Zeus.slnx` passes 0 failures
- [ ] §2.1.1 Thetis handshake works
- [ ] §2.2.x RX-side parity unchanged
- [ ] §2.3.x off-spec name routing changes radio backend (verified via Zeus UI mirror)
- [ ] §2.4.x sensors_enable echo shape correct
- [ ] §2.5.x DIGL/DIGU echo
- [ ] §2.6.1 synthetic sine emits clean tone
- [ ] §2.6.2 source-gating drops frames as expected
- [ ] §2.6.3 a real digital-mode client can transmit (or self-decode confirmed) — **stretch goal**, can ship without if 2.6.1 passes
- [ ] §2.6.4 chrono timer starts/stops on MOX edges
- [ ] §2.7.x no regressions in the local-mic / two-tone / tune / RX-audio paths

If §2.6.3 is not achievable before merge, document in the PR description and follow up in a separate verification pass with whichever client is most accessible.
