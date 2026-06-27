
This page captures ideas for future amateur radio SDR projects.

# Hazelnut SDR

The Hazelnut is a modular SDR. Existing inexpensive FPGA boards are used as the main decimation engine. Various ADC/DAC daughter boards or capes can be built. These daughter boards would have minimal frontend circuitry so that filyers, PAs, undersampling filters or mixers can be added by an experimenter. This approach is similar to the one used with the original [Hermes-Lite](https://github.com/softerhardware/Hermes-Lite/wiki/Gallery-of-Hermes-Lite-Builds).

## FPGA Boards

### QMTech Bajie Boards

The [QMTech Bajie Boards](https://qmtechchina.aliexpress.com/store/4486047) are low cost Zynq-based boards with a connector which can support interfacing to an ADC/DAC daughter board. They are new, have length-matched routes to the IO, well documented and include gigabit ethernet. There is the smaller XC7010 with FPGA resources similar to the current HL2 for $50. There is the larger XC7010 with 3X the FPGA resources for $90. The Xilinx FPGAs are the only inexpensive ones capable of running LVDS faster than 800Mbs, which is required by some ADC/DAC parts. There is [prior SDR work](http://pavel-demin.github.io/red-pitaya-notes/) based on Zynq boards which could be leveraged.

### EBAZ4205

Thanks to crypto mines being closed in China, there is now a glut of inexpensive [EBAZ4205 zynq boards](https://www.aliexpress.com/wholesale?catId=0&initiative_id=SB_20211121134942&origin=y&SearchText=ebaz4205), currently in the $20-$30 range. There is an interesting blog series covering this board: part [1](https://embed-me.com/ebaz4205-recycle-cheap-crypto-miner-part-1/), [2](https://embed-me.com/ebaz4205-recycle-cheap-crypto-miner-part-2/), [3](https://embed-me.com/ebaz4205-recycle-cheap-crypto-miner-part-3/) and [4](https://embed-me.com/ebaz4205-recycle-cheap-crypto-miner-part-4/). Search for EBAZ4205 on github and you'll find lots of good information including schematics. This board has 3 IO connectors with 14 IOs each. These connectors could be removed and a small SDR daughter board placed instead. This board only supports 100 Mb/s ethernet, but that is what the original Hermes-Lite had and is very usable. For example, you can still have 4-5 384kHz, 8-10 192kHz, or 10+ 96kHz receivers. It is unclear if LVDS pairs are routed on this board, so the choice of compatible ADC/DAC parts may be more liited than the Bajie boards.


### Other Boards

There may be other boards that could work, for example this [Intel Cyclone IV board](https://www.aliexpress.com/item/4001310584093.html). Many boards,like this one, do not have high-speed connectivity and ethernet or USB2.0 would have to be added. In general, the most attractive boards have plenty of multipliers, clean and fast connections to the IO poins, as well as high-speed connectivity to a host computer.


## ADC/DAC Parts

### AD9866

The [AD9866](https://www.analog.com/en/products/ad9866.html) has been at the heart of both the original Hermes-Lite, Hermes-Lite 2 and radioberry. It is a 12-bit ADC and DAC. The ADC operates up to 80Msps. The DAC runs at 160Msps. One main attraction is the integrated LNA in front of the ADC. This eliminates the expense and area of external LNAs and attenuators. Although it is only a single ADC and DAC, multiple devices can be fed a synchronous clock and used for coherent operation. Analog Devices sells this part for $22.75 in quantities of 100.

### AD9865

The [AD9865](https://www.analog.com/en/products/ad9865.html) is a switch from 12-bit to 10-bit but a reduction in price from $22.75 to $11.85 per piece when bought in quantities of 100. The extra 2 bits don't appear to make much of a difference. First, there is a lot of hype when marketing ADCs about the number of bits and dynamic range, but it is important to look at other metrics such as ENOB, or effective number of bits. Compare [figure 12](https://www.analog.com/media/en/technical-documentation/data-sheets/AD9866.pdf#page=13) for the AD9866 with [figure 12](https://www.analog.com/media/en/technical-documentation/data-sheets/AD9865.pdf#page=13) for the AD9865. Here we see that the ENOB in the LNA range we typically use (+10 to +20) bounces around in the 9.5-10 bits for the AD9866 and 9.0-9.5 bits for the AD9865. So for all practical purposes it looks like there is only about 0.5 bit advantage for the AD9866! This is 3dB or less difference in dynamic range. If you can only set your HL2 LNA to +24 dB before clipping is bad, then you will see the same at +21 dB with the AD9865. There is a lot of debate over how much LNA gain is beneficial, but most of the experts say just enough so that you have signal. That usually is in the +10dB to +15dB range. For these reasons, the AD9865 should perform just as well for most people and cost half as much. The footprint and all control is identical with the AD9866, so a PCB can be built with either AD9866 or AD9865.

### AFE7222

The [AFE7222](https://www.ti.com/product/AFE7222) contains 2 65Msps ADCs and 2 130Msps DACs. It does not contain an integrated LNA so an external gain block would have to be added, for example an [LT6402-20](https://www.analog.com/en/products/lt6402-20.html). This is a fixed gain block and typically an adjustable attenuator is also added. This part does have analog input bandwidth up to 230MHz. This makes it a better candidate than the AD9866 for undersampling. The AD9866 is just barely able to undersample for 6M. It sells for $26.995 in quantities of 100 from Texas Instruments.

### AFE580* Family

One intriguing family of parts is the AFE580* series of ultrasound AFEs from Texas Instruments. In particular, take a look at the [AFE5803](https://www.ti.com/product/AFE5803). This contains 8 14-bit 65MSPS ADCs with LNA for $65 in lots of 100 from Texas Instruments. Imagine what might be possible with 8 coherent HF receivers! In reality it would be hard to have 8 decent HF antennas and instead one might reserve 3 or 4 inputs for HF and use the others with mixers for VHF and UHF. This would be different from existing SDRs as you could concurrently receive over a wide range of frequencies, instead of the typical slice of 10-60MHz at a single point in the spectrum. You could design and optimize some ADC frontends to perform well for specific bands. For instance maybe one ADC has a frontend and antenna designed for 630M and 2200M. Another for 160M-40M. Two others for diversity on 30M-10M. One for 4M and 6M. One for 2M. And the last two for 70cm and 33cm. Monitoring of all these bands could happen concurrently.

Using this part would require a Xilinx FPGA board with length matched LVDS. At 14 bits and 61.44MHz, a LVDS pair would be operating at 860Mbs. The AFE5803 has an integrated LPF with highest cutoff of 30MHz, so no under sampling may be possible and one could only use a mixer for higher frequencies. There is a very wide selection of mixers out there such as the [lt5560](https://www.analog.com/media/en/technical-documentation/data-sheets/5560f.pdf) or the one used in the ubiquitous RTL-SDR dongle. There are also many interesting variable LOs such as the [max2870](https://www.maximintegrated.com/en/products/comms/wireless-rf/MAX2870.html). The AFE5803 has some [interesting active termination](https://www.ti.com/lit/ds/symlink/afe5803.pdf#page=31) options for the ADC inputs. One could possibly use a very minimal frontend. There are some noise trade offs with the various termination options.

There is also the less expensive, 12-bit, and more DIY solderable [AFE5801](https://www.ti.com/product/AFE5801). But this has a LPF with highest cutoff of 15MHz. The LPF doesn't appear to be bypassable although there is an undefined setting in the datasheet.


### Other ADC/DAC Parts

An interesting list of other ADC parts can be found [here](https://groups.google.com/g/hermes-lite/c/_FylYaOdo6U/m/72X037mlEQAJ).


# Hermes-Lite 2.1

The 2021 integrated circuit supply chain problems have made it difficult to source ICs, especially the FPGA, for the existing Hermes-Lite 2.0 design. Furthermore, the two largest manufacturers of FPGAs, Xilinx and Intel, are no longer prioritizing smaller, older and less expensive FPGAs as used in the Hermes-Lite 2.0. This may be an opportunity to switch FPGA technology to a company which still caters to smaller target applications like the Hermes-Lite 2.0. Such a switch should not be done just to work around supply chain issues, but should also provide some other clear gains. Some other FPGA manufacturers are [Lattice](https://www.latticesemi.com/), [Anlogic](https://github.com/AnlogicInfo), [Gowin](https://www.gowinsemi.com/en/), and [Efinix](https://www.efinixinc.com/).

In terms of price, capacity, multiplier density, compatibility, and availability, the Efinix [Trion T120](https://www.efinixinc.com/shop/t120.php) is a good candidate to replace the existing Cyclone IV used in the Hermes-Lite 2.0. The Trion T120 has roughly 4 to 5 times the FPGA resources of the current Cyclone IV. It still uses a 1.2V core supply. The price is $33 to $35 from [Digi-Key](https://www.digikey.com/en/products/detail/efinix-inc/T120F324C4/11591359), or about $10 more than the pre-crisis price of the Cyclone IV currently used. In terms of price and capacity alone, there is clear gain here. The downside is that the T120 parts are only available as ball grid arrays, with spacing of 0.4 to 0.85 mm. Early experiments of porting the fast numerically controlled oscillator in the HL2 to the Trion T120 show that a full port should be possible but will require a speed grade 4, C4 or I4, efinix part.

The Hermes-Lite 2.1 with Trion T120 would not be a complete redesign of the current PCB. The number of changes would be minimized. Here is a list of changes considered for the Hermes-Lite 2.1.

 1. Replace the current Cyclone IV with a Trion T120. The T120 footprint takes the same or less space.
 2. To facilitate the denser ball grid array, convert the current PCB from 4-layer to 6-layer. 6-layer PCBs are becoming less expensive to make. In quantities of 100 and for a 10cm by 10cm PCB, a 4-layer PCB is $1.51 and a 6-layer PCB is $2.47, or roughly $1 per unit increase.
 3. The Trion T120 has significantly more IO available than the Cyclone IV. It will be very difficult to make use of all of them. Instead, as many IO as possible will be run out to 1 or 2 high density [FPC connectors](https://www.te.com/usa-en/products/connectors/pcb-connectors/wire-to-board-connectors/ffc-fpc-ribbon-connectors/fpc-connectors.html?tab=pgp-story). These would replace the current DB1 and DB12. Hopefully there could be enough IO in these connectors to allow for a daughter board with ADC/DAC synchronized to the main AD9866.
 4. Current Hermes-Lite 2.0 units are built with a VersaClock chip with 5 clock outputs instead of the original 3 clock outputs. This is because the 5 clock output chips are more common. Add 2 [uFL connectors](https://www.adafruit.com/product/1661) to make use of these clock outputs. These clocks could be used to synchronize internal daughter boards with additional ADC/DAC.
 5. The ST1S10 switching regulators are difficult to source. Consider substitutions. For example, various [MPS](https://www.monolithicpower.com/) equivalents.
 6. The KSZ9021/KSZ9031 ethernet PHY are difficult to source. Consider substitutions. For example, B50612D, RTL8211C, GMII version (not reduced) of KSZ9021/KSZ9031.
 7. Consider replacing current ethernet jack with integrated magnetics with HR911130A.
 8. Dedicate 2 Trion T120 pins for EER use. These can be routed to the PA power regulator area on one of the new inner layers. EER would not be added to the Hermes-Lite 2, but an option to remove the regulator and add a small board for experiments would be enabled.
 9. Dedicate 2-10 Trion pins for MCU use. These can be routed to the back edge connector on one of the new inner layers. These could support a faster quad SPI interface to the MCU, or implement current ATU, FAN/Band volts and UART functionality. Two free pins for internal debug of the T120 would also be helpful.
 10. Fix issue [163](https://github.com/softerhardware/Hermes-Lite2/issues/163), delay in PA bias turn on.
 11. Fix issue [153](https://github.com/softerhardware/Hermes-Lite2/issues/153), noise in 400 to 500 MHz range.
 12. Investigate improvements to Puresignal feedback from N2ADR filter board output to HL2 board. The goal is to make more permanent use of RF3 as a Puresignal feedback source. This would also enable other applications which require similar feedback.
 13. Consider an inexpensive i2s device on the HL2 PCB if PCB area and Trion T120 pins allow.

# Hermes-Nano

The Hermes-Nano idea is a very inexpensive small board which can serve as the core ADC/DAC plus decimation/interpolation engine of SDR transceiver projects. The concept is similar to what the [Raspberry Pi Pico](https://www.raspberrypi.com/news/raspberry-pi-silicon-pico-now-on-sale/) does for the RP2040 MCU and the [Teensy 4.X](https://www.pjrc.com/store/teensy41.html) does for the NXP MIMXRT1062 MCU. A maker could buy their own RP2040 or MIMXRT1062 chips and incorporate one of these into a project, but it is much easier to incorporate a Pico or Teensy or one of the many other MCU boards. These MCU boards don't do much specific, but they are setup with libraries, examples, support and other facilities to make them easy for makers to incorporate into their own projects. The Hermes-Nano would fill a similar purpose but for projects which need an inexpensive SDR core.

## Basic Architecture

The Hermes-Nano would consist of a single AD9865 or AD9866 coupled with an Efinix [T20](https://www.digikey.com/en/products/detail/efinix-inc/T20F256C3/10654501) FPGA which costs less than $10. There would be no Versa clock generator, but instead a crystal or oscillator directly connected to the AD9865/AD9866 as seen on the radioberry and tested on some customized Hermes-Lite 2.0 units. There would be a [uFL](https://www.adafruit.com/product/1661) connector for alternate external clock input and an easy way to manually disconnect the onboard oscillator if the external clock is preferred. External power of 5V would be expected and regulators to 3.3V and 1.2V for the AD9866/AD9865 and FPGA would be on the board. There would be no RF filters on the board. Instead, the board would have castellated holes on all edges or low profile common and inexpensive board-to-board interconnect and is meant to be soldered or connected to another board. The Hermes-Nano board would be as small as possible to ease shipping and manufacturing. With an AD9865, the estimated price is in the $50-$65 range, and with an AD9866 in the $60-$75 range.

## Carrier Boards

No communication method to a host PC or microcontroller has been specified. Instead, the Hermes-Nano board will expose an ample number of pins for a communication method on the maker's larger board. Furthermore, there will be a library of gateware variants and IPs to support a number of common communication methods. To facilitate discussion, let us refer to this larger board which incorporates the Hermes-Nano, communication method plus possibly filters, amps, and other support components as the carrier board. Some possible carrier boards are:

 1. A carrier board which is a cape for a commonly available [FT2232HL development board](https://www.amazon.com/FT2232HL-Development-Board-FT2232H-Mini/dp/B082NJD1LG). This would leverage the work done for the [radioberry juice board](http://www.pa3gsb.nl/2021/05/31/radioberry-juice/). The FPGA is programmed via the FT2232HL JTAG interface. Communication to the HOST PC is via USB 2.0 at bandwidth greater than 300Mbs. The carrier board could also include footprints for simple filters so that the carrier board and this FT2232HL board could be used immediately for receive. Future carrier boards could include the FT2232HL directly on the carrier board when these FTDI parts are readily available again.
 2. A carrier board which is a cape for a Raspberry Pi. Recent developments with the [CaribouLite SDR](https://github.com/cariboulabs/cariboulite#smi-interface) include proven open source code and RTL to use the secondary memory interface (SMI) on the Raspberry Pi. More information about the SMI can be found one the CaribouLite link, but the high-level description is a bidirectional data link with a Raspberry Pi in the 500Mbps bandwidth range which requires no additional chips, just enough pins on the FPGA. This is about the same bandwidth as USB 2.0 but would be a dedicated link.
 3. A carrier board which includes a socket for a Teensy 4.0 or 4.1. This would leverage the work of the [CW Keyer](https://github.com/softerhardware/CWKeyer) project and would be progress towards a standalone transceiver built around a Teensy 4.1, which is capable of running simple standalone SDR software. Communication with the Teensy would be via quad SPI or FPGA-emulation of a common memory device which the Teensy already supports. This would provide greater than 100Mbps bandwidth.
 4. A carrier board which allows connection to an inexpensive (<$2) LAN8720 100Mbps ethernet connection. This would leverage work done for the [original Hazelnut](https://groups.google.com/g/hermes-lite/c/_xhZanzt9KE/m/SqQuimxHCAAJ) as well as the existing ethernet support in the Hermes-Lite 2.0. A more advanced carrier board could also include a RGMII ethernet PHY for gigabit support.

 Example carrier boards complete with KiCAD files for 1,2 and 4 above would be made available on github. They would also be available to order (unpopulated) from Makerfabs along with the Hermes-Nano. They would be very inexpensive 4 layer boards and easy for a maker to build up. These would be starter boards and allow for immediate basic RF use. The intent is for the community to build up more interesting and sophisticated carrier boards over time, while the Hermes-Nano is developed to support more required interfacing scenarios and better internal DSP.

## IO Pins

 A goal is to keep the number of external pins on the Hermes-Nano low but use all available space on all edges of the Hermes-Nano for pins. Basic interfacing with the Hermes-Nano would include:

  * 4 pins for JTAG programming of the FPGA.
  * 18-20 pins targetting high speed communication to a host. This is enough pins to support any of the methods described above.
  * 5-10 pins targetting carrier board use. This would include i2c master from the FPGA for filter switching, pin for RX/TX switching, pin for EER PWM generation, and other uses.
  * 2 pins IAMP RF output
  * 2 pins TXDAC RF output
  * 2 pins ADC input
  * 1 uFl alternate clock input
  * 1 uFl clock output
  * 4 or more ground pins for good grounding
  * 1 or 2 3.3V or 5V power input pins

This is a total of 38 to 46 pins. For comparison, the Raspberry Pi Pico has 43 pins plus a USB connector.

## MIPI Option

One interesting option with the Efinix FPGAs is to use a part with [hardened MIPI CSI-2 and D-PHY interfaces](https://www.efinixinc.com/products-devkits-triont20-mipi.html). This could conceivably allow much higher bandwidth (2 Gbps is practical with 2 lanes found on most RPis) with a Raspberry Pi. Now that the Raspberry Pi 4 is [Vulkan 1.1 compliant](https://www.cnx-software.com/2021/10/27/raspberry-pi-4-achieves-vulkan-1-1-conformance-gets-up-to-60-gpu-performance-boost/), it should be possible to use the GPU and [VkFFT](https://github.com/DTolm/VkFFT) to do more interesting DSP on the Raspbery Pi with this extra data. From the FPGA point of view, it is easy to pack data with some padding into a specific video format, where pixel data represents ADC samples, and send it to the RPi. It is probably a bigger job on the RPi software and driver side to use this data.

The Efinix FPGAs with hardened MIPI only appear in devices with denser pitched ball grids. This may complicate fabrication. Also, there are additional power supply and power sequencing requirements to use MIPI. Finally, extra FPC connectors must be added to connect CSI-2.

The current MIPI CSI-2 IP only speaks one way to the Raspberry Pi's camera interface. Although the Raspberry Pi does have a DSI display interface, that does not interface easily with the Efinix hardened IPs. The Efinix CSI-2 IP can be either TX or RX, so multiple Hermes-Nano boards could be daisy chained before the last jump to the Raspberry Pi.

