# RCA — ANAN-10E detected as Hermes Lite 2 in 0.8.3 (issue #493)

**Date:** 2026-05-25  
**Reported by:** Ronnie C (RonnieC82)  
**Fixed in:** PR targeting `experimental`

---

## Symptom

ANAN-10E with `Hermes10E_v1.5.rbf` firmware discovered and connected as **Hermes Lite 2**
in Zeus 0.8.3-nightly. The PA drive model applied was the HL2 nibble/percentage model instead
of the 8-bit byte model, producing incorrect TX power levels. The Discover list already showed
"Hermes Lite 2" before Connect was clicked, confirming the mis-classification happened at
discovery parse time, not during connect.

Server log confirming the mis-classification:

```
radio.applyPsHwPeak proto=P1 board=HermesLite2 ...
pa.recompute ... profile=HermesLite2 (%-scale, 4-bit) -> byte=96
```

---

## Root Cause

### Wire byte 0x06 is ambiguous

The OpenHPSDR Protocol 1 discovery reply places the board-ID byte at offset 10.
`Zeus.Protocol1/Discovery/ReplyParser.cs` maps byte `0x06` to
`HpsdrBoardKind.HermesLite2` in `MapBoard()` -- correct per the protocol spec.

However, `Hermes10E_v1.5.rbf` (Apache Labs ANAN-10E firmware) also reports byte `0x06`.
This is a firmware quirk: the ANAN-10E uses a Hermes-II / Griffin derivative gateware that
was tagged with board-ID 0x06 in some firmware versions rather than the expected 0x02 (HermesII).

### The version-threshold guard was already present -- but incomplete

The parser already contained a discriminator:

```csharp
// ReplyParser.cs:85
var isHl2 = board == HpsdrBoardKind.HermesLite2 && codeVersion >= HermesLite2CodeVersionThreshold;
//                                                                  ^^ threshold = 40
```

Genuine HL2 units always report code versions >= 40. The ANAN-10E reports version 15
(`Hermes10E_v1.5.rbf`), so `isHl2` correctly evaluates to **false**.

The bug: although `isHl2` was false, `board` was never corrected. It remained
`HermesLite2` (from the raw `MapBoard` result) and that stale value flowed into
`DiscoveredRadio.Board`, which is the source of truth for all downstream dispatch:
PA drive profile, calibration tables, and log labels.

---

## Fix

`Zeus.Protocol1/Discovery/ReplyParser.cs` -- one guard added immediately after `isHl2`:

```csharp
if (!isHl2 && board == HpsdrBoardKind.HermesLite2)
    board = HpsdrBoardKind.HermesII;
```

If the raw wire byte was `0x06` but the code version is below the threshold (not a genuine HL2),
reclassify `board` to `HermesII` (Hermes-II / ANAN-10E / 100B family). This causes downstream
dispatch to select the correct 8-bit full-byte drive profile and the correct ANAN PA calibration table.

`RawBoardId` in `DiscoveryDetails` continues to store the raw wire byte (`0x06`) for diagnostic
purposes; the firmware string is formatted as `"1.5"` (not the HL2 `major.minor` format) because
`isHl2` remains false.

---

## Tests

`tests/Zeus.Protocol1.Tests/DiscoveryTests.cs`:

- `WireByte_0x06_BelowHl2CodeThreshold_ReclassifiesAsHermesII` -- regression test:
  version 15 + byte 0x06 -> `HermesII`, firmware string `"1.5"`, no HL2 extras.
- `Maps_Every_Recognised_WireByte_To_BoardKind` -- updated: `0x06` InlineData now uses
  code version 73 (>= 40) to continue asserting `HermesLite2` for genuine HL2 units;
  method signature gains a `codeVersion` parameter.

---

## Why the previous fix (#294) did not cover this

Issue #294 addressed the case where `RadioService.ConnectAsync` was not calling `SetBoardKind`
on the fresh `Protocol1Client`, leaving the client's `_boardKind` at its `HermesLite2`
constructor default even after a correctly-classified discovery. That fix ensured the discovered
`Board` was propagated to the client at connect time.

Issue #493 is upstream of that: the discovery parser itself was returning `HermesLite2` as
`DiscoveredRadio.Board` for the ANAN-10E firmware variant. Once the parser returns the wrong
board, there is nothing in the connect path to correct it -- the connect path trusts
`DiscoveredRadio.Board` as the authoritative classification.

---

## Timeline

| Date       | Event |
|------------|-------|
| 2026-05-24 | Ronnie reports issue #493 -- ANAN-10E shows as HL2 in 0.8.3-nightly |
| 2026-05-25 | Log analysis confirms `board=HermesLite2` in `radio.applyPsHwPeak` |
| 2026-05-25 | Ronnie reports `Hermes10E_v1.5.rbf` firmware version |
| 2026-05-25 | Manual override confirmed working; code fix implemented |

---

## Prevention

Any future firmware that reports byte `0x06` with code version < 40 will automatically be
classified as `HermesII`. The version-threshold logic was already the right design; the single
missing reassignment was the gap.

If future ANAN hardware reports a genuinely new byte (e.g. a next-generation Apache board),
add it to `MapBoard()` and `HpsdrBoardKind.cs` explicitly rather than reusing the HL2 byte.
