# Zeus Reference Documentation

Canonical wire-format and radio-specific docs, mirrored locally so you can read them without a network round-trip (and pinned at a known version).

For DSP questions, Thetis source is still the ground truth (there is no WDSP spec). For **protocol** questions, start here — the PDFs on this page are the authority, and Thetis is one implementation of them.

## Directory layout

```
docs/references/
├── README.md                                          (this file)
├── supported-settings.md                              (per-radio capability matrix)
├── protocol-1/                                        (legacy "USB over IP" protocol)
│   ├── USB_protocol_V1.60.doc                         ← core P1 spec (Phil Harman, VK6APH, 2019)
│   ├── Metis-How_it_works_V1.33.pdf                   ← Ethernet-wrapping layer
│   └── hermes-lite2-protocol.md                       ← HL2-specific deltas (softerhardware wiki)
├── protocol-2/                                        (newer "Ethernet protocol" a.k.a. P2)
│   ├── openHPSDR_Ethernet_Protocol_v4.4.pdf           ← latest P2 spec
│   └── New_protocol_FPGA_Block_diagrams.pdf           ← FPGA-side block diagrams
├── firmware/                                          (radio-side FPGA gateware + MCU firmware SOURCE)
│   ├── README.md                                      ← coverage map: every radio → its source dir
│   ├── <board>/                                       ← source-only gateware mirror per board, each w/ SOURCE.md
│   └── _tools/                                        ← acquire script + Quartus .qar extractor
└── radios/
    ├── anan-g2-user-manual-v1.4-2.pdf                 ← Apache Labs operator manual (G2 MkII)
    ├── Orion_MkII_P2_firmware_v1.9_release_notes.txt  ← TAPR firmware change log for G2 FPGA
    └── square-sdr-2/                                  ← HL2-derived SquareSDR2 firmware+gateware mirror
```

For **radio-side** questions ("what does this board actually put on the wire / do in
gateware?"), see [`firmware/`](firmware/) — a source-only mirror of the FPGA gateware
and MCU firmware for every P1/P2 radio Zeus supports, with a coverage map resolving
each radio to its directory. Host behaviour is still cross-checked against Thetis.

## Which protocol does which radio use?

| Radio | Protocol 1 | Protocol 2 | Notes |
|-------|:---------:|:---------:|-------|
| **Hermes Lite 2** (HL2) | ✅ | ✗ | Board ID 0x06. HL2 extends P1 with its own memory map — see `hermes-lite2-protocol.md`. |
| **Hermes / Metis / Griffin / Angelia / Orion** (original) | ✅ | ✅ | Older firmware used P1; newer firmware supports P2. |
| **ANAN G2 / G2 MkII** (Orion MkII class, 7000DLE/8000DLE) | ✅ (legacy) | ✅ | Apache Labs ships P2 firmware (`Orion_MkII_Protocol_2_vX.Y.rbf`). Zeus targets P2 for the G2. |

Zeus model enum: `HpsdrBoardKind` in `Zeus.Protocol1/Discovery/HpsdrBoardKind.cs`. Discovery byte values match the spec (HL2 = 0x06, OrionMkII = 0x0A, etc.).

## How to use these docs

1. **Hit a protocol-level bug or unknown field?** Read the spec PDF before reading Thetis. The spec is the source of truth; Thetis's variable names sometimes diverge from the spec's C0/C1/... terminology.
2. **HL2 doing something Hermes doesn't?** It probably uses an HL2-only address. Check `hermes-lite2-protocol.md` first — the extended memory map (`0x2b`, `0x39`, `0x3b`–`0x3f`) and the `RQST` (`C0[7]`) request/response scheme are HL2 extensions, not base P1.
3. **G2 MkII doing something other Orion-class radios don't?** G2 is Orion MkII class (board ID 0x0A). Check the P2 v4.4 spec first, then cross-reference firmware release notes in `radios/` and the Apache Labs user manual.
4. **Need to implement a new capability?** Add a row to `supported-settings.md` with the spec page/address that governs it, and link it from the PR.

## Updating these docs

These are mirrors. Upstream moves; check for new versions roughly yearly or when a protocol-level question has no answer here.

Upstream sources:
- **USB_protocol (P1 core):** <https://github.com/TAPR/OpenHPSDR-SVN/tree/master/Documentation>
- **Metis (P1 framing):** <https://github.com/TAPR/OpenHPSDR-Firmware/tree/master/Protocol%201/Documentation>
- **P2 specs + FPGA diagrams:** <https://github.com/TAPR/OpenHPSDR-Firmware/tree/master/Protocol%202/Documentation>
- **HL2 Protocol wiki:** <https://github.com/softerhardware/Hermes-Lite2/wiki/Protocol>  (fetch raw at `raw.githubusercontent.com/wiki/softerhardware/Hermes-Lite2/Protocol.md`)
- **Orion MkII firmware + release notes:** <https://github.com/TAPR/OpenHPSDR-Firmware/tree/master/Protocol%202/Orion_MkII%20(ANAN-7000DLE-8000DLE)>
- **Apache Labs manuals:** <https://apache-labs.com/instant-downloads.html>

## Useful community references (not mirrored, link only)

- **Wireshark dissectors** (decode pcap'd HL2/G2 traffic against the spec):
  - Protocol 1: <https://github.com/matthew-wolf-n4mtt/openhpsdr-u>
  - Protocol 2: <https://github.com/matthew-wolf-n4mtt/openhpsdr-e>
- **piHPSDR** (independent P1+P2 client, good second-source to Thetis): <https://github.com/g0orx/pihpsdr>
- **gr-hpsdr** (GNU Radio P1 module): <https://github.com/Tom-McDermott/gr-hpsdr>
- **Hermes-Lite mailing list** (Steve Haynal, N2ADR, answers protocol questions directly): <https://groups.google.com/g/hermes-lite>
- **Apache Labs community forum:** <https://community.apache-labs.com>

## Licensing

PDFs are redistributed from TAPR (TAPR Open Hardware License, permissive for non-commercial redistribution with attribution) and Apache Labs (operator manual, free download). The HL2 wiki content is published under the softerhardware project's terms. We mirror these for offline reference and pinning; upstream remains authoritative for any redistribution beyond this repo.
