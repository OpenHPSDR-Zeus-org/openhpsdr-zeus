# OpenHPSDR Zeus — The King of SDRs

![OpenHPSDR Zeus](docs/pics/zeus1.webp)

A browser-based SDR console for the **Hermes Lite 2** and other OpenHPSDR
radios. .NET 10 backend talks Protocol-1 / Protocol-2 to the radio and streams
IQ / audio / meter data to a React + WebGL frontend over WebSocket.

> Status: early but working.
>
> - **Hermes Lite 2 (Protocol-1):** RX is solid; TX is operator-verified on FM
>   and TUNE (v0.1, April 2026).
> - **ANAN G2 / G2 MkII (Protocol-2):** RX verified on OrionMkII / fw 2.7b41
>   across 80m–10m. TX wired for TUNE and MOX — on-air carrier verified clean
>   via an external KiwiSDR. PureSignal converging on G2 MkII. 160m not yet wired.
> - **ANAN-100D / Angelia (Protocol-2):** RX verified; S-ATT and PRE wired to radio.
> - Other Protocol-1 radios (older ANAN, Hermes, etc.) are not yet supported.
>
> **Important:** OpenHPSDR Zeus has only been tested on the **Hermes Lite 2**
> and the **ANAN G2 / G2 MkII** to date. If you have another OpenHPSDR board
> and can help bring it up, a PR would be lovely.

## About the name

**Zeus** — king of the gods. It doesn't really get more regal than that. The
name is also a nod to [Thetis](https://github.com/TAPR/OpenHPSDR-Thetis), the
long-running project a lot of the DSP heritage traces back to.

## What's in the box

- **WebGL panadapter + waterfall** with zoom, click-to-tune, drag-pan gestures
- **DSP panel**: NB, NR (NR1 / NR2 / NR4), ANF, SNB, NBP — NR2 (EMNR) at Thetis parity (Method, Trained, Post-Process), all driven by WDSP under the hood
- **Bands / modes / bandwidth / AGC / S-ATT step attenuator / PRE preamp / drive / mic gain**
- **TX**: PTT, TUNE, mic uplink, TX stage meters, SWR-trip and TX-timeout
  protection
- **PureSignal** (Protocol-2): four-patch convergence with AutoAttenuate loop
- **TX Audio Tools**: 10-band CFC for voice shaping
- **S-meter** (live + demo), RX meter frame streaming
- **Leaflet satellite map** with terminator and QRZ grid-square / beam heading
  — to interact with the map (pan / zoom), **press and hold the `M` key**.
  The experience isn't ideal yet and will improve over time.
- **Radio discovery** on the LAN (Protocol-1 + Protocol-2 broadcast, in parallel)

## At a glance

![OpenHPSDR Zeus on 20 m — advanced filter ribbon, QRZ-engaged great-circle map, operator pin (KB2UKA, FN30iv) and live panadapter / waterfall](docs/pics/screenshots/zeus-filter-panel-open.png)

> **The full user guide lives in the [OpenHPSDR Zeus Wiki](https://github.com/Kb2uka/openhpsdr-zeus/wiki).**
> Every panel, control, and gesture is documented there with screenshots — this
> README only covers what you need to install and run OpenHPSDR Zeus. If you have a
> question that starts with "what does that button do…", the wiki is the
> authoritative answer.

Wiki jump-off points for the most-asked things:

- [Installation](https://github.com/Kb2uka/openhpsdr-zeus/wiki/Installation) — installers, PWA install, macOS xattr step, first-run WDSP wisdom wait
- [Getting Started](https://github.com/Kb2uka/openhpsdr-zeus/wiki/Getting-Started) — first-minute walkthrough
- [Panadapter and Waterfall](https://github.com/Kb2uka/openhpsdr-zeus/wiki/Panadapter-and-Waterfall) — click-to-tune, zoom, palettes
- [Modes and Bands](https://github.com/Kb2uka/openhpsdr-zeus/wiki/Modes-and-Bands) and [Bandwidth and Filters](https://github.com/Kb2uka/openhpsdr-zeus/wiki/Bandwidth-and-Filters)
- [Frequency and VFO](https://github.com/Kb2uka/openhpsdr-zeus/wiki/Frequency-and-VFO) and [Front-End and Gain](https://github.com/Kb2uka/openhpsdr-zeus/wiki/Front-End-and-Gain)
- [DSP Noise Controls](https://github.com/Kb2uka/openhpsdr-zeus/wiki/DSP), [Meters](https://github.com/Kb2uka/openhpsdr-zeus/wiki/Meters), [TX Controls](https://github.com/Kb2uka/openhpsdr-zeus/wiki/TX-Controls), [TX Audio Tools](https://github.com/Kb2uka/openhpsdr-zeus/wiki/TX-Audio-Tools), [CW Keyer](https://github.com/Kb2uka/openhpsdr-zeus/wiki/CW-Keyer)
- [PureSignal (P2)](https://github.com/Kb2uka/openhpsdr-zeus/wiki/PureSignal-on-Protocol-2) and [PA Settings](https://github.com/Kb2uka/openhpsdr-zeus/wiki/PA-Settings)
- [QRZ and World Map](https://github.com/Kb2uka/openhpsdr-zeus/wiki/QRZ-and-World-Map), [Logbook](https://github.com/Kb2uka/openhpsdr-zeus/wiki/Logbook), [Keyboard & Mouse Shortcuts](https://github.com/Kb2uka/openhpsdr-zeus/wiki/Shortcuts)
- [Troubleshooting](https://github.com/Kb2uka/openhpsdr-zeus/wiki/Troubleshooting) — known quirks, missing native libraries
- [Developer Guide](https://github.com/Kb2uka/openhpsdr-zeus/wiki/Developer-Guide) — build from source, dev loop, project layout, tests

## Download

Grab the latest installer from the **[Releases page](https://github.com/Kb2uka/openhpsdr-zeus/releases/latest)**
— Windows x64 `.exe`, Windows on ARM (Snapdragon / Surface Pro X) `.exe`, macOS `.dmg`, Linux `.tar.gz`. Full install detail (PWA path,
macOS Gatekeeper xattr step, first-run WDSP wisdom wait) is on the wiki's
[Installation](https://github.com/Kb2uka/openhpsdr-zeus/wiki/Installation)
page.

## Building from source

See the wiki's [Developer Guide](https://github.com/Kb2uka/openhpsdr-zeus/wiki/Developer-Guide)
for prerequisites, the two-terminal dev loop, project layout, tests, and
conventions.

## Acknowledgements

OpenHPSDR Zeus stands on the shoulders of the OpenHPSDR community. Most of
what OpenHPSDR Zeus knows about Protocol-1 framing, Protocol-2 client
behaviour, WDSP init ordering, meter pipelines, and TX safety was learned by
reading the [Thetis source](https://github.com/ramdor/Thetis). OpenHPSDR Zeus
is an independent reimplementation in .NET — not a fork — but Thetis is the
authoritative reference for how an OpenHPSDR client should behave, and it
continues a GPL-governed lineage that runs from FlexRadio PowerSDR through
the OpenHPSDR (TAPR) ecosystem to Thetis itself.

OpenHPSDR Zeus gratefully acknowledges the Thetis contributors:

- **Richard Samphire** (MW0LGE)
- **Warren Pratt** (NR0V) — also author of **WDSP**, the DSP engine OpenHPSDR
  Zeus loads via P/Invoke
- **Laurence Barker** (G8NJJ)
- **Rick Koch** (N1GP)
- **Bryan Rambo** (W4WMT)
- **Chris Codella** (W2PA)
- **Doug Wigley** (W5WC)
- **Richard Allen** (W5SD)
- **Joe Torrey** (WD5Y)
- **Andrew Mansfield** (M0YGG)
- **Reid Campbell** (MI0BOT)
- **Sigi Jetzlsperger** (DH1KLM) — Red Pitaya implementation in Thetis, RX2 CAT/MIDI commands
- **FlexRadio Systems**

OpenHPSDR Zeus contributors to date: **Brian Keating (EI6LF)** — project lead,
and **Douglas J. Cerrato (KB2UKA)**.

See [`ATTRIBUTIONS.md`](ATTRIBUTIONS.md) for the full provenance statement,
per-component licensing, and the per-file header convention OpenHPSDR Zeus
uses to carry this acknowledgement through every source file.

## Contributing

Bug reports, feature suggestions, and PRs welcome. Please read
[`CONTRIBUTING.md`](CONTRIBUTING.md) before opening a PR — it covers the
branch model, what's red-light vs green-light, hot paths to leave alone, and
the commit / review conventions the project follows.

## License

OpenHPSDR Zeus is free software: you can redistribute it and/or modify it
under the terms of the **GNU General Public License v2 or (at your option)
any later version**, as published by the Free Software Foundation. See
[`LICENSE`](LICENSE) for the full text and [`ATTRIBUTIONS.md`](ATTRIBUTIONS.md)
for the full provenance statement.

This licensing aligns OpenHPSDR Zeus with its direct upstreams — Thetis
(GPL v2+) and WDSP (GPL v2+, by NR0V) — so that the derivation chain and any
linked distributions remain licence-compatible.

OpenHPSDR Zeus is distributed WITHOUT ANY WARRANTY; see the GPL for details.
