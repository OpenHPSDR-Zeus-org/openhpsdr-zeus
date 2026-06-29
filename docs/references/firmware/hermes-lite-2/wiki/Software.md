For basic operation, the Hermes-Lite 2.0 is compatible with all software supporting the openHPSDR protocol 1. This page lists popular software to use with the Hermes-Lite 2.0, and describes common software features to expect in each package. Some features are extensions to the basic openHPSDR protocol 1. For more details, please contact the main software developer or support page.

## [Quisk](http://james.ahlstrom.name/quisk)

 * [YouTube setup video](https://youtu.be/1pPbQplSBoo)
 * Open source
 * Runs on Windows and Linux, including Rasbperry Pi
 * Written in Python for easy extension and modification, most new HL2 features are prototyped with Quisk
 * Actively supported by N2ADR
 * Supports some unique HL2 features, such as setting a different MAC or updating the gateware

## [SparkSDR](https://www.sparksdr.com) and [Group](https://groups.google.com/forum/#!forum/sparksdr)

 * Free and easy to install
 * [Arduino UNO MIDI amnd SparkSDR](docs/Arduino_UNO_MIDI_and_SparkSDR.pdf)
 * Runs on Windows, Mac and Linux, including Raspberry Pi
 * Built for multiband skimming with multiple receivers per band
 * Low-latency CW via MIDI to the host PC
 * Actively supported by M0NNB
 * Supports some unique HL2 features such as updating the gateware

## [SDR Console](https://www.sdr-radio.com/console)

 * [Setup document](docs/HermesLite_SDRConsole_Installation.pdf)
 * Free and easy to install
 * Runs on Windows
 * A mature, professional and slick package
 * Actively supported by G4ELI
 * Supports some unique features for the HL2 such as CW sidetone via the PC audio

## [PowerSDR](https://github.com/TAPR/OpenHPSDR-PowerSDR/releases)

 * [Hermes-Lite-specific updates](https://groups.google.com/forum/#!topic/hermes-lite/dGGOPgxM0is) in progress by GI8TME/MI0BOT
 * [YouTube setup video](https://youtu.be/97UZi8qYAKM)
 * Open source
 * Runs on Windows
 * Defacto standard for openhpsdr protocol 1
 * Includes PureSignal support

## [Thetis](https://github.com/mi0bot/OpenHPSDR-Thetis/releases/)

 * Experimental support by MI0BOT. See [this thread](https://groups.google.com/g/hermes-lite/c/dGGOPgxM0is/m/3ju6toNKBwAJ) 
 * [Download](https://github.com/mi0bot/OpenHPSDR-Thetis/releases/) the Hermes-Lite 2.0 specific version of Thetis. Also follow the installation instructions given there.
 * [Hermes Lite 2 Thetis Installation and 3rd Party Apps by Rick N8SDR](docs/Hermes_Lite_2_Thetis_Installation_and_3rd_Party_Apps.pdf)
 * [Thetis set-up details](Thetis-Setup)
 * [Thetis I2C Control Panel](Thetis-I2C-Control)
 * [Running multiple instances of Thetis](https://g4zal.blogspot.com/2024/02/multiple-instances-of-thetis.html)

## [LinHPSDR](https://github.com/g0orx/linhpsdr)

 * [Hermes-Lite-specific updates](https://github.com/m5evt/linhpsdr) by M5EVT
 * [Hermes-Lite linHPSDR user guide](https://raw.githubusercontent.com/m5evt/linhpsdr/master/documentation/linhpsdr_hl2.pdf)
 * [CW and Logging Setup Document](docs/CW_and_Logging_on_linHPSDR.pdf)
 * Free and easy to install
 * Open source
 * Runs on Linux
 * Low-latency CW via MIDI to the host PC
 * Includes PureSignal support
 * Software and system configuration tips [1](https://groups.google.com/d/msg/hermes-lite/9DF7bcdvdDQ/fyeXLFYABwAJ)[2](https://groups.google.com/d/msg/hermes-lite/9DF7bcdvdDQ/FxqmRm-NAAAJ)

## [PiHPSDR](https://github.com/g0orx/pihpsdr)

 * [Hermes-Lite-specific updates](https://github.com/dl1ycf/pihpsdr) by DL1YCF
 * [DL1YCF web page](http://dl1ycf.darc.de) with additional Hermes-Lite reports
   * [Full report](docs/dl1ycf_hl2.pdf)
   * [piHPSDR + WSJTX CAT connection](https://groups.google.com/d/msg/hermes-lite/Bs0YN_5e6aw/y8gzi1JOCQAJ)
   * [How to build from source](https://groups.google.com/d/msg/hermes-lite/7u5et77cins/Vd_4EJzoAQAJ)
   * [How to build complete setup from scratch](https://groups.google.com/g/hermes-lite/c/4Bnf2p0C1S4/m/AlGdB8tiAwAJ)
   * Open source
   * Runs on Linux, targets the Raspberry Pi

 * [piHPSDR and FLDIGI setup on Raspberry Pi with PulseAudio](https://github.com/n7ihq/piHPSDR) by N7IHQ

## [hl2_tcp](https://github.com/hotpaw2/hl2_tcp)

 * Server for [rtl_tcp](http://www.hotpaw.com/rhn/hotpaw/), an iOS SDR app by N6YWU

## [openwebrx](https://www.openwebrx.de)

 * [Hermes-Lite support](https://github.com/jketterl/openwebrx/wiki/HPSDR-%28including-Hermes-Lite-2%29-device-notes) by N1ADJ[1](https://groups.google.com/g/hermes-lite/c/crdGo9YIK9U/m/bOG5l7thAgAJ)

## [GNURadio](https://www.gnuradio.org)

 * [Hermes-Lite module](https://github.com/daniestevez/gr-hermeslite2) by EA4GPZ
 * Open source
 * Popular package for experimentation and DIY setups

## [CW Skimmer](http://www.dxatlas.com/cwskimmer/)

 * [M1GEO's blog entry and instructions](http://www.george-smart.co.uk/2020/12/using-cw-skimmer-with-hermes-lite-2-sdr/)
 * [Latest Hermes-Lite DLL](https://github.com/KV4TT/HermesIntf/releases/tag/V2.0)
 * [Group thread with more information](https://groups.google.com/g/hermes-lite/c/dR17sr206_0/m/ypcrOAeECAAJ)

## [Hermes-Lite Python Module](https://github.com/softerhardware/Hermes-Lite2/tree/master/software/hermeslite) 
 * Should only be used by those comfortable with software development and command line tools
 * Implements the discovery protocol used by Hermes-Lite
 * Has methods for getting and setting various properties, updating gateware, locking GPSDO, etc.


# Core Software Support

This section lists and describes core Hermes-Lite 2.0 features supported by software. Although the specifics may change from package to package, you should expect support for these features.

## Low Noise Amplifier for Receive

The AD9866 integrated circuit used by the Hermes-Lite 2.0 includes a built-in low noise amplifier in front of the analog to digital converter. This can be adjusted from -12dB to +48dB, although some software may only support a range of 0 to +20dB. It is essential to have some gain set otherwise the HL2 will not be sensitive enough. Gain settings of 10dB to 20dB are typical. If the gain is too high, then you risk overloading the ADC (clipping) as well as adding distortion. IN3OTD has made some [interesting studies](https://github.com/softerhardware/Hermes-Lite2/wiki/Performance-Tests) of the LNA.

## Gain for Transmit

The AD9866 also provides hardware gain of 0 to 7.5 dB for the transmit signal. Software will provide a means to change this. Note that this gain is off the maximum power output. For example, 5W is 37dBm. The hardware gain control allows us to lower this to 37-7.5=29.5dBm or just under 1W. The hardware gain adjustment is not enough to reduce the power output to 0. Software may extend the range by varying the amplitude of the signal sent to the DAC, but that is at the expense of fewer bits and lower signal resolution.

Many software packages also provide a maximum power output limit per band. This is to better interface to a PA. Often the default settings for this do not work well with the Hermes-Lite 2.0 and they must be increased.

## Filter Select

The Hermes-Lite uses the J17 filter select method. This is essentially 7 bits, where 1 bit is on per band or pair of bands. For example, the N2ADR filter board splits these bits into 7 groups for 160M, 80M, 60/40M, 30/20M, 17/15M, 12/10M and AM blocking HPF. Software will provide a mechanism to set the J17 encoding for the N2ADR filter board.

The extra 8th bit on the N2ADR filter board can be used to select between RX and TX antennas.

## Number and Bandwidth of Receivers

The Hermes-Lite 2.0 standard gateware allows for 4 hardware receivers. All receivers can run at bandwidths of 48,96,192 or 384kHz. There are gateware variants intended for skimming that have 9 hardware receivers but no transmit.

## Full Duplex

The Hermes-Lite 2.0 always runs in full-duplex, and software should show the received spectrum during transmit.

## Enable or Disable the PA

The Hermes-Lite 2.0 PA can be disabled. Transmit out is then via the low power RF1 connector with maximum power of 17dBm. The TR switch can also be disabled. This allows for full-duplex interfacing to a transverter.

# Optional Software Support

This section lists and describes optional Hermes-Lite 2.0 features supported by by some software. These features are not necessary for typical operation, but are often very nice to have.

## Bandscope

The Hermes-Lite 2.0 can send period sample sets direct from the 12-bit ADC. These can be used to reconstruct and waterfall and graph of the entire HF spectrum. This is not enough data to decode signals, but is enough data to provide a nice visualization of what is happening in the entire HF spectrum.

## PureSignal

The Hermes-Lite 2.0 supports feedback of the transmit signal to an extra receiver to facilitate predistortion of the transmit signal. This can reduce the IMD of a transmit signal, provide the software supports the necessary predistortion.

## Temperature, Bias Current and FWD/REV Power Readings

The Hermes-Lite 2.0 has a slow ADC for instrumentation. This monitors temperature, PA bias current as well as forward and reverse transmit power. Software can display this information in realtime.

The bias current readings are used to properly set the PA bias. This is done at the factory, but can be redone by some software packages.

Quisk and SparkSDR provide a way to create a calibration table for the forward and reverse power measurements. This increases the accuracy of these measurements, but requires a one-time calibration step of manually measuring transmit power into a dummy load several times.

## FPGA-Generated Power Supply Switching Clock

The Hermes-Lite 2.0 uses some internal switching power supplies. These can sometimes cause spurs in the 160M band when using the N2ADR filter board. The Hermes-Lite 2.0 is by default enabled to generate a switching clock that falls outside of any amateur radio band. Some software allows the user to turn this feature on or off.

## Set MAC and Static IP

A unique MAC for each Hermes-Lite 2.0 is required if you have multiple units on the same network. Quisk allows you to write a new MAC into an EEPROM. You may also set a static IP, but the preferred method is to have you DHCP server assign a static IP based on the MAC.

## Hardware-Managed LNA Setting for Transmit

During transmit, you often want the Hermes-Lite LNA set to a lower level to not overload the ADC. Some software has the ability to set a LNA value for TX. The hardware quickly changes the LNA to this value during transmit.

## Icom AH-4 Compatible Tuner Support

The Hermes-Lite 2.0 can be configured to send the correct tuning signals to an Icom AH-4 or compatible tuner during tune. The Hermes-Lite 2.0 will recognize successful or unsuccessful tune.

## Transmit Buffer Latency

Some software can tune the transmit buffer latency for lower latency.

## CWX

The Hermes-Lite 2.0 supports the CWX protocol, which is used by some software to send keyboard CW.

## Hang Times

Hang times for both CW and PTT can be set by software.

## Vector Network Analyzer

The Hermes-Lite 2.0 can be used as a VNA for the HF frequencies. This is useful for checking filters or antenna analysis. See Quisk VNA.

## Clock Setup

The Hermes-Lite 2.0 has external clock input and output connectors CL1 and CL2. Software can be used to set a clock output on CL2 at a certain frequency, or accept a high resolution clock input on CL1. This is typical for synchronized radios, or if a higher quality external clock is available.

## Update the Gateware

Quisk and SparkSDR can update the gateware over the network. This is the preferred gateware update mechanism. See the [gateware page](Updating-Gateware) for details.


