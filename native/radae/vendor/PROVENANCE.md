# native/radae/vendor — RADE V1 vendored source provenance

Zeus builds its RADE V1 decoder (`libzeus_rade`, see `../CMakeLists.txt`) from three
upstream C slices. They are **not committed to the Zeus git tree** — together they are
~95 MB, almost all of it compiled-in neural-network weight tables. CI
(`build-native-libs.yml`) vendors them into this `vendor/` directory before
configuring the CMake project. This file is the single source of truth for *what* to
vendor, *from where*, and *under which license*.

## Upstream

All three slices are vendored from a single upstream repository:

| field        | value |
|--------------|-------|
| Upstream     | https://github.com/sv1eia/Thetis-RADE |
| Pinned SHA   | `f7605a46bd21275ab8b9edd00d4a1b6fae6eabe8` |
| HEAD subject | *"Remove vendored radae_nopy; build rade.lib from in-repo radae_c and update credits/docs"* |

(That HEAD commit is the one that switched Thetis-RADE itself from `radae_nopy` to the
in-repo `radae_c` + co-vendored `opus_dnn` model — i.e. exactly the composition Zeus
adopts here.)

## Slices to vendor

Copy these three directories from the pinned checkout into `native/radae/vendor/`,
preserving the directory name (drop the `Project Files/lib/` prefix):

| vendor/ target | upstream path (under the SHA above)        | license       |
|----------------|--------------------------------------------|---------------|
| `radae_c/`     | `Project Files/lib/radae_c/`               | BSD-2-Clause  |
| `opus_dnn/`    | `Project Files/lib/opus_dnn/`              | BSD-3-Clause  |
| `freedv_text/` | `Project Files/lib/freedv_text/`           | mixed: see below |

### `radae_c/` — BSD-2-Clause (David Rowe & Jean-Marc Valin, © 2024; C port by Christos Nikolaou SV1EIA, © 2026)
Pure-C, Python-free RADE modem. IQ → 36-float feature frames; encoder/decoder weights
compiled in (`src/rade_enc_data.c`, `src/rade_dec_data.c`, ~24 MB each). The public API
(`src/rade_api.h`, implemented by `src/radc_api.c`) is byte-for-byte compatible with the
reference `rade_api.h`. Only the **decode** path is used by Zeus, but the whole `src/`
tree is vendored (the tx files are tiny and the data tables are shared).

### `opus_dnn/` — BSD-3-Clause (Xiph.Org / Skype / Jean-Marc Valin et al.)
Xiph Opus pinned at upstream `xiph/opus` commit
`940d4e5af64351ca8ba8390df3f555484c567fbb` (per `opus_dnn/commit_pin.txt`), carrying the
DNN/FARGAN vocoder. Built via its own CMake with **`OPUS_DEEP_PLC=ON`**, which compiles
`dnn/fargan.c`, `dnn/fargan_data.c`, `dnn/nnet.c`, `dnn/freq.c`, etc. — providing
`fargan_synthesize()` (the 36-float-feature → 160-sample @ 16 kHz vocoder) that the shim
calls. (`OPUS_DRED`/`OPUS_OSCE` are **not** required for the V1 decode path.)

> **CI caveat — stripped SIMD subtrees.** The Thetis-RADE `opus_dnn` slice is the
> MSVC-x64 scalar build: its `silk/x86/`, `silk/arm/`, `celt/x86/`, `celt/arm/`,
> `dnn/x86/`, `dnn/arm/` directories are **absent**, but the CMake `*_headers.mk` /
> `*_sources.mk` lists still reference ~65 of those files. With
> `OPUS_DISABLE_INTRINSICS=ON` those SIMD *sources* are not compiled, but CMake's
> `target_sources()` still validates that the referenced *header* entries exist.
> CI must create empty placeholder files for every missing referenced path before
> configuring (one-liner: for each `silk|celt|dnn|src/...\.[ch]` token in the `.mk`
> files that does not exist on disk, `mkdir -p $(dirname) && : > $file`). This was the
> only patch needed to make `opus_dnn` configure on Linux/CMake.

### `freedv_text/` — mixed license (vendored AND wired)
The FreeDV-GUI reliable-text (LDPC) codec for the on-air **EOO callsign** frame.
radae_c's built-in `rade_rx_get_eoo_callsign()` uses a simple 7-bit-MSB packing
that is *not* the FreeDV-GUI on-air format. The shim therefore uses
`freedv_text/src/rade_text.c` (CRC8 + 6-bit chars + LDPC HRA_56_56 +
gp_interleaver) for **both** TX-encode and RX-decode of the callsign, so it
interoperates with FreeDV-GUI RADE stations. Compiled into `zeus_rade` via
`../CMakeLists.txt` (`FREEDV_TXT_SOURCES`) along with the codec2 LDPC files; on
Windows a one-file `shim/compat/alloca.h` maps `<alloca.h>`→`<malloc.h>` (the
codec2 sources hardcode the glibc/BSD header). Licenses within the slice:
- `freedv_text/src/rade_text.{c,h}` — BSD-2/3-Clause (Mooneer Salem)
- `freedv_text/codec2/*` (LDPC: `mpdecode_core.c`, `gp_interleaver.c`, `ldpc_codes.c`,
  `HRA_56_56.c`, `phi0.c`) — **LGPL-2.1** (David Rowe / codec2)

## License roll-up

| component   | license      | copyright holders | copyleft? |
|-------------|--------------|-------------------|-----------|
| radae_c     | BSD-2-Clause | © 2024 David Rowe & Jean-Marc Valin (RADE authors); © 2026 Christos Nikolaou SV1EIA (C port) | no |
| opus_dnn    | BSD-3-Clause | © Xiph.Org / Skype / Jean-Marc Valin et al. | no |
| freedv_text/src (rade_text) | BSD | © Mooneer Salem | no |
| freedv_text/codec2 (LDPC) | LGPL-2.1 | © David Rowe / codec2 | weak (dynamic-link OK) |

The audio RX/TX build (`radae_c` + `opus_dnn` + Zeus shim) is **BSD-only**. The
LGPL-2.1 codec2 LDPC code is pulled in for the EOO-callsign path (now wired);
`libzeus_rade` is a shared library, so dynamic linking satisfies LGPL-2.1.

## Proven build + decode (WSL Ubuntu-24.04, 2026-06-24)

The one-shared-library composition above was built and validated end-to-end:

- **opus_dnn** built static via CMake: `-DOPUS_DEEP_PLC=ON -DOPUS_DISABLE_INTRINSICS=ON`
  (+ the placeholder-stub step). FARGAN symbols present in `libopus.a`.
- **radae_c** compiled with `-DRADE_STATIC -DRADE_PYTHON_FREE=1 -DIS_BUILDING_RADE_API=1`.
- Linked **radae_c objects + shim + whole-archived opus_dnn** into one
  `libzeus_rade.so` (11 MB). All `zeus_rade_*` symbols exported.
- **Decode test** on a 5'26" off-air RADE recording (`FDV_offair.wav`, 48 kHz mono),
  fed as **real samples** (`RADE_COMP.real = s, .imag = 0` — radae_c's modem expects a
  real 8 kHz signal; this is the reference feed in `ChannelMaster/radae.c`, *not* a
  Hilbert IQ front-end like radae_nopy's `real2iq`):

  ```
  zeus_rade_test: pcm_samples=5017920 ticks=2717 synced=2620 last_snr=14dB
  out.wav stat: Maximum amplitude 0.999969  RMS 0.116568  (313.6 s of real speech)
  ```

  **2620 synced ticks, 14 dB SNR, near-full-scale audio** — matches the prior
  radae_nopy reference run (~2628 ticks / 14 dB / 0.9999 max-amp). RADE V1 decode from
  the radae_c + opus_dnn base is PROVEN.

## Transmit + EOO callsign (proven, 2026-06-24)

The shim gained a full TX path (`zeus_rade_tx`): 16 kHz speech → opus_dnn LPCNet
feature analyzer (`lpcnet_compute_single_frame_features`) → `rade_tx` → modem IQ;
the transmitted SSB audio is the **real** part of the IQ (inverse of the RX feed).
The EOO callsign uses `freedv_text` (`rade_text`), not radae_c's 7-bit packing, so
it matches FreeDV-GUI on the air. A self-contained encode→decode loopback
(`zeus_rade_test loopback N9WAR`, win-x64 MinGW) PROVES it end-to-end:

```
loopback: ticks=60 synced=48 last_snr=35dB pcm=89440 callsign=N9WAR
loopback: callsign round-trip OK ('N9WAR')   loopback: PASS
```

i.e. the TX modem waveform re-syncs in the decoder (48 ticks / 35 dB) and the
LDPC EOO callsign round-trips byte-exact. The managed RadeModem TX→RX loopback
test mirrors this through the C# resampler/ring/P-Invoke chain.

## Proven build + decode (Windows / MinGW, 2026-06-24)

The same composition was then PROVEN on native Windows with MinGW UCRT64 gcc 16.1
(Ninja, `OPUS_DISABLE_INTRINSICS=ON`):

- `zeus_rade.dll` (11.0 MB) linked clean; all 12 `zeus_rade_*` symbols exported.
- **Standalone** — `objdump -p` shows deps only on `KERNEL32` + `ADVAPI32` +
  system UCRT (`api-ms-win-crt-*`); NO `libgcc_s_seh-1.dll` / `libwinpthread-1.dll`
  / `libstdc++`. The static-link flags are now in `../CMakeLists.txt` (`if(MINGW)`).
- Decoded `FDV_offair.wav` (Windows exe): `synced=2620 last_snr=14dB`,
  decoded audio max-amp `0.999969` — **byte-for-byte identical to the Linux run**.

Windows build notes now folded into `../CMakeLists.txt`:
- `if(WIN32) PREFIX ""` so MinGW emits `zeus_rade.dll`, not `libzeus_rade.dll`
  (matches the .NET loader's probe name).
- `if(MINGW)` static-runtime link (`-static -static-libgcc -Wl,-Bstatic`).
- `-O1` on the two ~24 MB weight `.c` files (compile-time/RAM; they are data).
- The `shim/zeus_rade_test.c` harness needs `_setmode(_O_BINARY)` on Windows (stdio
  binary mode) — added there; the production P/Invoke path doesn't use stdio.
