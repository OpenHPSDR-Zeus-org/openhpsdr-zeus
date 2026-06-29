

# Clock Generator Synchronization

The Hermes-Lite 2.0 main board has two connectors for external clocks on the front panel, CL1 and CL2. CL1 is an input and accepts a clock from a master radio to synchronize two radios. It can also be used potentially as an alternate precision reference clock input. CL2 is an output and generates a master clock to synchronize a slave radio. CL2 can be configured from 1MHz to 200MHz and can potentially be used as the local oscillator for a transverter.

The clock generator in the Hermes-Lite 2.0 is the [5P49V5923](https://www.idt.com/us/en/document/dst/5p49v5923-datasheet). A detailed description of the device registers is found in the [programming guide](https://www.idt.com/us/en/document/mau/versaclock-5-family-register-descriptions-and-programming-guide).

## Clock Generator Register Read and Write

The gateware makes the clock generator registers accessible to software via the i2c interface at protocol address 0x3a with no response expected or 0x7a with response expected. The i2c address of the clock generator is 0xd4. To write a clock generator register, software must send `0x3c,0x06,0xea,reg,data` as the 5 bytes of command and control. 0x3c is the protocol address. 0x06 is the i2c write cookie. 0xea can be written as the concatenation of 0b1,0x6a where the single set bit 0b1 enables the i2c stop bit and the value 0x6a is the i2c address shifted right by 1 bit. The `reg` byte is the clock generator register address. The `data` byte is the value to write.

Likewise, to read a clock generator register, software must send `0x3c,0x07,0xea,reg,dont_care` as the 5 bytes of command and control. This is similar to the write command except 0x07 is the read cookie. Software will see the byte read as part of the response described on the [protocol wiki page](Protocol).

It is helpful when programming software for the Hermes-Lite 2.0 to be able to insert arbitrary command and control sequences into the stream sent to the Hermes-Lite 2.0, and to handle responses. An example of how to do this, including clock generator methods, are part of [Quisk](https://pypi.org/project/quisk/). See the download link on that page for the source code, and then the file `hermes/quisk_hardware.py`.

## Initial Configuration

When the Hermes-Lite 2.0 is powered on, a state machine in the gateware does initial programming of the clock generator. This turns on clock output 1, the one connected to the AD9866, and sets the frequency to 76.8MHz. The register write sequence is shown below.

| Register | Value | Description |
| ---------| ----- | ----------- |
| 0x17 | 0x04 | FB_intdiv[11:4] |
| 0x18 | 0x40 | FB_intdiv[3:0] |
| 0x1e | 0xe8 | RC control register |
| 0x1f | 0x80 | RC control register |
| 0x2d | 0x01 | OD1_intdiv[11:4] |
| 0x2e | 0x10 | OD1_intdiv[3:0] |
| 0x60 | 0x3b | Clock 1 output configuration |

The value FB_intdiv[11:0] is a 12-bit value which multiplies the input clock to set the VCO frequency. For the current Hermes-Lite 2.0 configuration we have a 38.4MHz precision oscillator which is multiplied by 0x044 to run the VCO at 2611.2MHz.

The VCO frequency has a hardcoded divide by 2, and then is further divided by OD1_intdiv. For the Hermes-Lite 2.0, this is 2611.2/2/0x011 which equals 76.8MHz. Although the clock generator is capable of fractional division, we are not using that to keep jitter to a minimum.

The other values for RC control register and Clock 1 output configuration complete setup for a 3.3V CMOS output use of clock 1. They were determined using the [Timing Commander Software](https://www.idt.com/us/en/products/clocks-timing/timing-commander-software-download-resource-guide). This software is a good way to understand how to set various clock generator registers.


## Set CL2 to Synchronous 76.8MHz Output

CL2 can be set to generate a 76.8MHz synchronous output for a slave radio. The general idea is to set the output divider for clock 2 (OD2_intdiv) to be the same as clock 1, reset both divider counters to align the edges, but include a skew value for clock 2 so that once the clock passes through the clock generator in the slave radio, it is still aligned with the master when both clocks arrive at the AD9866.

| Register | Value | Description |
| ---------| ----- | ----------- |
| 0x62 | 0x3b | Set clock2 to CMOS 3.3V |
| 0x3d | 0x01 | OD1_intdiv[11:4] |
| 0x3e | 0x10 | OD1_intdiv[3:0] |
| 0x31 | 0x81 | Enable divider for clock 2 |
| 0x3c | 0x00 | integer skew if required |
| 0x3f | 0x1f | fractional skew |
| 0x63 | 0x01 | Enable clock 2 output |

This configuration is very similar to the initial clock 1 configuration. It includes integer and fractional skew values. These values were determined experimentally to best align the final clocks at the two AD9866 devices. The values may be refined in the future. If the integer skew is 0x00, it need not be written.

Once clock 2 is setup and enabled, the clock generator must be reset to properly align clock 2 with clock 1. This can't be done through software as the reset process turns off the clock generator and this can't be tolerated for a long period of time. The protocol now supports this reset by writing 1 bit[0] at protocol address 0x39. Internally, this writes 0x43 and then 0x63 to clock generator register 0x76.

To turn off the CL2 clock, please program the following:

| Register | Value | Description |
| ---------| ----- | ----------- |
| 0x31 | 0x80 | Disable divider for clock 2 |
| 0x63 | 0x00 | Disable clock 2 output |


## Use CL1 at Synchronous 76.8MHz Input

CL1 can be set as the source of the clock generator so that a slave radio can synchronize with a master. The general idea is to switch the input from crystal oscillator to external, and then connect clock 1 output directly to the new input from within the clock generator. The PLL is not used in this case as there is no known way to align the output clocks from two separate clock generators. The path used through clock buffer and mux directly to OUT1 mux is seen in the functional block diagram on page 2 of the [datasheet](https://www.idt.com/us/en/document/dst/5p49v5923-datasheet). The clock buffer and muxes add a small delay. This is why there is a small fractional skew added to clock 2 of the master. It is untested how much jitter or phase noise is added by the extra buffer and muxes, but no evidence of increased noise is seen on any waterfall displays.

To switch to the CL1 input, please program:

| Register | Value | Description |
| ---------| ----- | ----------- |
| 0x17 | 0x02 | FB_intdiv[11:4] Adjust multiplication for new 76.8MHz reference |
| 0x18 | 0x20 | FB_intdiv[3:0] |
| 0x10 | 0xc0 | Enable both local oscillator and external clock inputs |
| 0x13 | 0x03 | Switch to external clock |
| 0x10 | 0x44 | Enable external clock input only plus refmode |
| 0x21 | 0x0c | Use previous channel, bypass divider |


To switch back to the local crystal oscillator, please program:

| Register | Value | Description |
| ---------| ----- | ----------- |
| 0x10 | 0xc4 | Enable both local oscillator and external clock inputs plus refmod |
| 0x21 | 0x81 | Enable divider |
| 0x13 | 0x00 | Switch to local oscillator |
| 0x10 | 0x80 | Enable local oscillator, no external clock and refmod |
| 0x17 | 0x04 | FB_intdiv[11:4] Adjust multiplication for new 38.4MHz reference |
| 0x18 | 0x40 | FB_intdiv[3:0] |

## Use CL1 with 10 MHz External Clock 

It has been shown that a 10 MHz clock from a GPS disciplined oscillator
(GPSDO) can be fed into the CL1 port and the Hermes Lite can be commanded
to use that source using the hermeslite.py script to generate the 76.8MHz
clock that it uses internally.  Care must be used to not overdrive the CL1
input to the VersaClock chip otherwise it may be damaged.  Many GPSDOs 
provide 5V sine wave output whereas HL2 requires a 3.3V square wave input; 
a 50 ohm SMA attenuator of at least 6 dB will reduce the voltage from a 5V 
source to a level that will not damage the chip.

The key to dynamically switching the clock source is to always supply a
legitimate clock (80MHz or less) to the AD9866. The clock can't stop. Even
so, sometimes the AD9866 PLL no longer locks and then no clock is sent to
the FPGA. It has been found that both the internal and external clocks
have to be enabled for a short time so that neither clock was stopped
during the switch. 

The following code can be added to hermeslite.py and invoked to dynamically 
switch the clock source from the internal crystal to the 10 MHz signal on CL1:

```python
  def enable_cl1_10mhz(self):
    """Use 10MHz CL1 as input to PLL1 and then to AD9866"""
    # Multiplying 10MHz by 288 will give us the desired 2880.0MHz VCO.
    # We then need to use the output divider (18.75 * 2) to get us down
    # to the required 76.8 MHz.

    self.write_versa5(0x10,0xc0) ## Enable xtal and clock
    self.write_versa5(0x13,0x03) ## Switch to clock
    self.write_versa5(0x10,0x40) ## Enable clock input only, won't lock to master

    # Output Divider 1
    self.write_versa5(0x2d,0x01) ## Change top divider to 0x012
    self.write_versa5(0x2e,0x20)
    self.write_versa5(0x22,0x03) ## Change fractional divider to 0x3000000
    self.write_versa5(0x23,0x00)
    self.write_versa5(0x24,0x00)
    self.write_versa5(0x25,0x00)

    # PLL multiplier
    self.write_versa5(0x19,0x00) ## Change fractional multiplier to 0x000000
    self.write_versa5(0x1A,0x00)
    self.write_versa5(0x1B,0x00)
    self.write_versa5(0x18,0x00) ## Change top multiplier to 0x120. LSB first to prevent VCO > 2900MHz
    self.write_versa5(0x17,0x12)
```

The Quisk software has a built-in frequency meter which can be used to
measure the frequency of station WWV by clicking the menu button next to
its S-meter and selecting the average time.  When doing so with the Hermes
Lite using its internal crystal, "Frequency 2" results in 10,000,001.78 Hz, 
with the 8 occasionally going up to 9 before going back.  1.78 Hz error
at 10 MHz (~0.18ppm) with the internal crystal is quite good.  When using
the GPSDO connected to CL1, the output is locked right on 10,000,000.00
Hz and doesn't deviate.

Ref: [Original thread on Google Groups[(https://groups.google.com/g/hermes-lite/c/v6EqUb4QGns/m/ott2USEeAgAJ)
Ref: [EA4GPZ on external 10 MHz reference from GPSDO](https://destevez.net/2021/11/hermes-lite-2-external-10-mhz-reference/)


# Gateware RX Synchronization

Besides synchronizing clocks, all delay elements in the gateware receive path as well as numerical oscillators must be synchronized.

**Note -** this wiki page describes how to synchronize gateware (and does not comment on SDR applications) and this feature remains experimental. The only known implemented support for this in the listed [software](https://github.com/softerhardware/Hermes-Lite2/wiki/Software) is the m5evt fork of linHPSDR SDR. It is expected that SDR developers would need to make changes to their SDR application to support this. The whole thread [here](https://groups.google.com/g/hermes-lite/c/FYS15G-tWhs) should be read to understand this further.

## HL2 Link

Two HL2s must be linked as shown below. Note that on one HL2 the red wire of the 6-pin ribbon cable is on the right and the other HL2 has it on the left. The red wire must be on opposite sides for each HL2.

One HL2 is designated as the master and must have an ethernet connection. The master HL2 sends a clock signal to the secondary HL2. This requires a 50Ohm SMA short coax connection from CL2 on the master to CL1 on the secondary HL2. The secondary HL2 may or may not be connected to ethernet.

[![hl2link](pictures/hl2link.jpg)](pictures/hl2link.jpg)

## Synchronization Process

The register values below are addresses in the Hermes-Lite 2 space as defined on the [Protocol](Protocol) wiki page. All writes are sent to the designated master unit. Only the master unit needs to be connected to the network.

| HL2 Register | Value | Description |
| ---------| ----- | ----------- |
| 0x39 | 0x0000_000b | Enable the clock output on the master unit |
| 0x39 | 0x0000_0900 | Designate and enable master unit, the slave unit should train and show connected LED status |
| 0x39 | 0x0000_0080 | Reset all filter pipelines in both units |
| 0x39 | 0x0081_0000 | Specify which receivers are locked, all frequency changes will then be synchronized |
|      |             | Send frequency updates to all receivers to force loading of NCO frequency register |
| 0x39 | 0x0000_0090 | Align all NCOs in both units |


## Command Routing

By default, all commands sent to the master are sent to the slave and executed by both units in lock step. It is possible to route one command only to the master or slave. There are three 0x7f sent before every command packet. Bits 1 and 0 of the the third 0x7f are a destination mask. Set bit 0 for the command to be sent to the master. Set bit 1 for the command to be sent to the slave. By default 0x7f sets both bits and commands are sent to both units.

