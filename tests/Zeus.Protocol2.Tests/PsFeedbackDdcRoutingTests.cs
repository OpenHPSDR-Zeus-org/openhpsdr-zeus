// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Zeus.Contracts;

namespace Zeus.Protocol2.Tests;

/// <summary>
/// Issue #960 — "PS dont work on Anan G2E, dead receive sound when on".
///
/// On the single-ADC HermesC10 / ANAN-G2E (and Hermes / HermesII), Zeus maps
/// the user's only RX to DDC0 (<see cref="Protocol2Client.RxBaseDdc"/> == 0)
/// and does NOT reserve PureSignal-feedback DDCs. The RX-thread dispatch used
/// to test <c>_psFeedbackEnabled &amp;&amp; ddcIndex == 0</c> with no board
/// term, so arming PS diverted every DDC0 user-RX packet (UDP src port 1035)
/// into the PS paired-feedback path. The WDSP RX0 audio channel lost its only
/// producer → 1.4M-sample underrun + rebuffering = dead receive audio.
///
/// The fix board-gates that dispatch (and every PS wire-compose site) on
/// <see cref="Protocol2Client.ReservesPsFeedbackDdcs"/> (derived from
/// <see cref="Protocol2Client.RxBaseDdc"/> != 0). On the dual-ADC
/// OrionMkII/Saturn/G2 family DDC0 is a genuine reserved feedback slot, so the
/// gate is a constant <c>true</c> and the dispatch is byte-/path-identical to
/// the historical behaviour — zero regression.
///
/// SCOPE — what this fix does and does NOT do (issue #960): it stops PureSignal
/// from killing RX audio on the G2E by DISABLING PS there. It does NOT make PS
/// work on the G2E. The N1GP G2E gateware genuinely supports Orion-style PS on
/// its single LTC2208 — with command byte 1363 (SyncRx[0]) = 0x02 (the exact
/// byte Zeus already writes to arm the Orion pair), Rx_fifo_ctrl0
/// (Hermes.v:717-720) muxes the TX-DAC reference (rx_I[NR]) and the ADC
/// feedback (rx_I[0]) into DDC0 as the identical interleaved pair, and the
/// firmware records "PS works" (Hermes.v:139; N1GP/Phil PS work at :174, ported
/// from Angelia P2 v11.6). Zeus disables it only because the single ADC means
/// arming that pair consumes DDC0 and the operator RX must relocate to DDC1
/// (receiver_inst1, also fed from temp_ADC) — a larger, G2E-bench-gated change.
/// So #960 closes as "PS no longer kills RX audio (PS itself disabled pending a
/// single-ADC PS implementation)", NOT "PS works on G2E".
///
/// References:
///  - deskhpsdr src/new_protocol.c:1692-1698 — only ANGELIA/ORION/ORION2/SATURN
///    apply the ddc = 2 + i offset; Hermes-class fall through to ddc = i.
///  - G2E P2 gateware Hermes_Protocol_2_C10_v11.0.5/Hermes.v:139 ("PS works"),
///    :174 (N1GP PS work), :363 (NR = 2), :717-720 (Rx_fifo_ctrl0 interleaves
///    rx_I[NR] TX-DAC ref + rx_I[0] ADC on DDC0, gated by C122_SyncRx[0][1]);
///    Rx_specific_C&amp;C.v:181 maps command byte 1363 → SyncRx[0]. i.e. the G2E
///    DOES present the Orion-style interleaved pair on DDC0 when byte 1363 is
///    set — Zeus simply does not yet route around the single-ADC RX collision.
///  - Thetis Console/console.cs groups HermesC10 with Hermes/HermesII in P2
///    channel setup.
/// </summary>
public class PsFeedbackDdcRoutingTests
{
    // ---- ReservesPsFeedbackDdcs predicate (pins the board set) ----

    [Theory]
    [InlineData(HpsdrBoardKind.Hermes)]
    [InlineData(HpsdrBoardKind.HermesII)]
    [InlineData(HpsdrBoardKind.HermesC10)]
    public void ReservesPsFeedbackDdcs_False_For_SingleAdc_HermesClass(HpsdrBoardKind board)
    {
        // Single-ADC boards put the operator's RX on DDC0 — that DDC can never
        // also be a PS feedback slot.
        Assert.False(Protocol2Client.ReservesPsFeedbackDdcs(board));
    }

    [Theory]
    [InlineData(HpsdrBoardKind.OrionMkII)]
    [InlineData(HpsdrBoardKind.Orion)]
    [InlineData(HpsdrBoardKind.Angelia)]
    [InlineData(HpsdrBoardKind.Metis)]
    [InlineData(HpsdrBoardKind.HermesLite2)]
    [InlineData(HpsdrBoardKind.Unknown)]
    public void ReservesPsFeedbackDdcs_True_For_Everything_Else(HpsdrBoardKind board)
    {
        // The dual-ADC Orion/G2 family (and the OrionMkII default for every
        // historically-shipped board) reserves DDC0/DDC1 for the feedback pair
        // and runs user RX at DDC2.
        Assert.True(Protocol2Client.ReservesPsFeedbackDdcs(board));
    }

    [Fact]
    public void ReservesPsFeedbackDdcs_Tracks_RxBaseDdc_For_Every_Board()
    {
        // Structural invariant the fix relies on: a board reserves feedback
        // DDCs iff its user-RX base DDC is not DDC0. Asserting this over the
        // whole enum keeps the predicate and the DDC-base resolver from ever
        // drifting apart if a new board kind is added.
        foreach (HpsdrBoardKind board in System.Enum.GetValues<HpsdrBoardKind>())
        {
            bool expected = Protocol2Client.RxBaseDdc(board) != 0;
            Assert.Equal(expected, Protocol2Client.ReservesPsFeedbackDdcs(board));
        }
    }

    // ---- RX-thread dispatch decision (the dead-audio regression lock) ----

    [Theory]
    [InlineData(HpsdrBoardKind.HermesC10)]
    [InlineData(HpsdrBoardKind.Hermes)]
    [InlineData(HpsdrBoardKind.HermesII)]
    public void Dispatch_SingleAdc_PsArmed_Ddc0_StaysOnUserRx(HpsdrBoardKind board)
    {
        // The headline #960 bug: PS armed (psFeedbackEnabled=true), a DDC0
        // packet (port 1035) on a single-ADC board MUST route to the user-RX
        // decoder, NOT the PS paired-feedback path. False here == RX audio lives.
        Assert.False(Protocol2Client.RoutesDdc0ToPsFeedback(
            psFeedbackEnabled: true, ddcIndex: 0, board));
    }

    [Fact]
    public void Dispatch_G2_PsArmed_Ddc0_RoutesToPsFeedback_Unchanged()
    {
        // Zero-regression lock for the bench radio: on the dual-ADC G2/OrionMkII
        // a DDC0 packet with PS armed still routes to the PS paired-feedback
        // path exactly as before — DDC0 is a genuine reserved feedback slot
        // there (user RX is at DDC2).
        Assert.True(Protocol2Client.RoutesDdc0ToPsFeedback(
            psFeedbackEnabled: true, ddcIndex: 0, HpsdrBoardKind.OrionMkII));
    }

    [Theory]
    [InlineData(HpsdrBoardKind.OrionMkII)]
    [InlineData(HpsdrBoardKind.HermesC10)]
    public void Dispatch_PsDisarmed_Ddc0_AlwaysUserRx(HpsdrBoardKind board)
    {
        // With PS not armed, DDC0 is always user RX on every board.
        Assert.False(Protocol2Client.RoutesDdc0ToPsFeedback(
            psFeedbackEnabled: false, ddcIndex: 0, board));
    }

    [Theory]
    [InlineData(HpsdrBoardKind.OrionMkII)]
    [InlineData(HpsdrBoardKind.HermesC10)]
    public void Dispatch_NonZeroDdc_NeverPsFeedback(HpsdrBoardKind board)
    {
        // Only DDC0 is ever the feedback slot; higher DDCs are always user RX,
        // regardless of board or PS state.
        Assert.False(Protocol2Client.RoutesDdc0ToPsFeedback(
            psFeedbackEnabled: true, ddcIndex: 2, board));
    }

    // ---- Compose-side byte identity after the DRY refactor ----

    [Fact]
    public void Compose_RxSpecific_OrionMkII_PsArmed_ByteIdentical_To_LegacyShape()
    {
        // The compose guard was refactored from an explicit
        // `!= Hermes && != HermesII && != HermesC10` test to call
        // ReservesPsFeedbackDdcs. For the G2 the set is identical, so the
        // PS-armed wire buffer must be byte-for-byte what PsWireFormatTests
        // already pins (DDC0|DDC2 = 0x05, sync byte 1363 = 0x02, DDC0/DDC1
        // config blocks). This locks the refactor as a no-op on the bench radio.
        var p = Protocol2Client.ComposeCmdRxBuffer(
            seq: 9, numAdc: 2, sampleRateKhz: 192, psEnabled: true,
            boardKind: HpsdrBoardKind.OrionMkII);

        Assert.Equal((byte)0x05, p[7]);     // DDC0 (0x01) | DDC2 (0x04)
        Assert.Equal((byte)0x02, p[1363]);  // DDC1 → DDC0 sync
        Assert.Equal((byte)0x00, p[17]);    // DDC0 ADC0
        Assert.Equal((byte)0x00, p[18]);    // DDC0 192 kHz BE high
        Assert.Equal((byte)0xC0, p[19]);    // DDC0 192 kHz BE low
        Assert.Equal((byte)24, p[22]);      // DDC0 24-bit
        Assert.Equal((byte)2, p[23]);       // DDC1 ADC = numAdc
        Assert.Equal((byte)24, p[28]);      // DDC1 24-bit
    }

    [Fact]
    public void Compose_RxSpecific_HermesC10_PsArmed_NoPsBlock()
    {
        // Single-ADC board: even with psEnabled the PS DDC0/DDC1 block must
        // stay clear after the DRY refactor (the radio rejects the packet
        // otherwise). Only DDC0 enable, no sync byte.
        var p = Protocol2Client.ComposeCmdRxBuffer(
            seq: 9, numAdc: 1, sampleRateKhz: 192, psEnabled: true,
            boardKind: HpsdrBoardKind.HermesC10);

        Assert.Equal((byte)0x01, p[7]);     // only DDC0 enable
        Assert.Equal((byte)0x00, p[1363]);  // no DDC1 → DDC0 sync
    }

    // ---- Wire-compose gate (the consistent-across-all-PS-sites lock) ----
    //
    // #960 audit: the first cut gated only the RxSpecific DDC-enable block and
    // the RX decode, leaving the TxSpecific PA-protection bytes and the
    // HighPriority phase-mirror / ALEX_PS / bypass coupler bits firing on the
    // G2E when PS was "armed". Every PS wire site now funnels through
    // ComposesPsFeedbackWire(psFeedbackEnabled, board); these tests pin that
    // predicate and the byte effect through the two exposed static composers so
    // a regression that re-introduces a bare `_psFeedbackEnabled` at any site
    // (or a board-set drift) is caught.

    [Theory]
    [InlineData(HpsdrBoardKind.HermesC10)]
    [InlineData(HpsdrBoardKind.Hermes)]
    [InlineData(HpsdrBoardKind.HermesII)]
    public void ComposesPsFeedbackWire_False_For_SingleAdc_EvenWhenArmed(HpsdrBoardKind board)
    {
        // Single-ADC boards never put PS bytes on any wire command, even with
        // PS feedback armed — that is what keeps DDC0 (the operator's only RX)
        // and the RX-coupler relay clear on the G2E.
        Assert.False(Protocol2Client.ComposesPsFeedbackWire(
            psFeedbackEnabled: true, board));
    }

    [Fact]
    public void ComposesPsFeedbackWire_Tracks_BareFlag_On_DualAdc()
    {
        // Zero-regression lock: on the dual-ADC G2/OrionMkII the gate equals the
        // bare _psFeedbackEnabled it replaced, so every wire site is unchanged.
        Assert.True(Protocol2Client.ComposesPsFeedbackWire(
            psFeedbackEnabled: true, HpsdrBoardKind.OrionMkII));
        Assert.False(Protocol2Client.ComposesPsFeedbackWire(
            psFeedbackEnabled: false, HpsdrBoardKind.OrionMkII));
    }

    [Fact]
    public void ComposesPsFeedbackWire_Matches_RoutesDdc0_On_Ddc0_For_Every_Board()
    {
        // The compose-side and receive-side PS decisions must agree on DDC0 for
        // every board, or the wire and the demux drift (compose feedback bytes
        // the RX path won't consume, or vice-versa).
        foreach (HpsdrBoardKind board in System.Enum.GetValues<HpsdrBoardKind>())
        {
            Assert.Equal(
                Protocol2Client.RoutesDdc0ToPsFeedback(true, 0, board),
                Protocol2Client.ComposesPsFeedbackWire(true, board));
        }
    }

    [Fact]
    public void Compose_TxSpecific_HermesC10_GatedFlag_KeepsNonPsShape()
    {
        // The SendCmdTx call site now passes ComposesPsFeedbackWire(...) as the
        // psEnabled arg. On the G2E that gate is false, so the TxSpecific
        // PA-protection bytes p[57..59] stay the historical non-PS shape
        // (all == step-att) rather than the PS shape (p[57]=0, p[58]=PA flag).
        bool gate = Protocol2Client.ComposesPsFeedbackWire(
            psFeedbackEnabled: true, HpsdrBoardKind.HermesC10);
        Assert.False(gate);

        var p = Protocol2Client.ComposeCmdTxBuffer(
            seq: 1, sampleRateKhz: 192, txStepAttnDb: 7, paEnabled: true,
            psEnabled: gate);
        Assert.Equal((byte)7, p[57]);   // non-PS: == step-att
        Assert.Equal((byte)7, p[58]);   // non-PS: == step-att (no PA-prot flag)
        Assert.Equal((byte)7, p[59]);
    }

    [Fact]
    public void Compose_TxSpecific_OrionMkII_GatedFlag_KeepsPsShape()
    {
        // On the dual-ADC bench radio the gate is true, so the PS-armed
        // PA-protection shape is byte-identical to before (p[57]=0, p[58]=31).
        bool gate = Protocol2Client.ComposesPsFeedbackWire(
            psFeedbackEnabled: true, HpsdrBoardKind.OrionMkII);
        Assert.True(gate);

        var p = Protocol2Client.ComposeCmdTxBuffer(
            seq: 1, sampleRateKhz: 192, txStepAttnDb: 7, paEnabled: true,
            psEnabled: gate);
        Assert.Equal((byte)0, p[57]);
        Assert.Equal((byte)31, p[58]);  // PA-protection for the TX-DAC reference
        Assert.Equal((byte)7, p[59]);   // dynamic step-att
    }

    [Fact]
    public void Compose_Alex1_GatedFlag_DropsPsBit_On_G2E_KeepsIt_On_G2()
    {
        // The alex1 word (HighPriority offset 1428) ORs ALEX_PS only when its
        // psEnabled arg is set. The call site now passes the board-gated flag,
        // so the PS coupler bit is present on the G2 and absent on the G2E.
        const uint AlexPsBit = 0x00040000;

        uint g2e = Protocol2Client.ComposeAlex1Word(
            rxFreqHz: 14_100_000, rx2FreqHz: 14_100_000, txLpfFreqHz: 14_100_000,
            rx2Enabled: false, moxOn: true,
            psEnabled: Protocol2Client.ComposesPsFeedbackWire(true, HpsdrBoardKind.HermesC10),
            board: HpsdrBoardKind.HermesC10);
        Assert.Equal(0u, g2e & AlexPsBit);

        uint g2 = Protocol2Client.ComposeAlex1Word(
            rxFreqHz: 14_100_000, rx2FreqHz: 14_100_000, txLpfFreqHz: 14_100_000,
            rx2Enabled: false, moxOn: true,
            psEnabled: Protocol2Client.ComposesPsFeedbackWire(true, HpsdrBoardKind.OrionMkII),
            board: HpsdrBoardKind.OrionMkII);
        Assert.Equal(AlexPsBit, g2 & AlexPsBit);
    }

    // -----------------------------------------------------------------------
    // G2E single-ADC time-multiplexed PS feedback (issue #960, the "make PS
    // actually converge on the G2E" follow-up). The dead-receive fix above
    // DISABLED PS on the single-ADC G2E. This evolves it to time-multiplex the
    // coupler+TX-DAC-reference interleave onto DDC0 during a TX burst (and only
    // then), reverting DDC0 to the user RX at rest.
    //
    // SAFETY — the on-air path is held DARK by the burn-zone interlock
    // Protocol2Client.G2ePsTimeMuxOnAir (PureSignal hard rule): with it false
    // (production default) the G2E behaviour is byte-identical to commit a7fc99b
    // (PS disabled, RX survives). It must not be flipped true until the byte-59
    // (Angelia_atten_Tx0) protective seed lands in RadioService with KB2UKA
    // sign-off AND OK1BR bench-verifies first-key-down ADC-overload (#289).
    //
    // The PURE routing/compose logic below is tested by injecting the
    // capability/txKeyed explicitly (deterministic, parallel-safe — no static
    // mutation), independent of the production interlock.
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(HpsdrBoardKind.HermesC10)]
    [InlineData(HpsdrBoardKind.OrionMkII)]
    [InlineData(HpsdrBoardKind.Hermes)]
    [InlineData(HpsdrBoardKind.HermesII)]
    public void TimeMuxesPsFeedbackOnDdc0_DarkInProduction_ForEveryBoard(HpsdrBoardKind board)
    {
        // Burn-zone interlock: until KB2UKA signs off the byte-59 safety seed and
        // the G2E is bench-verified, the on-air time-mux capability stays false
        // for EVERY board — so production G2E is byte-identical to PS-disabled.
        Assert.False(Protocol2Client.G2ePsTimeMuxOnAir);
        Assert.False(Protocol2Client.TimeMuxesPsFeedbackOnDdc0(board));
    }

    [Fact]
    public void Routes_G2E_TimeMux_FalseAtRest_TrueInBurst()
    {
        // With the time-mux capability injected (= future on-air state), DDC0 on
        // the G2E is the user RX at rest and the PS feedback interleave only
        // during a TX burst (txKeyed = MOX || TUNE). This is the core
        // "feedback only during the burst, never at rest" safety property.
        Assert.False(Protocol2Client.RoutesDdc0ToPsFeedback(
            psFeedbackEnabled: true, ddcIndex: 0, HpsdrBoardKind.HermesC10,
            txKeyed: false, timeMuxOnDdc0: true));   // at rest -> user RX
        Assert.True(Protocol2Client.RoutesDdc0ToPsFeedback(
            psFeedbackEnabled: true, ddcIndex: 0, HpsdrBoardKind.HermesC10,
            txKeyed: true, timeMuxOnDdc0: true));    // in burst -> feedback
    }

    [Fact]
    public void Routes_G2E_TimeMux_NeverEngagesWhenPsDisarmed()
    {
        // Even mid-burst with the capability on, an unarmed PS never routes DDC0
        // to feedback — PsEnabled is the sole arm gate (no auto-arm).
        Assert.False(Protocol2Client.RoutesDdc0ToPsFeedback(
            psFeedbackEnabled: false, ddcIndex: 0, HpsdrBoardKind.HermesC10,
            txKeyed: true, timeMuxOnDdc0: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Routes_DualAdc_IndependentOfTxKeyed_ByteIdentical(bool txKeyed)
    {
        // Dual-ADC G2/OrionMkII reserves DDC0 permanently (user RX at DDC2), so
        // routing is independent of txKeyed and the time-mux term — exactly the
        // historical _psFeedbackEnabled && ddc0 behaviour. Zero regression.
        Assert.True(Protocol2Client.RoutesDdc0ToPsFeedback(
            psFeedbackEnabled: true, ddcIndex: 0, HpsdrBoardKind.OrionMkII,
            txKeyed: txKeyed, timeMuxOnDdc0: false));
    }

    [Theory]
    [InlineData(HpsdrBoardKind.Hermes)]
    [InlineData(HpsdrBoardKind.HermesII)]
    public void Routes_OtherSingleAdc_NeverFeedback_EvenInBurst(HpsdrBoardKind board)
    {
        // Hermes/HermesII are excluded from the time-mux capability (no
        // gateware verification, no bench), so they never route DDC0 to feedback
        // regardless of txKeyed.
        Assert.False(Protocol2Client.RoutesDdc0ToPsFeedback(
            psFeedbackEnabled: true, ddcIndex: 0, board,
            txKeyed: true, timeMuxOnDdc0: false));
    }

    [Fact]
    public void Routes_G2E_Production_DarkEvenInBurst()
    {
        // End-to-end production wiring: feeding the GATED capability
        // (TimeMuxesPsFeedbackOnDdc0, dark) into the routing predicate keeps the
        // G2E on the user RX even mid-burst with PS armed — byte-identical to the
        // PS-disabled fix until the interlock lifts.
        Assert.False(Protocol2Client.RoutesDdc0ToPsFeedback(
            psFeedbackEnabled: true, ddcIndex: 0, HpsdrBoardKind.HermesC10,
            txKeyed: true,
            timeMuxOnDdc0: Protocol2Client.TimeMuxesPsFeedbackOnDdc0(HpsdrBoardKind.HermesC10)));
    }

    [Fact]
    public void Compose_G2E_FeedbackBurst_ByteExact()
    {
        // The G2E TX-burst feedback descriptor: DDC0 enable ONLY (no DDC1 — the
        // TX-DAC reference is the hardwired rx_I[NR] receiver synced into Rx0,
        // not a separate DDC; Hermes.v Rx0_fifo_ctrl_inst), ADC0, 192 kHz/24-bit,
        // and byte 1363 = 0x02 to arm the SyncRx[0][1] coupler/reference
        // interleave (Rx_specific_C&C.v:181, Hermes.v:717-720).
        var p = Protocol2Client.ComposeCmdRxBuffer(
            seq: 9, numAdc: 1, sampleRateKhz: 192, psEnabled: true,
            boardKind: HpsdrBoardKind.HermesC10, g2eFeedbackBurst: true);

        Assert.Equal((byte)1, p[4]);        // single ADC
        Assert.Equal((byte)0x01, p[7]);     // DDC0 enable only, NO DDC1
        Assert.Equal((byte)0x00, p[17]);    // DDC0 ADC0
        Assert.Equal((byte)0x00, p[18]);    // DDC0 192 kHz BE high
        Assert.Equal((byte)0xC0, p[19]);    // DDC0 192 kHz BE low (0x00C0 = 192)
        Assert.Equal((byte)24, p[22]);      // DDC0 24-bit
        Assert.Equal((byte)0x02, p[1363]);  // SyncRx[0][1] interleave
    }

    [Fact]
    public void Compose_G2E_AtRest_PsArmed_NoFeedbackDescriptor()
    {
        // PS armed but NOT in a burst (g2eFeedbackBurst = false): the single-ADC
        // G2E descriptor stays the plain user-RX layout — DDC0 only, no sync
        // byte. This is the "never at rest" compose-side guarantee.
        var p = Protocol2Client.ComposeCmdRxBuffer(
            seq: 9, numAdc: 1, sampleRateKhz: 192, psEnabled: true,
            boardKind: HpsdrBoardKind.HermesC10, g2eFeedbackBurst: false);

        Assert.Equal((byte)0x01, p[7]);     // only DDC0 enable (user RX)
        Assert.Equal((byte)0x00, p[1363]);  // no interleave sync
    }

    [Fact]
    public void Compose_G2E_FeedbackBurst_DoesNotPerturb_DualAdc_Default()
    {
        // Regression guard: the new g2eFeedbackBurst parameter defaults false and
        // a normal dual-ADC PS-armed compose is unchanged (DDC0|DDC2 = 0x05,
        // sync 0x02), so adding the parameter cannot drift the bench-radio wire.
        var p = Protocol2Client.ComposeCmdRxBuffer(
            seq: 9, numAdc: 2, sampleRateKhz: 192, psEnabled: true,
            boardKind: HpsdrBoardKind.OrionMkII);
        Assert.Equal((byte)0x05, p[7]);
        Assert.Equal((byte)0x02, p[1363]);
    }

    [Fact]
    public void Decode_G2E_Interleave_CouplerFirstToRx_ReferenceSecondToTx()
    {
        // The G2E gateware emits the SyncRx[0][1] interleave coupler-FIRST
        // (rx_I[0] = temp_ADC, the PA coupler) then reference-SECOND
        // (rx_I[NR] = temp_DACD, the TX-DAC reference) — Rx_fifo_ctrl.v emits the
        // Sync data before the main data, and Hermes.v:717-720 wires Sync =
        // rx_I[0], data = rx_I[NR]. That is exactly the slot order the existing
        // paired decoder expects (first 6B -> rx, second 6B -> tx), so calcc gets
        // coupler -> rx ('feedback') and reference -> tx, identical to the G2.
        var pair = new byte[12]
        {
            0x7F, 0xFF, 0xFF,   // slot 0 (coupler) I = +full
            0x40, 0x00, 0x00,   // slot 0 (coupler) Q = +~0.5
            0x80, 0x00, 0x00,   // slot 1 (reference) I = -full
            0xC0, 0x00, 0x00,   // slot 1 (reference) Q = -~0.5
        };

        var (rxI, rxQ, txI, txQ) = Protocol2Client.DecodePsPairForTest(pair);

        Assert.True(rxI > 0.99f, $"coupler I should map to rx ~+1.0, got {rxI}");
        Assert.True(rxQ > 0.49f && rxQ < 0.51f, $"coupler Q -> rx ~+0.5, got {rxQ}");
        Assert.True(txI < -0.99f, $"reference I should map to tx ~-1.0, got {txI}");
        Assert.True(txQ < -0.49f && txQ > -0.51f, $"reference Q -> tx ~-0.5, got {txQ}");
    }

    // -----------------------------------------------------------------------
    // ANAN-10E (HermesII) single-ADC time-multiplexed PS feedback.
    //
    // The 10E PS feedback is byte-for-byte identical on the wire to the G2E
    // (HermesC10) single-ADC time-mux path above — it differs only in the
    // firmware register NAME (command byte 1363 is the `Mux` register on the
    // 10E, where Mux[1] swings Rx1's input to temp_DACD) and the reference
    // source, not in any byte Zeus emits/decodes. Firmware ground truth:
    // anan-10e_100b/P2-gateware/Hermes_v10.3/Hermes.v:684-687, :1080-1093,
    // :1483 and Rx_specific_C&C.v:181 / Tx_specific_C&C.v:182-183.
    //
    // SAFETY — the on-air path is held DARK by the burn-zone interlock
    // Protocol2Client.Hermes10ePsTimeMuxOnAir (PureSignal hard rule), the
    // sibling of G2ePsTimeMuxOnAir. With it false (production default) the
    // HermesII wire/behaviour is byte-identical to PS-disabled-on-single-ADC:
    // no feedback descriptor, no byte-59 seed, ComposesPsFeedbackWire false.
    // It must not be flipped true until the byte-59 (Angelia_atten_Tx0)
    // protective seed value is signed off by KB2UKA AND a real 10E
    // bench-verifies first-key-down ADC overload.
    //
    // The flag is process-global. These tests live in THIS class (which is the
    // only one that reads the time-mux flags) so they run sequentially with the
    // dark-assertion tests above, and every mutation is restored in try/finally
    // — no other test ever observes a lifted interlock. Pure routing logic is
    // exercised by injecting timeMuxOnDdc0 explicitly (no static mutation).
    // -----------------------------------------------------------------------

    [Fact]
    public void TimeMuxesPsFeedbackOnDdc0_HermesII_TracksHermes10eFlag_AndIsDarkByDefault()
    {
        // Default (production): the 10E interlock is down, so the time-mux
        // capability is false — byte-identical to PS-disabled.
        Assert.False(Protocol2Client.Hermes10ePsTimeMuxOnAir);
        Assert.False(Protocol2Client.TimeMuxesPsFeedbackOnDdc0(HpsdrBoardKind.HermesII));

        bool savedG2e = Protocol2Client.G2ePsTimeMuxOnAir;
        bool saved10e = Protocol2Client.Hermes10ePsTimeMuxOnAir;
        try
        {
            // Lifting ONLY the 10E interlock turns the 10E capability on and
            // leaves every other board exactly where it was: the G2E still
            // tracks its OWN (still-down) flag, and Hermes / the dual-ADC
            // family stay false.
            Protocol2Client.Hermes10ePsTimeMuxOnAir = true;
            Assert.True(Protocol2Client.TimeMuxesPsFeedbackOnDdc0(HpsdrBoardKind.HermesII));
            Assert.False(Protocol2Client.TimeMuxesPsFeedbackOnDdc0(HpsdrBoardKind.HermesC10));
            Assert.False(Protocol2Client.TimeMuxesPsFeedbackOnDdc0(HpsdrBoardKind.Hermes));
            Assert.False(Protocol2Client.TimeMuxesPsFeedbackOnDdc0(HpsdrBoardKind.OrionMkII));
        }
        finally
        {
            Protocol2Client.G2ePsTimeMuxOnAir = savedG2e;
            Protocol2Client.Hermes10ePsTimeMuxOnAir = saved10e;
        }

        Assert.False(Protocol2Client.Hermes10ePsTimeMuxOnAir); // restored
    }

    [Fact]
    public void TimeMuxesPsFeedbackOnDdc0_G2E_StillTracksOwnFlag_IndependentOf10e()
    {
        // Regression lock: the G2E remains gated by G2ePsTimeMuxOnAir ALONE.
        // Flipping the 10E flag must not affect the G2E (and vice-versa).
        bool savedG2e = Protocol2Client.G2ePsTimeMuxOnAir;
        bool saved10e = Protocol2Client.Hermes10ePsTimeMuxOnAir;
        try
        {
            Protocol2Client.Hermes10ePsTimeMuxOnAir = true;
            Protocol2Client.G2ePsTimeMuxOnAir = false;
            Assert.False(Protocol2Client.TimeMuxesPsFeedbackOnDdc0(HpsdrBoardKind.HermesC10));

            Protocol2Client.G2ePsTimeMuxOnAir = true;
            Protocol2Client.Hermes10ePsTimeMuxOnAir = false;
            Assert.True(Protocol2Client.TimeMuxesPsFeedbackOnDdc0(HpsdrBoardKind.HermesC10));
            Assert.False(Protocol2Client.TimeMuxesPsFeedbackOnDdc0(HpsdrBoardKind.HermesII));
        }
        finally
        {
            Protocol2Client.G2ePsTimeMuxOnAir = savedG2e;
            Protocol2Client.Hermes10ePsTimeMuxOnAir = saved10e;
        }
    }

    [Fact]
    public void Routes_Hermes10e_TimeMux_FalseAtRest_TrueInBurst()
    {
        // Core "feedback only during the burst, never at rest" property for the
        // 10E. Injected timeMuxOnDdc0 (no static mutation) — pure routing logic.
        Assert.False(Protocol2Client.RoutesDdc0ToPsFeedback(
            psFeedbackEnabled: true, ddcIndex: 0, HpsdrBoardKind.HermesII,
            txKeyed: false, timeMuxOnDdc0: true));   // at rest -> user RX
        Assert.True(Protocol2Client.RoutesDdc0ToPsFeedback(
            psFeedbackEnabled: true, ddcIndex: 0, HpsdrBoardKind.HermesII,
            txKeyed: true, timeMuxOnDdc0: true));     // in burst -> feedback
    }

    [Fact]
    public void Routes_Hermes10e_NeverEngagesWhenPsDisarmed()
    {
        // No auto-arm: even mid-burst with the capability on, an unarmed PS
        // never routes DDC0 to feedback.
        Assert.False(Protocol2Client.RoutesDdc0ToPsFeedback(
            psFeedbackEnabled: false, ddcIndex: 0, HpsdrBoardKind.HermesII,
            txKeyed: true, timeMuxOnDdc0: true));
    }

    [Fact]
    public void Routes_Hermes10e_Production_DarkEvenInBurst()
    {
        // End-to-end production wiring: feeding the GATED capability
        // (TimeMuxesPsFeedbackOnDdc0, dark) keeps the 10E on the user RX even
        // mid-burst with PS armed — byte-identical to PS-disabled until the
        // interlock lifts.
        Assert.False(Protocol2Client.RoutesDdc0ToPsFeedback(
            psFeedbackEnabled: true, ddcIndex: 0, HpsdrBoardKind.HermesII,
            txKeyed: true,
            timeMuxOnDdc0: Protocol2Client.TimeMuxesPsFeedbackOnDdc0(HpsdrBoardKind.HermesII)));
    }

    [Fact]
    public void Compose_Hermes10e_FeedbackBurst_ByteExact()
    {
        // The 10E TX-burst feedback descriptor is byte-for-byte the G2E burst:
        // DDC0 enable ONLY (no DDC1), ADC0, 192 kHz / 24-bit, byte 1363 = 0x02
        // (Mux[1] = coupler/reference interleave). Same composer, board HermesII.
        var p = Protocol2Client.ComposeCmdRxBuffer(
            seq: 9, numAdc: 1, sampleRateKhz: 192, psEnabled: true,
            boardKind: HpsdrBoardKind.HermesII, g2eFeedbackBurst: true);

        Assert.Equal((byte)1, p[4]);        // single ADC
        Assert.Equal((byte)0x01, p[7]);     // DDC0 enable only, NO DDC1
        Assert.Equal((byte)0x00, p[17]);    // DDC0 ADC0
        Assert.Equal((byte)0x00, p[18]);    // DDC0 192 kHz BE high
        Assert.Equal((byte)0xC0, p[19]);    // DDC0 192 kHz BE low
        Assert.Equal((byte)24, p[22]);      // DDC0 24-bit
        Assert.Equal((byte)0x02, p[1363]);  // Mux[1] interleave
    }

    [Fact]
    public void Compose_Hermes10e_AtRest_PsArmed_NoFeedbackDescriptor()
    {
        // PS armed but NOT in a burst: the 10E descriptor stays plain user RX —
        // DDC0 only, no sync byte. The "never at rest" compose-side guarantee.
        var p = Protocol2Client.ComposeCmdRxBuffer(
            seq: 9, numAdc: 1, sampleRateKhz: 192, psEnabled: true,
            boardKind: HpsdrBoardKind.HermesII, g2eFeedbackBurst: false);

        Assert.Equal((byte)0x01, p[7]);     // only DDC0 enable (user RX)
        Assert.Equal((byte)0x00, p[1363]);  // no interleave sync
    }

    [Theory]
    [InlineData(HpsdrBoardKind.HermesC10)]
    [InlineData(HpsdrBoardKind.Hermes)]
    [InlineData(HpsdrBoardKind.HermesII)]
    public void ComposesPsFeedbackWire_HermesII_StaysFalse_EvenWithFlagOn(HpsdrBoardKind board)
    {
        // The 10E PS engages via byte 1363 + byte 59 + the paired decode ONLY —
        // never via the dual-ADC PS wire bytes (ALEX_PS, p[57]/p[58] PS shape).
        // ComposesPsFeedbackWire stays false for every single-ADC board even
        // with the 10E interlock lifted, so no PS-shaped bytes ever compose.
        bool saved10e = Protocol2Client.Hermes10ePsTimeMuxOnAir;
        try
        {
            Protocol2Client.Hermes10ePsTimeMuxOnAir = true;
            Assert.False(Protocol2Client.ComposesPsFeedbackWire(
                psFeedbackEnabled: true, board));
        }
        finally
        {
            Protocol2Client.Hermes10ePsTimeMuxOnAir = saved10e;
        }
    }

    // ---- byte-59 (Angelia_atten_Tx0) TX-time ADC-overload protection seed ----

    [Theory]
    [InlineData(HpsdrBoardKind.HermesII)]
    [InlineData(HpsdrBoardKind.HermesC10)]
    [InlineData(HpsdrBoardKind.Hermes)]
    [InlineData(HpsdrBoardKind.OrionMkII)]
    public void SeedsTxAdcProtection_DarkInProduction_ForEveryBoard(HpsdrBoardKind board)
    {
        // With both interlocks down (production) NO board seeds byte 59 — so the
        // CmdTx wire is byte-identical to today for every board.
        Assert.False(Protocol2Client.SeedsTxAdcProtection(board));
    }

    [Fact]
    public void SeedsTxAdcProtection_True_OnlyForHermesII_WhenFlagOn()
    {
        // With the 10E interlock lifted, ONLY the 10E seeds byte 59. The G2E
        // byte-59 seed is a SEPARATE, independently-gated pre-condition and is
        // deliberately NOT armed here, so the HermesC10 path is unchanged.
        bool saved10e = Protocol2Client.Hermes10ePsTimeMuxOnAir;
        bool savedG2e = Protocol2Client.G2ePsTimeMuxOnAir;
        try
        {
            Protocol2Client.Hermes10ePsTimeMuxOnAir = true;
            Protocol2Client.G2ePsTimeMuxOnAir = true; // even with G2E lifted too
            Assert.True(Protocol2Client.SeedsTxAdcProtection(HpsdrBoardKind.HermesII));
            Assert.False(Protocol2Client.SeedsTxAdcProtection(HpsdrBoardKind.HermesC10));
            Assert.False(Protocol2Client.SeedsTxAdcProtection(HpsdrBoardKind.Hermes));
            Assert.False(Protocol2Client.SeedsTxAdcProtection(HpsdrBoardKind.OrionMkII));
        }
        finally
        {
            Protocol2Client.Hermes10ePsTimeMuxOnAir = saved10e;
            Protocol2Client.G2ePsTimeMuxOnAir = savedG2e;
        }
    }

    [Fact]
    public void Compose_Hermes10e_TxSpecific_NonPsShape_ProtectiveByte59Reaches_Wire()
    {
        // ComposesPsFeedbackWire(HermesII) is false, so ComposeCmdTxBuffer takes
        // the non-PS branch: p[57]=p[58]=p[59]=txStepAttnDb. The seed sets
        // txStepAttnDb to the protective floor (31), so byte 59 is protective on
        // the wire — exactly what the gateware reads on FPGA_PTT (Hermes.v:1483).
        bool gate = Protocol2Client.ComposesPsFeedbackWire(
            psFeedbackEnabled: true, HpsdrBoardKind.HermesII);
        Assert.False(gate);

        var p = Protocol2Client.ComposeCmdTxBuffer(
            seq: 1, sampleRateKhz: 192,
            txStepAttnDb: Protocol2Client.PsTxAdcProtectFloorDb,
            paEnabled: true, psEnabled: gate);
        Assert.Equal(Protocol2Client.PsTxAdcProtectFloorDb, p[57]);
        Assert.Equal(Protocol2Client.PsTxAdcProtectFloorDb, p[58]);
        Assert.Equal(Protocol2Client.PsTxAdcProtectFloorDb, p[59]); // protective
        Assert.True(p[59] >= 1, "byte 59 must clear the ADC-overload floor");
    }

    [Fact]
    public void SetPsFeedbackEnabled_HermesII_FlagOff_DoesNotSeedByte59()
    {
        // Production (interlock down): arming PS on a 10E leaves the operator's
        // TX step-att untouched — byte-identical to today, no seed.
        using var p2 = new Protocol2Client(NullLogger<Protocol2Client>.Instance);
        p2.SetBoardKind(HpsdrBoardKind.HermesII);
        p2.SetTxAttenuationDb(3);
        Assert.Equal((byte)3, p2.TxStepAttnDb);

        p2.SetPsFeedbackEnabled(true);
        Assert.Equal((byte)3, p2.TxStepAttnDb); // unchanged — no seed
        p2.SetPsFeedbackEnabled(false);
        Assert.Equal((byte)3, p2.TxStepAttnDb);
    }

    [Fact]
    public void SetPsFeedbackEnabled_HermesII_FlagOn_SeedsByte59_RestoresOnDisarm()
    {
        // Interlock lifted: arming PS on a 10E seeds the byte-59 protective floor
        // (31) so the TX-DAC feedback can't slam the only RX ADC at 0 dB on
        // first key-down; disarm restores the operator's prior value verbatim.
        bool saved10e = Protocol2Client.Hermes10ePsTimeMuxOnAir;
        try
        {
            Protocol2Client.Hermes10ePsTimeMuxOnAir = true;
            using var p2 = new Protocol2Client(NullLogger<Protocol2Client>.Instance);
            p2.SetBoardKind(HpsdrBoardKind.HermesII);
            p2.SetTxAttenuationDb(3);

            p2.SetPsFeedbackEnabled(true);
            Assert.Equal(Protocol2Client.PsTxAdcProtectFloorDb, p2.TxStepAttnDb); // 31

            p2.SetPsFeedbackEnabled(false);
            Assert.Equal((byte)3, p2.TxStepAttnDb); // restored
        }
        finally
        {
            Protocol2Client.Hermes10ePsTimeMuxOnAir = saved10e;
        }
    }

    [Theory]
    [InlineData(HpsdrBoardKind.HermesC10)]
    [InlineData(HpsdrBoardKind.OrionMkII)]
    [InlineData(HpsdrBoardKind.Hermes)]
    public void SetPsFeedbackEnabled_NonHermesII_FlagOn_DoesNotSeedByte59(HpsdrBoardKind board)
    {
        // Zero-regression: with the 10E interlock lifted (AND the G2E one too),
        // arming PS on any OTHER board never touches the TX step-att — the seed
        // is HermesII-scoped. The G2E keeps its own separate, unimplemented
        // byte-59 pre-condition; the dual-ADC family is unaffected.
        bool saved10e = Protocol2Client.Hermes10ePsTimeMuxOnAir;
        bool savedG2e = Protocol2Client.G2ePsTimeMuxOnAir;
        try
        {
            Protocol2Client.Hermes10ePsTimeMuxOnAir = true;
            Protocol2Client.G2ePsTimeMuxOnAir = true;
            using var p2 = new Protocol2Client(NullLogger<Protocol2Client>.Instance);
            p2.SetBoardKind(board);
            p2.SetTxAttenuationDb(3);

            p2.SetPsFeedbackEnabled(true);
            Assert.Equal((byte)3, p2.TxStepAttnDb); // unchanged
            p2.SetPsFeedbackEnabled(false);
            Assert.Equal((byte)3, p2.TxStepAttnDb);
        }
        finally
        {
            Protocol2Client.Hermes10ePsTimeMuxOnAir = saved10e;
            Protocol2Client.G2ePsTimeMuxOnAir = savedG2e;
        }
    }
}
