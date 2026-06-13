# Square SDR 2 — architecture notes for Zeus

Square SDR 2 (Wide Servis, CZ) is an HL2-compatible 5 W SDR transceiver that
discovers as `board=HermesLite2` over HPSDR Protocol-1 discovery. It is **not**
a stock Hermes-Lite 2: it pairs a custom Altera Cyclone IV gateware (derived
from the HL2 reference) with a **separate STM32G031 peripheral microcontroller**
that owns the front-panel band-voltage / BCD / fan-control outputs.

The mirrored sources in this directory (`SQUARE_SDR_2/`) are the vendor's
publicly-published reference. Provenance, authorship and license are in
[`SOURCE.md`](./SOURCE.md). Pin/schematic information lives only in the
manufacturer's PCB files (not mirrored).

## Why this matters for Zeus

Issue #361 reports that the Band Volts toggle (PR #314) has no effect on
Square SDR 2 even though the board classifies as `HermesLite2`. The audit below
explains why and what Zeus does and doesn't have to do for amplifier
band-following to work on this board.

## Two parallel band-voltage paths on Square SDR 2

The vendor's gateware variant
`Gateware/SquareSDR2_V75_52/variants/hl2b5up_SquareSDR2/hermeslite.v`
instantiates `hermeslite_core` with **both** non-default parameters enabled:

```verilog
hermeslite_core #(
  .UART (1),
  .FAN  (1), // FAN must be 1 for PA thermal protection to work (WiSER)
) hermeslite_core_i ( ... )
```

That switches on two independent band-volts paths that share no Zeus-side
gating:

### Path A — FPGA `fan_pwm` pin, gated on C3 bit 3 (`LT2208_DITHER_ON`)

`rtl/control.v` line 584: `band_volts_enabled <= cmd_data[11]` — that is the
**same bit** Zeus drives via `EnableHl2BandVolts`
([Zeus.Protocol1/ControlFrame.cs:442](../../../../Zeus.Protocol1/ControlFrame.cs))
and that mi0bot Thetis drives via `chkHL2BandVolts → SetADCDither`. The FPGA's
fan-PWM output is then either thermal-fan PWM or the per-band voltage
(`VOLT_160M`…`VOLT_10M` localparams in `control.v` lines 666-675) depending on
that bit. This is the genuine HL2 "Band Volts" feature; Zeus already drives it
correctly.

### Path B — FPGA `io_uart_txd` → STM32 USART2, no Zeus toggle required

`rtl/control.v` lines 720-746 wire `extamp` to `cmd_data` and emit
**Elecraft-style `FA0000XXXXXXXX;` Kenwood CAT frequency strings** on
`io_uart_txd` every time the radio's TX VFO register (`cmd_addr == 6'h01`,
i.e. wire C0 = 0x02) changes. The line is then inverted (because the STM32
expects active-low when `AK4951 != 1`) and routed to the STM32G031's USART2
RX pin.

The STM32 firmware
(`Firmware/SQUARE-SDR2_01/AppSource/USART2.c` + `bandVoltage.c` +
`bcdBandCode.c`) parses the ASCII frequency, looks up the band index, and
drives **its own** band-voltage PWM (TIM2 CH4) + BCD band-code GPIO pins
on the rear-panel connector. The per-band voltages for an XPA125B-style
amplifier are baked into `bandVoltage.c`:

```c
const uint16_t ConstBandVoltage[AMA_NUM_BANDS] = {
/*160m 80m  60m  40m  30m   20m   17m   15m   12m   10m */
  230, 460, 690, 920, 1150, 1380, 1610, 1840, 2070, 2300
};
```

**No Zeus-side toggle controls this path.** As long as Zeus writes the TX
frequency register through the normal Protocol-1 register rotation, the
STM32 sees CAT updates and drives the BVO PWM + BCD outputs. This is the
"default-on" behavior @41south observed under deskHPSDR.

## What Zeus needs to do for Square SDR 2

| Symptom on Square SDR 2 | Driven by | Action in Zeus |
|---|---|---|
| FPGA fan-PWM pin band-voltage | Path A — C3 bit 3 | `EnableHl2BandVolts` toggle (already shipped in PR #314) |
| Rear-panel BVO PWM + BCD band code | Path B — STM32 via FPGA UART | **Nothing.** Already works because Zeus writes TX-freq reg every rotation |

If an operator's XPA125B is fed from the rear-panel BVO (Path B), Zeus does
not need any toggle to make the amplifier follow. The Band Volts checkbox in
Zeus controls Path A only and is independent of whether the rear-panel BVO is
behaving.

If an operator's amplifier is wired to the FPGA's `fan_pwm` pin via the HL2
case-fan connector, the Band Volts checkbox is the correct path — and Zeus's
implementation already drives the right bit.

## Open questions for hardware verification

The vendor archive does not include a Square SDR 2 schematic / PCB silkscreen,
so the following can only be confirmed on bench by an operator with the
hardware:

1. Which connector does an XPA125B normally plug into — rear-panel BVO + BCD
   (Path B), or a case-fan-style header (Path A)?
2. Is the rear-panel BVO line behaving on Colin's hardware with `extamp.v` in
   the build (it should be unconditional)?
3. Is there a Square SDR 2 firmware-config option (over the STM32's I2C1
   port; see `bandVoltage.c::bandVoltageReadSetting / WriteSetting`) that can
   override the default per-band voltages?

## See also

- Zeus issue #361 (Kb2uka/openhpsdr-zeus) — the reporting thread
- Zeus PR #314 — the original Band Volts implementation
- [`docs/references/protocol-1/hermes-lite2-protocol.md`](../../protocol-1/hermes-lite2-protocol.md)
  line 39 — the canonical bit description (`Fan or Band Volts PWM, 0=Fan / 1=Band Volts`)
- [`SOURCE.md`](./SOURCE.md) — vendor archive provenance and license
