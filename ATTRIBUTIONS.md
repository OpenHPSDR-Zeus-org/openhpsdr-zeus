# Zeus — Provenance and Attributions

This file is the canonical, human-readable statement of provenance for the
Zeus project. It exists so that anyone reading the code — or auditing it —
can trace Zeus's lineage, see who the work rests on, and understand how the
licence obligations flow through the project.

Per-file headers reference this document by name. This file is the
authoritative list; those headers are a reminder.

## License

Zeus is distributed under the **GNU General Public License, version 2 or
(at your option) any later version** (GPL-2.0-or-later). The full licence
text is in [`LICENSE`](LICENSE). Every first-party source file in this
repository carries the `SPDX-License-Identifier: GPL-2.0-or-later` tag
plus a short-form copyright and attribution block.

This licence was chosen deliberately to align Zeus with its primary
upstreams and reference projects:

- **Thetis** — GPL v2 or later
- **WDSP** — GPL v2 or later
- **pihpsdr** — GPL v2 or later
- **DeskHPSDR** — GPL v2 or later

Zeus's "or later" clause preserves forward-compatibility with downstream
GPL v3 works.

## Zeus contributors

Zeus is maintained by:

- **Brian Keating (EI6LF)** — project lead
- **Douglas J. Cerrato (KB2UKA)** — maintainer
- **Christian Suarez (N9WAR)** — maintainer
- **Ramón Martínez (EA5IUE)** — contributor

Additional contributions are visible in `git log` and in the repository's
pull-request history.

## Relationship to Thetis

Zeus is **an independent reimplementation in .NET — not a fork** of
Thetis. No Thetis binary is distributed with Zeus, and no Thetis source
file is carried in the Zeus tree.

That said, Zeus was **developed with direct reference to the Thetis
source** as the authoritative specification of OpenHPSDR Protocol-1 /
Protocol-2 client behaviour. The following categories of knowledge were
learned by reading Thetis source:

- Protocol-1 and Protocol-2 discovery and framing
- WDSP initialisation ordering and channel-state transitions
- Meter pipelines (S-meter, TX-stage meters)
- AGC curves, filter widths, bandwidth scheduling
- TX safety behaviour (SWR trip, TX timeout, TUNE)
- Console/radio wiring conventions

Under the GPL, code whose structure, behaviour, or implementation
detail is substantially informed by a GPL-covered work is itself
a derivative work. Accordingly, the Zeus codebase is treated as
**subject to the GNU General Public License**, the licence of its
upstream. Zeus's per-file headers, this document, and the root
`LICENSE` file together carry the full GPL v2-or-later notice
through the derivation chain.

Where any Zeus file is later identified as a close port of a specific
Thetis source file — rather than behaviour-informed original code — that
file will carry an additional per-file header naming the Thetis source,
the original copyright holders, and the date of modification, as required
by GPL v2 §2(a).

## Thetis — lineage and contributors

Thetis continues a long-running GPL-governed software lineage:

1. **FlexRadio PowerSDR** — the original GPL-licensed Software-Defined
   Radio client from FlexRadio Systems.
2. **OpenHPSDR ecosystem** (TAPR / OpenHPSDR) — continuation of the
   PowerSDR codebase as an open-hardware / open-source SDR platform.
3. **Thetis** — the modernised OpenHPSDR client implementation used as
   Zeus's reference.

The authoritative Thetis tree referenced by Zeus is:
<https://github.com/ramdor/Thetis>

Zeus gratefully acknowledges the Thetis contributors whose work — carried
forward through the lineage above — made this project possible:

| Name | Callsign |
| --- | --- |
| Richard Samphire | MW0LGE |
| Warren Pratt | NR0V |
| Laurence Barker | G8NJJ |
| Rick Koch | N1GP |
| Bryan Rambo | W4WMT |
| Chris Codella | W2PA |
| Doug Wigley | W5WC |
| Richard Allen | W5SD |
| Joe Torrey | WD5Y |
| Andrew Mansfield | M0YGG |
| Reid Campbell | MI0BOT |
| Sigi Jetzlsperger | DH1KLM |
| **FlexRadio Systems** | *(corporate)* |

Some Thetis contributions carry dual-licensing statements in addition
to the GPL. Where Zeus references or is informed by a specific Thetis
source file, any such dual-licensing notice from that file is to be
preserved in the corresponding Zeus per-file header — not stripped to
GPL alone.

## WDSP

Zeus loads **WDSP** (Warren Pratt, NR0V) via P/Invoke for all on-air DSP.
WDSP source ships in-tree under [`native/wdsp/`](native/wdsp/); its
upstream licence, copyright notices, and author attribution are
preserved in every file as received. Zeus builds a shared library from
that source at build time — it does not modify WDSP.

WDSP is Copyright (C) Warren Pratt (NR0V) and is distributed under
**GNU General Public License, version 2 or later**. See
<https://github.com/TAPR/OpenHPSDR-Thetis/tree/master/Project%20Files/Source/wdsp>
for the upstream.

Five small shim / glue files under `native/wdsp/` and
`native/wdsp/stubs/` were authored by Zeus contributors and are
GPL-2.0-or-later under the Zeus copyright:

- `native/wdsp/wdsp_export.h`
- `native/wdsp/stubs/nr3/rnnoise.h`
- `native/wdsp/stubs/nr3/rnnr_stub.c`
- `native/wdsp/stubs/nr4/sbnr_stub.c`
- `native/wdsp/stubs/nr4/specbleach_adenoiser.h`

## libspecbleach

Zeus's NR4 (SBNR — Spectral Bleaching Noise Reduction) signal path links
against **libspecbleach** (Luciano Dato), vendored in-tree under
[`native/libspecbleach/`](native/libspecbleach/). The library is built as
a static sub-target of `libwdsp` with hidden symbol visibility, so the
SBNR exports surface from `libwdsp.{so,dll,dylib}` directly and end-users
do not see a separate runtime dependency.

libspecbleach is **Copyright (C) 2022 Luciano Dato
&lt;lucianodato@gmail.com&gt;** and is distributed under the **GNU Lesser
General Public License, version 2.1 or (at your option) any later
version** (LGPL-2.1-or-later). The full licence text is preserved
verbatim at
[`native/libspecbleach/LICENSE`](native/libspecbleach/LICENSE);
provenance and a re-vendor recipe are in
[`native/libspecbleach/VENDORING.md`](native/libspecbleach/VENDORING.md).

The vendored copy is the **MW0LGE-modified snapshot that ships with
Thetis**, sourced from
`Thetis/Project Files/lib/NR_Algorithms_x64/src/libspecbleach/`. This was
chosen over upstream `lucianodato/libspecbleach` so that Zeus's
`specbleach_adaptive_*` calls in `native/wdsp/sbnr.c` match Thetis's NR4
reference behaviour bit-for-bit. The MW0LGE modifications are
concentrated in `CMakeLists.txt` (FFTW3f path discovery for the Windows
build, marked `# MW0LGE (c) 2025`); the algorithmic source under `src/`
matches upstream as of the Thetis snapshot.

Upstream:
- Original library — <https://github.com/lucianodato/libspecbleach>
- Thetis-modified snapshot — <https://github.com/ramdor/Thetis>

LGPL-2.1-or-later → GPL-2.0-or-later is one-way licence-compatible, so
linking libspecbleach into Zeus's GPL-2-or-later distribution is
consistent with both the LGPL's permissive linking clause and Zeus's own
licence terms. Zeus does not modify the vendored libspecbleach source;
per-file headers in `native/libspecbleach/` are preserved as received
from upstream and must remain so on re-vendor.

libspecbleach also introduces a build-time dependency on **FFTW3f** (the
single-precision build of FFTW3) on every host that rebuilds the native
library. FFTW3f is a separately-distributed library and is not vendored
into Zeus; see `native/README.md` for the per-platform install hint.

## librnnoise (RNNoise)

Zeus's NR3 (RNNoise) signal path links against **RNNoise** (xiph), vendored
in-tree under [`native/rnnoise/`](native/rnnoise/). Like libspecbleach it is
built as a static sub-target of `libwdsp` with hidden symbol visibility, so the
RNNR exports surface from `libwdsp.{so,dll,dylib}` directly with no separate
runtime dependency.

RNNoise is **Copyright (C) Jean-Marc Valin and the Xiph.Org Foundation** and is
distributed under the **BSD 3-Clause License**. The full licence text is
preserved verbatim at [`native/rnnoise/COPYING`](native/rnnoise/COPYING);
provenance, the pinned upstream commit, and a re-vendor recipe are in
[`native/rnnoise/VENDORING.md`](native/rnnoise/VENDORING.md).

The vendored copy is the upstream xiph `main`-branch architecture (the
weights-file / DNN variant whose `rnnoise_model_from_filename` API
`native/wdsp/rnnr.c` calls). The library is built with `USE_WEIGHTS_FILE` and a
minimal `rnnoise_data.c` (the `init_rnnoise()` function only, no default weights
compiled in), so NR3 loads its model at runtime rather than baking one into
`libwdsp`. See `native/rnnoise/VENDORING.md` for details.

### Bundled default model

Zeus ships a **default RNNoise model** so NR3 works out of the box:
[`Zeus.Server.Hosting/nr3-data/rnnoise-default.bin`](Zeus.Server.Hosting/nr3-data/rnnoise-default.bin).
It is a standard xiph/rnnoise model in the DNNw weights-file format, compatible
with the vendored RNNoise architecture (43 weight arrays: `conv1`/`conv2`, three
GRU layers, `dense_out`, `vad_dense`). The model weights carry the same
**BSD-3-Clause** licence as RNNoise itself (`native/rnnoise/COPYING`). The
operator may override it at runtime by installing their own weights file via the
DSP menu (upload or URL); removing that reverts to this bundled default.

Upstream:
- <https://github.com/xiph/rnnoise> (mirror of <https://gitlab.xiph.org/xiph/rnnoise>)
- Models: <https://media.xiph.org/rnnoise/models/>

BSD-3-Clause → GPL-2.0-or-later is one-way licence-compatible, so linking
RNNoise into Zeus's GPL-2-or-later distribution is consistent with both
licences. The RNNoise `src/` is vendored unmodified except for the minimal
`rnnoise_data.c` described above; per-file headers are preserved as received
from upstream and must remain so on re-vendor.

## ft8_lib (FT8/FT4 decode + encode)

Zeus's native FT8/FT4 digital-mode core links against **ft8_lib** (Kārlis
Goba), vendored in-tree under [`native/ft8/vendor/`](native/ft8/vendor/) and
wrapped by `native/ft8/zeus_ft8.c` in the stable `zeus_ft8_*` C ABI that the
managed `Zeus.Dsp.Ft8` P/Invoke layer binds against. It builds as
`libzeus_ft8.{so,dll,dylib}` with hidden symbol visibility so only the
`zeus_ft8_*` exports surface.

ft8_lib is **Copyright (c) 2018 Kārlis Goba** and is distributed under the
**MIT License**. The full licence text is preserved verbatim at
[`native/ft8/vendor/LICENSE`](native/ft8/vendor/LICENSE).

ft8_lib is an **independent, clean-room implementation** of the FT8/FT4
protocols written from the published specification — it is **not** derived
from WSJT-X or JTDX (which are GPL Fortran/Qt applications). The protocol
constants it reproduces (the LDPC(174,91) parity matrix, the three Costas 7×7
sync arrays, the CRC-14 polynomial, and the 77-bit message packing) were
placed in the **public domain** by the protocol authors in *"The FT4 and FT8
Communication Protocols"* (Franke, Somerville, Taylor — QEX, 2020), so their
reproduction under the MIT licence is legitimate. Every conformant FT8
implementation necessarily shares these constants, because they define the
over-the-air signal.

ft8_lib bundles its own **KISS FFT** (Mark Borgerding, BSD-3-Clause, under
[`native/ft8/vendor/fft/`](native/ft8/vendor/fft/)); Zeus's FT8 path therefore
has no FFTW dependency. MIT and BSD-3-Clause are both one-way
licence-compatible with Zeus's GPL-2.0-or-later distribution. The vendored
ft8_lib `ft8/`, `common/`, and `fft/` sources are unmodified; per-file headers
are preserved as received from upstream and must remain so on re-vendor.

Upstream:
- <https://github.com/kgoba/ft8_lib>
- FT4/FT8 protocol paper — <https://wsjt.sourceforge.io/FT4_FT8_QEX.pdf>

## wsprd (WSPR encode + decode)

Zeus's native WSPR core vendors the **WSPR encoder and decoder** in-tree under
[`native/wspr/vendor/`](native/wspr/vendor/), pinned from **pavel-demin/wsprd**
(the minimal build-able extract of the WSJT-X `wsprd`) at commit
`8aa903085479910c77de95f7e7c178f66a245ed3`.

Unlike FT8 — where the clean-room MIT `ft8_lib` exists — **no permissively
licensed WSPR decoder exists anywhere**; the canonical K1JT/K9AN demodulator
(4-FSK sync search + K=32 r=1/2 convolutional FEC via a Fano/Jelinek sequential
decoder) is the only working implementation, and its algorithm was never placed
in the public domain the way the FT4/FT8 protocol was. The decoder
(`wsprd.c`, `wsprd_utils.c`, `fano.c`, `jelinek.c`, `nhash.c`, `tab.c`,
`metric_tables.c`) and the encoder (`wsprsim_utils.c`) are **Copyright
2001-2018 Joe Taylor (K1JT) and Steven Franke (K9AN)**, distributed under the
**GNU General Public License v3**.

GPL-3 is one-way licence-compatible with Zeus's GPL-2.0-or-later distribution,
so vendoring it is consistent with both licences — the combined work is governed
by GPLv3, which Zeus's "or later" permits. This is the single component in Zeus
of WSJT-X decoder lineage; it is used because a clean-room permissive WSPR
decoder does not exist and reimplementing the Fano demodulator from scratch
carries no benefit (the on-air format is standardised regardless).

The decoder bundles **pffft** (Julien Pommier's "pretty fast FFT", FFTPACK-style
permissive licence) instead of FFTW, so Zeus's WSPR path has no external FFT
dependency. The vendored source is unmodified; all Zeus-specific glue lives in
the `zeus_wspr` shim. See [`native/wspr/README.md`](native/wspr/README.md).

Upstream:
- <https://github.com/pavel-demin/wsprd>
- WSJT-X (decoder lineage) — <https://wsjt.sourceforge.io/>

## RADE V1 (Radio Autoencoder — radae_c, opus_dnn, freedv_text)

Zeus's RADE V1 (Radio Autoencoder) digital-voice mode builds a single shared
library, `libzeus_rade.{so,dll,dylib}`, from three upstream C slices. The
slices are vendored at build time into
[`native/radae/vendor/`](native/radae/vendor/) by CI and are **not** committed
to the Zeus tree (~95 MB, almost all of it compiled-in neural-network weight
tables). All three slices are taken from a single upstream composition:

| field | value |
|---|---|
| Upstream | <https://github.com/sv1eia/Thetis-RADE> |
| Pinned SHA | `f7605a46bd21275ab8b9edd00d4a1b6fae6eabe8` |

The composition itself — selecting `radae_c` as the Python-free RADE modem,
pairing it with the `opus_dnn` FARGAN vocoder, and wiring the FreeDV-GUI
`freedv_text` LDPC path for the on-air EOO callsign frame so RADE stations
interoperate — is the work of **Christos Nikolaou (SV1EIA)** in the
[`sv1eia/Thetis-RADE`](https://github.com/sv1eia/Thetis-RADE) project. Zeus
gratefully acknowledges that adaptation; the build recipe, slice layout, and
SHA pin in [`native/radae/vendor/PROVENANCE.md`](native/radae/vendor/PROVENANCE.md)
trace directly to it.

### `radae_c` — BSD-2-Clause

The pure-C, Python-free RADE modem (IQ → 36-float feature frames; encoder /
decoder weights compiled in). **Copyright © 2024 David Rowe (VK5DGR,
[drowe67](https://github.com/drowe67))** and distributed under the **BSD
2-Clause License**. Zeus uses the decode path on RX and the encode path on
TX; the source tree is vendored unmodified except for compilation flags set
in [`native/radae/CMakeLists.txt`](native/radae/CMakeLists.txt). Upstream
reference: <https://github.com/drowe67/radae>.

### `opus_dnn` — BSD-3-Clause

The Xiph Opus tree pinned at upstream `xiph/opus` commit
`940d4e5af64351ca8ba8390df3f555484c567fbb`, built with `OPUS_DEEP_PLC=ON` to
provide the FARGAN deep neural vocoder (`fargan_synthesize()` — 36-float
feature frame → 160-sample @ 16 kHz speech) that the RADE shim calls on
decode, and the LPCNet feature analyzer (`lpcnet_compute_single_frame_features()`)
used on encode. **Copyright © Xiph.Org Foundation, Jean-Marc Valin and the
Opus contributors** (with FARGAN/LPCNet contributions also under Skype/Microsoft
attribution as carried in the upstream source headers) and distributed under
the **BSD 3-Clause License**. The vendored copy preserves the upstream
per-file headers. Upstream: <https://github.com/xiph/opus>.

### `freedv_text/src/rade_text.{c,h}` — BSD

The FreeDV-GUI reliable-text codec used by Zeus for the RADE EOO callsign
frame (CRC8 + 6-bit packing + LDPC HRA_56_56 + gp_interleaver). This replaces
`radae_c`'s built-in 7-bit-MSB packing so Zeus interoperates with FreeDV-GUI
RADE stations on the air. **Copyright © Mooneer Salem** and distributed
under a permissive **BSD** licence. Upstream reference:
<https://github.com/drowe67/freedv-gui>.

### `freedv_text/codec2/*` (LDPC primitives) — LGPL-2.1-or-later

The LDPC primitives the reliable-text path links against
(`mpdecode_core.c`, `gp_interleaver.c`, `ldpc_codes.c`, `HRA_56_56.c`,
`phi0.c`) come from the **codec2** project. **Copyright © David Rowe and
the codec2 contributors** and distributed under the **GNU Lesser General
Public License, version 2.1 or (at your option) any later version**
(LGPL-2.1-or-later). Upstream: <https://github.com/drowe67/codec2>.

`libzeus_rade` is a shared library; Zeus's managed code reaches it through
P/Invoke. Dynamic linking against LGPL-2.1-or-later code from Zeus's
GPL-2.0-or-later distribution is consistent with both licences (LGPL §6's
permissive linking clause is preserved, and the combined work remains
distributable under Zeus's licence terms).

### License roll-up

| component | license | copyleft? |
|---|---|---|
| `radae_c` | BSD-2-Clause | no |
| `opus_dnn` | BSD-3-Clause | no |
| `freedv_text/src` (rade_text) | BSD | no |
| `freedv_text/codec2` (LDPC) | LGPL-2.1-or-later | weak (dynamic-link OK) |

BSD-2-Clause, BSD-3-Clause, and LGPL-2.1-or-later are all one-way
licence-compatible with Zeus's GPL-2.0-or-later distribution. The vendored
sources retain their upstream per-file headers and copyright notices as
received and must remain so on re-vendor. The `shim/` and `CMakeLists.txt`
glue under [`native/radae/`](native/radae/) is original Zeus work and is
GPL-2.0-or-later under the Zeus copyright.

## Relationship to pihpsdr

Zeus is independent of pihpsdr but **routinely consulted pihpsdr source as
the authoritative reference for Saturn-class (ANAN G2, G2 MkII, Saturn /
Saturn-XDMA) Protocol-2 behaviour**, particularly for:

- Hardware-peak values per board class (`transmitter.c`)
- Wire-format byte semantics on `CmdHighPriority` and `CmdTx` (`new_protocol.c`)
- PureSignal arm sequence and `tx_ps_reset` / `tx_ps_resume` patterns
- ALEX antenna routing for the PS feedback DDC pair
- DDC0 / DDC1 sample-pair convention into `pscc()`

pihpsdr is maintained by **Christoph Wüllen, DL1YCF** at
[github.com/dl1ycf/pihpsdr](https://github.com/dl1ycf/pihpsdr) and is
licensed GPL-2.0-or-later, compatible with Zeus.

Zeus acknowledges the following pihpsdr contributors whose work informed
Zeus's Protocol-2 / PureSignal implementation:

| Callsign |
| --- |
| DL1YCF (Christoph Wüllen) |

## Relationship to DeskHPSDR

Zeus is independent of DeskHPSDR but consulted DeskHPSDR as a
cross-reference for HPSDR client behaviour. DeskHPSDR is maintained by
**Heiko, DL1BZ** at [github.com/dl1bz/deskhpsdr](https://github.com/dl1bz/deskhpsdr)
and is licensed GPL-2.0-or-later, compatible with Zeus.

## Third-party assets and imagery

Images under `docs/pics/` are original screenshots of the Zeus user
interface, unless explicitly stated otherwise in an adjacent caption or
`NOTICE` entry. No FlexRadio, Apache Labs (ANAN), or Thetis marketing
imagery is reproduced in this repository.

## Per-file header format

Every first-party Zeus source file begins with an SPDX identifier,
the Zeus copyright line, the short GPL notice, and an acknowledgement
block that names all thirteen Thetis contributors, references pihpsdr
(DL1YCF) and DeskHPSDR (DL1BZ), and points back at this file.
See any source file for the canonical form.

## Reporting attribution concerns

If you believe Zeus has inadequately attributed your work — or carries
content that should be attributed to you or to an upstream project —
please open an issue at
<https://github.com/Kb2uka/openhpsdr-zeus/issues> or contact
the project lead directly. Zeus will treat attribution corrections as
a priority class of change.
