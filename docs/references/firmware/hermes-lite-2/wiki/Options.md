The Hermes-Lite 2.0 includes additonal hardware options on the main PCB. This page describes how to use some of these options.

## FPGA-Generated Power Supply Switching Clock

The N2ADR filter board picks up switching noise from the HL2 power supply via magnetic coupling of inductors in the high pass and 160M filters. You can see these spurs by viewing the bandscope view in Quisk with no antenna attached as shown below. These spurs are not or barely seen when using the HL2 standalone or with inline filters.

[![powersupplyspurs](pictures/powersupplyspurs.png)](pictures/powersupplyspurs.png)

The default internal switching frequency for the HL2 onboard regulators is around 900KHz. This can put the strong second harmonic in the 160M amateur radio band. A simple way to reduce these spurs is to not engage any of the N2ADR filters when receiving on 160M. Below is a picture of how to setup Quisk (similar for other software) to engage the 160M LPF filter during TX but not during RX. Note that on 160M the HPF that blocks AM broadcast frequencies is normally not engaged.

[![filterselection](pictures/filterselection.png)](pictures/filterselection.png)

In the picture below, you can see the difference when the 160M LPF filter is not used during RX.

[![nopowersupplyspurs](pictures/nopowersupplyspurs.png)](pictures/nopowersupplyspurs.png)


Another option is to move these spurs outside of the 160M band. The Hermes-Lite 2.0 has PCB support for the FPGA to generate the power supply switching frequency. Recent Hermes-Lite gateware (201906 and later) supports this option. The FPGA-generate frequency is at 1.059 MHz, and the strong lower frequency harmonics fall outside of any amateur radio bands. It may be that having these spurs present provides some incidental dither to the ADC for overall improvement.

Enabling this feature requires a modification to build8 and early Hermes-Lite 2.0 units. In the picture below, move the jumpers pointed to by the red arrows, J11 and J2, to the footprints pointed to by the yellow arrows, R16 and R13. If you have 10K resistors, install them at J11 and J2. The 10K resistors are not required but help ensure that by default some power supply switching is on. This modification will probably be standard in builds after build8.

[![switchfreqmod](pictures/switchfreqmod.jpg)](pictures/switchfreqmod.jpg)

Below is a picture showing the FPGA-generated switching frequency enabled and disabled. Note the change in spur frequencies. Now there is a choice if a spur ever gets in the way. 

[![switchedfreq](pictures/switchedfreq.png)](pictures/switchedfreq.png)


By default, the gateware has the FPGA-generated switching frequency on. It can be disabled by setting the "LT2208 Random" bit which is unused in the HL2. You can find the checkbox control for "LT2208 Random" in PowerSDR and other openhpsdr software configuration screens. On Quisk, there is Config-><Your Radio Name>->Hardware->Disable Power Supply Sync to turn this feature off or on.

## RF and Clock Connectors

There are various RF and clock connectors which may be installed depending on which (if any) external clock options are desired and which companion filter card is used. These are prefixed on the schematic with RF or CL. The expectation is that most builders will use edge mount SMA connectors.

[CONSMA003.062](https://octopart.com/search?q=CONSMA003.062&start=0) has a round base and may be used when a chassis end plate with a round hole.

[CON-SMA-EDGE-S](https://octopart.com/search?q=CON-SMA-EDGE-S&start=0) is a less expensive option but has a square base.

For the least expensive option, standard vertical SMA connectors, such as [these](https://www.aliexpress.com/item/100-Pcs-Gold-copper-SMA-female-jack-Panel-Mount-PCB-Solder-Connector/32607809145.html) from AliExpress, may be used in edge mount configuration. This is my preferred choice. Search AliExpress and Ebay have a wide variety of SMA connectors that can work, including round base and those designed specifically for edge mount.

Standard vertical SMA connectors may also be used in vertical orientation with some overhang by inserting 3 of the pins in the RF1,RF2,RF3,CL1 or CL2 footprints.

The edge mount footprints are also wide enough to accommodate BNC edge mount connectors like the [031-6009](https://octopart.com/search?q=031-6009&start=0). If these are used, it is expected that only RF1 and RF2 (no RF3) and only CL1 or CL2 use this part as the BNC connectors are larger and there is not enough room for the connecting BNC cables.

The optional uFL connectors on the Hermes-Lite2 are designed to use part [73412-0112](https://www.adafruit.com/product/1661). Although a surface mount part, it may be hand soldered.

## Vector Network Analyzer

If you have a Hermes-Lite 2.0, you can use it as a vector network analyzer by using a special program in the Quisk suite. You must have installed FPGA firmware version 64 or newer. Run the VNA program with **python quisk_vna.py** or use a shortcut. The VNA program will not work with SoftRock or other hardware. The **Help** button explains how to use it, and should get you started. This VNA program enables you to analyze your antennas without additional expense.

A calibration run must be taken before any data can be obtained. The calibrations request a scan of data points every 15 kilohertz from 15 kHz to 36 MHz, or about 2400 points. The calibration data are saved so that the scan frequencies can be changed without needing to perform a new calibration. For any start and end scan frequency the user chooses, these saved calibrations are used with linear interpolation. 

## Calibration

The Hermes-Lite must have the proper bias current set for the PA. This is done at the factory. If you wish to reset this, please use [Quisk](http://james.ahlstrom.name/quisk), [SparkSDR](http://www.ihopper.org/radio) or this [simple standalone program](http://james.ahlstrom.name/hl2setup/). Please see the [HL2 Quisk PA Bias](https://youtu.be/mEUiqmx37L8) video for more details.

The forward and reverse power sense uses a default profile. For best accuracy, calibrate the power meter against a known reference with repeated readings. In Quisk this is under Config->Your Radio->Hardware->Power meter calibration. A similar calibration table can be made and/or used in Spark SDR.

