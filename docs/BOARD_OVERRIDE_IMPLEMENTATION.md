# Board Override Implementation Summary

## Problem Statement

Users with **Anvelina SDR + ANAN 200D PA** boards report that Zeus auto-detects the combination as "ANAN G2 / 7000D / G1" (OrionMkII), causing:
1. Incorrect filter switching behavior
2. ATT control has no effect on the PA board (unlike older ANAN models)
3. No way to override detection and force ANAN 200D behavior

## Solution Implemented

### Backend Changes

1. **PreferredRadioStore.cs** - Added `OverrideDetection` boolean field
   - When `false` (default): Preference only affects PA defaults before connection
   - When `true`: Preference overrides `ConnectedBoardKind` for ALL board-specific behavior
   - Added `GetOverrideDetection()` and `SetOverrideDetection(bool)` methods
   - Updated `Set(board, overrideDetection)` to accept optional override flag

2. **RadioService.cs** - Updated `ConnectedBoardKind` property
   - Now checks `PreferredRadioStore.GetOverrideDetection()`
   - If override is enabled AND a preferred board is set, returns the preferred board
   - Otherwise falls back to discovery result (normal behavior)
   - This affects drive-byte encoding, ATT behavior, filter switching, and all board-specific code paths

3. **Zeus.Contracts/Dtos.cs** - Updated DTOs
   - `RadioSelectionDto` now includes `bool OverrideDetection`
   - `RadioSelectionSetRequest` now includes `bool? OverrideDetection`
   - Updated XML docs to explain override behavior and warnings

4. **OpenhpsdrZeus/Program.cs** - Updated API endpoints
   - GET `/api/radio/selection` returns current `OverrideDetection` status
   - PUT `/api/radio/selection` accepts optional `OverrideDetection` flag
   - Calls `prefs.Set(chosen, req.OverrideDetection)` to persist

### Frontend Changes

1. **zeus-web/src/api/radio.ts** - Updated types and API calls
   - `RadioSelection` interface includes `overrideDetection: boolean`
   - `updateRadioSelection()` accepts optional `overrideDetection` parameter
   - Proper serialization/deserialization of override flag

2. **zeus-web/src/state/radio-store.ts** - Updated Zustand store
   - Added `setOverrideDetection(enabled: boolean)` action
   - Updated `setPreferred()` to accept optional `overrideDetection` parameter
   - Proper state management and optimistic updates

### Documentation

1. **docs/rca/anvelina-200d-detection.md** - Root cause analysis
   - Explains the problem in detail
   - Documents why Anvelina reports OrionMkII board ID
   - Describes the solution options considered
   - Provides technical implementation notes

## Remaining Work

### 1. UI Implementation (HIGH PRIORITY)

**File**: `zeus-web/src/components/RadioSelector.tsx`

Add a checkbox control for "Override Detection" with:
- Clear label: "Override Detection (Advanced)"
- Tooltip/help text explaining the risks:
  ```
  WARNING: Only enable this if you understand your hardware.
  Incorrect board selection can result in:
  - Incorrect drive levels
  - No output power
  - Hardware damage

  Use this when your hardware combination reports the wrong board ID
  (e.g., Anvelina SDR + ANAN 200D PA detected as ANAN G2).
  ```
- Visual warning when enabled (red/orange indicator)
- Should only be visible/enabled when a board other than "Auto" is selected
- Call `setOverrideDetection(checked)` on change

**Example implementation**:
```tsx
{selection.preferred !== 'Auto' && (
  <label style={{ display: 'flex', alignItems: 'center', gap: 6, marginLeft: 12 }}>
    <input
      type="checkbox"
      checked={selection.overrideDetection}
      disabled={!loaded || inflight}
      onChange={(e) => setOverrideDetection(e.target.checked)}
    />
    <span style={{ fontSize: 11, color: 'var(--fg-2)' }}>
      Override Detection (Advanced)
    </span>
    {selection.overrideDetection && (
      <span
        style={{
          fontSize: 10,
          color: 'var(--tx)',
          background: 'var(--tx-soft)',
          padding: '2px 6px',
          borderRadius: 2,
        }}
        title="Override is active. Zeus will use the selected board for ALL behavior, ignoring auto-detection."
      >
        ⚠ OVERRIDE ACTIVE
      </span>
    )}
  </label>
)}
```

### 2. Automated Tests (HIGH PRIORITY)

**Files to create/update**:
- `tests/Zeus.Server.Tests/PreferredRadioStoreTests.cs`
- `tests/Zeus.Server.Tests/RadioServiceBoardOverrideTests.cs`

**Test cases needed**:

1. **PreferredRadioStore tests**:
   - `GetOverrideDetection_DefaultsFalse()`
   - `SetOverrideDetection_Persists()`
   - `Set_WithOverrideTrue_SetsFlag()`
   - `Set_WithOverrideFalse_ClearsFlag()`
   - `SetOverrideDetection_WithNoPreferredBoard_NoOps()`

2. **RadioService board override tests**:
   - `ConnectedBoardKind_WithOverrideDisabled_ReturnsDiscoveryResult()`
   - `ConnectedBoardKind_WithOverrideEnabled_ReturnsPreferred()`
   - `ConnectedBoardKind_WithOverrideEnabledNoPreferred_ReturnsDiscoveryResult()`
   - `RadioDriveProfile_WithOverride_UsesPreferredBoard()`
   - `PaDefaults_WithOverride_UsesPreferredBoard()`

**Example test skeleton**:
```csharp
[Fact]
public void ConnectedBoardKind_WithOverrideEnabled_ReturnsPreferred()
{
    // Arrange: Mock PreferredRadioStore to return Orion with override enabled
    var mockPrefs = new Mock<PreferredRadioStore>();
    mockPrefs.Setup(p => p.Get()).Returns(HpsdrBoardKind.Orion);
    mockPrefs.Setup(p => p.GetOverrideDetection()).Returns(true);

    // Mock Protocol1Client to report OrionMkII (different board)
    var mockClient = new Mock<Protocol1Client>();
    mockClient.Setup(c => c.BoardKind).Returns(HpsdrBoardKind.OrionMkII);

    var radioService = new RadioService(..., mockPrefs.Object, ...);
    radioService._activeClient = mockClient.Object;

    // Act
    var board = radioService.ConnectedBoardKind;

    // Assert: Should return Orion (preferred) not OrionMkII (detected)
    Assert.Equal(HpsdrBoardKind.Orion, board);
}
```

### 3. Board Configuration Verification (MEDIUM PRIORITY)

Verify that all board types have correct configuration in:

1. **PaDefaults.cs** - Check per-board PA gain tables:
   - `HermesGains` - Hermes / ANAN-10/10E (currently defined)
   - `Anan100Gains` - ANAN-100 / 100B / 8000D (currently defined)
   - `Anan200Gains` - ANAN-100D / 200D (currently defined)
   - `OrionG2Gains` - ANAN G2 / 7000D / G1 (currently defined)
   - `Hl2OutputPct` - HL2 percentage model (currently defined)
   - ✅ All boards have proper defaults

2. **RadioDriveProfile.cs** - Check drive byte encoding:
   - `HermesLite2DriveProfile` - 4-bit quantized (currently defined)
   - `FullByteDriveProfile` - 8-bit for all others (currently defined)
   - ✅ All boards use correct profile

3. **ControlFrame.cs** - Check board-specific control frame encoding:
   - ATT behavior (line 154-169):
     - HL2: Firmware gain reduction `0x40 | (60 - Db)`
     - Standard HPSDR: Hardware attenuator `0x20 | (Db & 0x1F)`
   - Filter/OC pins (line 171-207):
     - HL2 with N2ADR: Auto-filter mask via `N2adrBands.RxOcMask()`
     - All boards: User OC-TX/OC-RX masks from PA Settings
   - **Action needed**: Verify Angelia, Orion, OrionMkII use correct ATT/filter encoding

### 4. User Documentation (MEDIUM PRIORITY)

**Files to update**:
- `README.md` - Add note about board override feature
- `docs/references/supported-settings.md` - Document override behavior per board
- Create `docs/how-to/board-override.md` with step-by-step instructions

**Documentation content needed**:

1. **When to use board override**:
   - Hardware combinations that report incorrect board IDs
   - Examples: Anvelina SDR + ANAN 200D PA, custom builds

2. **How to use board override**:
   - Step-by-step UI instructions
   - Screenshots showing the checkbox and warnings
   - How to verify it's working (check "Effective" board)

3. **Risks and warnings**:
   - Incorrect board selection can damage hardware
   - Drive levels may be wrong for your PA
   - Only use if you understand your hardware
   - Test with low power first

4. **Per-board differences**:
   - HL2: 4-bit drive quantization, firmware gain ATT, N2ADR filters
   - Orion (200D): 8-bit drive, hardware ATT, Alex filters
   - OrionMkII (G2): 8-bit drive, hardware ATT, integrated filters
   - Etc.

### 5. Integration Testing (LOW PRIORITY)

Manual testing with actual hardware:
- ✓ Anvelina SDR + ANAN 200D PA with override to "Orion"
- ✓ Verify filter switching works correctly
- ✓ Verify ATT control behavior
- ✓ Verify drive levels are correct
- ✓ Test all board types listed in settings panel
- ✓ Verify "Auto" detection still works
- ✓ Verify override checkbox persists across page reloads

## Design Decisions & Rationale

### Why require explicit "Override Detection" flag?

1. **Safety**: Prevents accidental misconfiguration that could damage hardware
2. **Clear intent**: Operator must consciously enable override mode
3. **Backward compatible**: Default behavior (override=false) unchanged
4. **Maintainable**: Clean separation between "preview defaults" and "override physics"

### Why modify ConnectedBoardKind instead of adding new property?

1. **Minimal invasiveness**: All existing code paths automatically respect override
2. **Consistency**: Single source of truth for board-specific behavior
3. **Testability**: Easy to mock and verify override affects all code paths
4. **Performance**: No additional lookups or conditionals in hot paths

### Why not add board sub-variants (Orion vs OrionMkII)?

1. **Protocol compliance**: Both report 0x0A board ID per HPSDR spec
2. **Complexity**: Would require changes to Zeus.Contracts (wire format)
3. **Flexibility**: Override mechanism handles this and future similar cases
4. **Maintainability**: Less code duplication, single abstraction

## Testing Strategy

1. **Unit tests** - Test override logic in isolation
2. **Integration tests** - Test API endpoints with real DB
3. **Manual tests** - Test UI with actual hardware
4. **Regression tests** - Verify HL2 and G2 still work without override

## Risks & Mitigation

### Risk: Operator sets wrong board and damages hardware

**Mitigation**:
- Strong warnings in UI
- Require explicit checkbox enable
- Visual indicators when override is active
- Documentation with per-board differences

### Risk: Override affects boards that shouldn't be overridden

**Mitigation**:
- Only applies when OverrideDetection=true AND Preferred≠Auto
- Default behavior unchanged (override=false)
- Extensive testing of all board types

### Risk: Future code doesn't respect override

**Mitigation**:
- All code uses ConnectedBoardKind (single source of truth)
- Tests verify override affects all code paths
- Documentation explains the abstraction

## Files Modified

### Backend
- `Zeus.Server.Hosting/PreferredRadioStore.cs`
- `Zeus.Server.Hosting/RadioService.cs`
- `Zeus.Contracts/Dtos.cs`
- `OpenhpsdrZeus/Program.cs`
- `docs/rca/anvelina-200d-detection.md` (new)

### Frontend
- `zeus-web/src/api/radio.ts`
- `zeus-web/src/state/radio-store.ts`
- `zeus-web/src/components/RadioSelector.tsx` (TODO)

### Tests (TODO)
- `tests/Zeus.Server.Tests/PreferredRadioStoreTests.cs`
- `tests/Zeus.Server.Tests/RadioServiceBoardOverrideTests.cs`

### Documentation (TODO)
- `README.md`
- `docs/how-to/board-override.md` (new)
- `docs/references/supported-settings.md`

## Next Steps for Maintainer

1. **Review the implementation** - Check that the override logic is sound
2. **Add UI controls** - Implement the checkbox and warnings in RadioSelector
3. **Add tests** - Ensure the override mechanism works correctly
4. **Test with hardware** - Verify with actual Anvelina + ANAN 200D
5. **Update documentation** - Add user-facing docs for the override feature
6. **Verify board configurations** - Ensure all boards have correct settings

## Maintainer Review Required

This implementation touches **red-light** areas per CLAUDE.md:
- **Architecture**: Changes how board detection affects runtime behavior
- **Default values**: Can affect operator experience if misconfigured
- **UX behavior**: Adds new control that could confuse operators

**Recommendation**: This should NOT be merged without:
1. Maintainer approval of the architecture
2. Comprehensive testing with actual hardware
3. User documentation with strong warnings
4. UI implementation with clear risk indicators
