# Anvelina + ANAN 200D PA Board Detection Issue

## Problem Statement

Users with **Anvelina SDR + ANAN 200D PA** boards report:
1. Zeus auto-detects the combination as "ANAN G2 / 7000D / G1" (OrionMkII)
2. Not all filters switch correctly
3. ATT control has no effect on the PA board (unlike older ANAN models where ATT affects both SDR and PA)

## Root Cause

### Board Detection
- Anvelina SDR reports Protocol 1 board ID `0x0A` (same as OrionMkII)
- Zeus.Protocol1/Discovery/ReplyParser.cs line 127: `0x0A => HpsdrBoardKind.OrionMkII`
- This is correct per the HPSDR protocol - Anvelina uses the OrionMkII gateware

### Behavior Differences
- ANAN 200D PA is a different hardware configuration than ANAN G2
- ANAN 200D uses Angelia SDR board + separate PA board
- ANAN G2 is an integrated unit with different control paths

### Current Architecture Limitations
1. `ConnectedBoardKind` (RadioService.cs:1058-1069) is read-only from discovery
2. `PreferredRadioStore` only affects PA defaults before connection, not runtime behavior
3. No mechanism to override detection for boards with same protocol ID but different hardware

## Affected Components

### Filter Switching
- Zeus.Protocol1/ControlFrame.cs WriteConfigPayload() line 171-207
- C2 byte controls OC pins for filter boards
- Currently only has HL2 N2ADR special case (line 184-187)
- ANAN variants may need different OC pin mappings per hardware

### ATT Behavior
- Zeus.Protocol1/ControlFrame.cs WriteAttenuatorPayload() line 154-169
- C4 byte controls attenuation
- HL2: firmware gain reduction (`0x40 | (60 - Db)`)
- Standard HPSDR: hardware step attenuator (`0x20 | (Db & 0x1F)`)
- **Issue**: ANAN 200D PA board doesn't respond to the SDR's ATT control
- Unlike older ANAN-10/100 where ATT signal routes to PA board

## Solution Options

### Option 1: Manual Board Override (Preferred)
Add a mechanism to manually override detected board for runtime behavior:
- Extend PreferredRadioStore to affect ConnectedBoardKind when explicitly set
- Add UI control: "Override detected board" checkbox
- Only for experienced users who understand their hardware
- **CAUTION**: This is red-light per CLAUDE.md - architectural change

### Option 2: Board Sub-Variants
Add board sub-variants to distinguish:
- `OrionMkII_Integrated` (ANAN G2, 7000D)
- `OrionMkII_Angelia200D` (Angelia + ANAN 200D PA)
- `OrionMkII_Anvelina` (Anvelina SDR + PA)
- Store sub-variant preference, use for behavior decisions
- **CAUTION**: Changes Zeus.Contracts (red-light)

### Option 3: Hardware Detection Heuristics
- Try to detect hardware differences via protocol extensions
- Query additional telemetry to identify sub-variant
- Falls back to operator choice if ambiguous
- **CAUTION**: May not be reliable

### Option 4: Per-Feature Overrides (Minimal Change)
- Keep board detection as-is
- Add individual overrides for specific behaviors:
  - "ATT controls PA board" (bool, default per board)
  - "Filter board type" (enum: None, N2ADR, Alex, Custom)
- Stored per-radio in PaSettingsStore
- User configures once per radio
- **This is the most minimal, green-light option**

## Recommended Approach

**Option 4** with documentation:
1. Add per-radio configuration options for ATT and filter behavior
2. Document the settings in the UI with help text
3. Provide preset profiles for common combinations
4. Keep auto-detection unchanged (stays OrionMkII for protocol compatibility)

This avoids architectural changes while solving the operator's immediate problem.

## Implementation Notes

### ATT Behavior
- ANAN 200D PA: ATT register should still be sent (for SDR), but operator should understand it won't affect PA
- Could add a warning in UI: "ATT controls SDR only, not PA board"
- Or add a separate "PA attenuation" control that maps to drive level

### Filter Switching
- Need to understand what filter board is present (N2ADR, Alex, custom)
- OC pin assignments vary by hardware
- Should be user-configurable per band in PA Settings

### Testing
- Cannot test without physical hardware
- Need to document expected behavior per board
- Add unit tests for control frame encoding with different configurations

## References
- Zeus.Protocol1/Discovery/ReplyParser.cs - board ID mapping
- Zeus.Protocol1/ControlFrame.cs - ATT and filter control encoding
- Zeus.Server.Hosting/RadioService.cs - ConnectedBoardKind vs EffectiveBoardKind
- Zeus.Server.Hosting/PreferredRadioStore.cs - board preference storage
- Zeus.Server.Hosting/PaDefaults.cs - per-board PA gain tables
- docs/references/supported-settings.md - capability matrix
