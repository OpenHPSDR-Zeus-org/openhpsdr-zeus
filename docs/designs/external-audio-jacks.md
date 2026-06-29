# External Audio-Jack Wiring — Re-Port Design (against new `develop`)

Re-introduces the operator-selectable TX **audio input source** feature first shipped to the old
develop in PR #639 (squash `78c3c28e`) and dropped during the base swap to Christian's fork. This is
a faithful re-port adapted to the current base, where only the sibling **PTT-jack** feature
(PR #799 / impl #797) has landed as a template.

Pull every old reference file with `git show 78c3c28e:<path>`.

## Scope

Single mutually-exclusive per-radio TX audio source:

```
enum TxAudioSource : byte { Host = 0, RadioMic = 1, RadioLineIn = 2, RadioBalancedXlr = 3 }
```

plus `MicBoost` (20 dB), `MicBias` (Orion electret bias, **default OFF**), and `LineInGain` (0..31).
`Host` = the browser/host mic pipeline that exists today. The three radio sources route the radio's
own front-end audio (UDP port 1026) into the WDSP TX chain.

**Output jacks (line-out / headphone / speaker) are NOT wire-selectable in either protocol** — they
are fixed hardware fed by the RX DAC. We expose only the `HasAudioAmplifier` presence cap, nothing to
control. "Wire all jacks" therefore means: all *input* sources selectable; outputs are presence-only.

## Per-board capability matrix (source: Thetis ramdor + BoardJacks/OldCode research)

| Board (HpsdrBoardKind / variant) | Codec | RadioMic | LineIn | XLR | MicBias |
|---|---|---|---|---|---|
| Metis 0x00 | ✅ | ✅ | ❌ | ❌ | ❌ |
| Hermes 0x01 (ANAN-10/100) | ✅ | ✅ | ❌ | ❌ | ❌ |
| HermesII 0x02 (**ANAN-10E**/100B) | ✅ | ✅ | ✅ (10E fix) | ❌ | ❌ |
| Angelia 0x04 (ANAN-100D) | ✅ | ✅ | ✅ | ❌ | ✅ |
| Orion 0x05 (ANAN-200D) | ✅ | ✅ | ✅ | ❌ | ✅ |
| OrionMkII 0x0A → G2 / G2-1K | ✅ | ✅ | ✅ | ✅ | ✅ |
| 0x0A → 7000DLE / 8000DLE | ✅ | ✅ | ✅ | ❌ | ✅ |
| 0x0A → OrionMkII (Apache orig) | ✅ | ✅ | ✅ | ❌ | ❌ |
| 0x0A → AnvelinaPro3 / RedPitaya | ✅ | ✅ | ✅ | ❌ | ❌ † |
| HermesC10 0x?? (G2E) | ✅ | ✅ | ❌ | ❌ ‡ | ❌ |
| Hermes-Lite 2 0x06 | ❌ codec | ❌ * | ❌ * | ❌ | ✅ frame |

* HL2 has no audio codec (no audio OUT). Mic-in exists on the 0x14 frame but is mi0bot-firmware
  dependent — **expose nothing for HL2 in v1 except `HermesLite2MicFrontEnd` plumbing kept inert**
  until confirmed on hardware. Default Host.
† RedPitaya / AnvelinaPro3 bias: connector is DIY — bias gated OFF until confirmed (electret bias on
  the wrong jack is a foot-gun).
‡ G2E XLR: Thetis hard-gates balanced to G2/G2-1K and excludes G2E — keep excluded until N1GP/OK1BR
  confirm (#289).

### The 10E two-gate rule (issue #667 / fix `caac2f73`) — do NOT reintroduce the bug
Visibility needs **both** gates to agree, each keyed on `ConnectedBoardKind`:
1. **Capability gate** — `BoardCapabilitiesTable` sets `HasRadioLineIn` for `HermesII`. Surfaced via
   `/api/radio/capabilities`; frontend gates the option + slider on it. Missing flag = control never
   appears.
2. **Encoder gate** — the encoder must actually emit the select bit + gain for that source on that
   board. No-op-for-all default = silent dead control.

Keep the 10E piece **HermesII-only**. KB2UKA explicitly scoped Hermes/ANAN-10/100 line-in OUT.

## Wire encoding (verbatim from `78c3c28e`, Thetis `network.c:1226-1236`)

```
LineInBit   = 0x01  // bit0 line-in select
MicBoostBit = 0x02  // bit1 mic 20dB boost
MicBiasBit  = 0x10  // bit4 Orion mic bias
XlrBit      = 0x20  // bit5 balanced/XLR (Saturn)

P2 micControl byte (TxSpecific byte 50):
  Host             -> 0                                        // literal 0, NEVER read params
  RadioMic         -> (boost?0x02) | (bias?0x10)
  RadioLineIn      -> 0x01                                     // gain rides byte 51
  RadioBalancedXlr -> 0x20 | (boost?0x02) | (bias?0x10)
P2 lineInGain byte (TxSpecific byte 51):
  RadioLineIn -> gain & 0x1F   else 0

P1 Hermes codec (0x12 frame): C2[0]=mic_boost, C2[1]=mic_linein   (default false = byte-identical)
P1 HL2 (0x14 frame, reg 0x0a): C1[4]=mic_trs, C1[5]=mic_bias, C2[4:0]=line_in_gain
  ** READ-MODIFY-WRITE preserving C2[6]=puresignal_run and the C4 PGA byte **  mic_ptt(C1[6])=0
```

## Safety invariants (enforce + verify with tests)
1. **Host purity** — every encode of `Host` returns literal `0` without reading boost/bias/gain. No
   stale-param wire leak on revert to host.
2. **Default-inert / byte-identical** — at defaults (Host, bias off) every board's wire bytes are
   unchanged. PureSignal golden suites (P1 + P2) MUST stay green.
3. **HL2 0x14 RMW** preserves `C2[6]=puresignal_run` + C4 PGA. PS-adjacent burn-zone → bench-verify.
4. **Audio is decoupled from PureSignal/K36** — audio rides byte 50/51 (P2) and 0x12/0x14 (P1);
   it NEVER touches the alex word, PS bit, or the K36 RX-aux BYPASS path.
5. **Board clamp** — persisted-but-unsupported source clamps to Host at connect (survives radio swap).
6. **mic_bias default OFF** + explicit frontend confirm.

## Implementation graft order (mirror PTT-jack #797 conventions)
1. `Zeus.Contracts/TxAudioSource.cs` (new enum, STJ by name).
2. `Zeus.Contracts/BoardCapabilities.cs` += `HasOnboardCodec`, `HermesLite2MicFrontEnd`,
   `HasRadioLineIn`, `HasBalancedXlr`, `HasMicBias`. `Dtos.cs` += `AudioFrontEndDto`,
   `AudioFrontEndSetRequest`, `StateDto.TxAudioSource` (resolved, anti-clobber).
3. `BoardCapabilitiesTable.cs` per-board flags per matrix above (HermesII gets `HasRadioLineIn`).
4. `Zeus.Server.Hosting/ExternalPortEncoder.cs` (new): `IExternalPortEncoder` +
   P1/P2/HL2 encoders + `ExternalPortEncoders.For(board,variant,protocol)` + `ExternalPortAudio`
   pure helpers with the bit math above. `InternalsVisibleTo` Hosting→Protocol1/2.
5. `Zeus.Server.Hosting/AudioSettingsStore.cs` (new): LiteDB `audio_frontend` single global row,
   `DeleteMany(_=>true)+Insert` (Id=0 bug PR #387), `PrefsDbPath.Get()` shared, clamp on `Get()`,
   `Changed` event, legacy four-bool row → Host.
6. DI in `ZeusHost.cs` next to `AudioDeviceSettingsStore`.
7. `RadioService` wiring: ctor `AudioSettingsStore?`, `PushAudioFrontEnd()`, `ClampAudioSource`,
   `ReplayAudioFrontEnd()`, `AudioFrontEndChanged` event, `StateDto.TxAudioSource` mutate.
8. P1 emission: `IProtocol1Client.SetAudioFrontEnd(...)` + `ControlFrame.cs` 0x12 / 0x14 RMW.
9. P2 emission: `Protocol2Client.ComposeCmdTxBuffer` += `micControl`,`lineInGain` →
   `p[50]`,`p[51]`; `SetAudioFrontEndBytes`; 1026 RX dispatch + `_radioMicHandler`.
10. Radio-mic RX: `RadioMicReceiver.cs` (decode UDP-1026: 4B BE seq + 64×int16 BE @48k = 132B →
    960-sample f32le blocks), `TxAudioIngest` source-tagging (`MicBlockSource` enum,
    `_activeSource`/`_accumulatorSource`, `SetActiveSource`, drop mistagged blocks), wire via
    `DspPipelineService`. **Most invasive merge** — current `OnMicPcmBytes` is untagged/recency-based;
    re-add tagging so it composes with TCI/WAV.
11. Endpoints: `GET/PUT /api/radio/audio` (`AudioFrontEndDto`); 409 if no codec & no HL2 mic FE;
    clamp gain + source before persist.
12. Frontend: `state/audio-store.ts` (Zustand, optimistic-rollback); **add an "Audio Input" card to
    the EXISTING `RadioSettingsPanel.tsx`** (do NOT duplicate the tab or PTT card);
    `board-capabilities.ts` cap fields. mic-bias behind a `window.confirm` warning.
13. Tests (re-port from `78c3c28e`): P2 `ControlFrameAudioEncoderTests`, `CmdTxAudioFrontEndTests`,
    `ExternalPortAlexGoldenTests`; P1 `ExternalPortGoldenTests`; server `ExternalPortEncoderTests`,
    `AudioEndpointTests`, `AudioSettingsStoreTests`, `RadioMicReceiverTests`, `TxAudioIngestTests`,
    `BoardCapabilitiesTableTests`; web `audio-store.test.ts`, `RadioSettingsPanel.test.tsx`.

## Verification gates
- `dotnet build Zeus.slnx` green (all platforms via CI).
- Full `dotnet test` green — **PureSignal golden suites byte-identical** is the acceptance proof.
- `npm --prefix zeus-web run build` + vitest green.
- **Bench gates before merge (draft PR):** HL2 0x14 RMW PS-safety on real HL2 + G2; live G2
  capability read (G2 was offline during design). Flag in PR; do not merge without KB2UKA sign-off.
```
