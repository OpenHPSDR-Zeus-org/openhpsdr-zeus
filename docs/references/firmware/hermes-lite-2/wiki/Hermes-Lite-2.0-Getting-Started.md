# DEPRECATED PAGE!!!

These notes cover builds 5 to 7, which came as a partially assembled kits. They are archived here for historical purposes. Please refer to links on the wiki sidebar for the latest information.*

This page covers how to setup a Hermes-Lite 2.0 and get it on the air! If you don't yet have a Hermes-Lite 2.0, please see the [Releases](Releases) wiki page. See the main [Hermes-Lite Web Page](http://www.hermeslite.com) for links to the latest schematics, BOM and production files.

# RX Only Test and Use

If you obtained one of the partially assembled Hermes-Lite 2.0 units, you can still use it immediately for receive. Completion is only required for transmit and proper RF connectors. Below is a picture of a fresh unit from connected just to an antenna, power and ethernet. The antenna is a wire inserted into a through hole for the center pin of the RF3 connector footprint. Software running on a host is skimming FT-8.

[![hl2justrx](pictures/hl2justrx.jpg)](pictures/hl2justrx.jpg)


# Hand-Installed Components and Add-Ons

Hermes-Lite 2 components not installed by the assembly house are marked DNI (Do Not Install) on the [schematic](https://github.com/softerhardware/Hermes-Lite2/raw/master/hardware/hl/hermeslite.pdf) and [BOM](https://github.com/softerhardware/Hermes-Lite2/raw/master/hardware/hl/bom/bom.standard.pdf). Some of these components must be hand installed to complete the Hermes-Lite 2. Some may be installed to enable optional functionality. This section documents these hand installed components. 

Please see the [HL2 Build Completion](https://youtu.be/L_8KbabSFZ0) video for more help.

### Kit

The kitted Hermes-Lite 2.0 partial builds with N2ADR companion filter card include the parts seen below. These must be hand installed.

[![kitparts](pictures/kitparts.jpg)](pictures/kitparts.jpg)

## TX Transmit Balun

T3 must be hand installed to complete the onboard PA. Two cores have been tested:

 * [B62152A4X30](https://octopart.com/search?q=B62152A4X30)
 * [BN43-202](http://www.kitsandparts.com/toroids.php)

The B62152A4X30 core conducts and there have been problems when using enamel wire with this core. Use wire with high temperature insulation (PFTE/FEP) with this core. Some options are:

 * FEP wire Junkosha AF04B050 (SA)
 * [7*0.2 ptfe wire](https://www.wires.co.uk/acatalog/cu_sp_ptfe.html)
 * [Solid silver wire 22AWG FPE](https://www.ebay.com/p/solid-silver-wire-22-awg-fpe-insulation-teflon-dielectric-500-feet/1740729388?_trksid=p2047675.l2644)


### Transformer Pin-Out

The T3 transformer pin numbers on the schematic and its PCB thru-hole connections location are shown in the following picture:

[![hl2t3pinout](pictures/hl2t3pinout.png)](pictures/hl2t3pinout.png)

Note that the polarity between primary and secondary is not important, pins 4 and 5 on the primary can be swapped as well as pins 1 and 3 on the secondary.

When using the B62152A4X30 core, with a 4-turns winding and 1 turn plus 1 turn on the other winding the transformer windings connections will look like in the following drawing:

[![hl2t3drawing](pictures/hl2t3drawing.png)](pictures/hl2t3drawing.png)

### Kit

The kitted Hermes-Lite 2.0 partial builds include wire with high temperature insulation and the [B62152A4X30](https://octopart.com/search?q=B62152A4X30) core. To complete this, use about *14 to 16cm* of the wire for four turns through the core. Measure the amount of wire you received and budget accordingly. See the picture below. On the bottom side of the core where the wires protrude, you should count three wires looping through the core. On the other side of the core where no wires protrude, you should count four wires looping through the core.

Below is the TX balun with 4 loops showing which holes the wire ends should be soldered to. Note that on the bottom of the core you count *3* turns going from hole to hole.

[![txbalun_b3l](pictures/txbalun_b3l.jpg)](pictures/txbalun_b3l.jpg)

Below is a top view of the TX balun with 4 loops. Note that from this side you count *4* turns going from hole to hole.

[![txbalun_t4l](pictures/txbalun_t4l.jpg)](pictures/txbalun_t4l.jpg)

The two single loops are shown below. First is the left and then the right.

[![txbalun_l1l](pictures/txbalun_l1l.jpg)](pictures/txbalun_l1l.jpg)

[![txbalun_r1l](pictures/txbalun_r1l.jpg)](pictures/txbalun_r1l.jpg)

First the four turn loop is wound, then the two single loops in any order. The TX balun with all loops should be soldered snuggly on to the PCB as seen below.

[![hl2b7](pictures/hl2b7.jpg)](pictures/hl2b7.jpg)


One way to complete the TX balun is to solder the two single loops first to the center power through hole as seen below. These wires should be about *3.5 to 4.5cm* in length each.

[![txbalun1](pictures/txbalun1.jpg)](pictures/txbalun1.jpg)

Next, install the core with the four turn secondary wires going through the two holes farthest from the edge as seen below. One of the primary turns will go up through the left hole of the core, down the right hole and into the right hole of the PCB. The other primary turn will go up through the right hole of the core, down the left hole and into the left hole of the PCB.

[![txbalun2](pictures/txbalun2.jpg)](pictures/txbalun2.jpg)



## RF and Clock Connectors

There are various RF and clock connectors which may be installed depending on which (if any) external clock options are desired and which companion filter card is used. These are prefixed on the schematic with RF or CL. The expectation is that most builders will use edge mount SMA connectors.

[CONSMA003.062](https://octopart.com/search?q=CONSMA003.062&start=0) has a round base and may be used when a chassis end plate with a round hole.

[CON-SMA-EDGE-S](https://octopart.com/search?q=CON-SMA-EDGE-S&start=0) is a less expensive option but has a square base.

For the least expensive option, standard vertical SMA connectors, such as [these](https://www.aliexpress.com/item/100-Pcs-Gold-copper-SMA-female-jack-Panel-Mount-PCB-Solder-Connector/32607809145.html) from AliExpress, may be used in edge mount configuration. This is my preferred choice. Search AliExpress and Ebay have a wide variety of SMA connectors that can work, including round base and those designed specifically for edge mount.

Standard vertical SMA connectors may also be used in vertical orientation with some overhang by inserting 3 of the pins in the RF1,RF2,RF3,CL1 or CL2 footprints.

The edge mount footprints are also wide enough to accommodate BNC edge mount connectors like the [031-6009](https://octopart.com/search?q=031-6009&start=0). If these are used, it is expected that only RF1 and RF2 (no RF3) and only CL1 or CL2 use this part as the BNC connectors are larger and there is not enough room for the connecting BNC cables.

The optional uFL connectors on the Hermes-Lite2 are designed to use part [73412-0112](https://www.adafruit.com/product/1661). Although a surface mount part, it may be hand soldered.

### Kit

The kitted Hermes-Lite 2.0 partial builds include two SMA connectors. These can be installed edge mounted as shown below for the low power TX connector and the RX/TX main connector. The middle RX connector is optional. *Only install the SMA connectors if you will not use the N2ADR companion filter card.*

[![hl2b7conn](pictures/hl2b7conn.jpg)](pictures/hl2b7conn.jpg)

For builds that use the N2ADR companion filter card, a single row header as pictured in the next section. See the N2ADR comapnion filter card section for more details.

## Transmit/Receive Relay

The relay K2 may be installed to use the Hermes-Lite 2 with inline lowpass filters for transmit or to support some companion filter cards. Compatible relays are:

 * [EC2-3NU](https://octopart.com/search?q=EC2-3NU)
   * [AliExpress](https://www.aliexpress.com/item/EC2-3NJ-8PINS-3VDC-Signal-Relay-original-New/32269204657.html) - EC2-3NJ, same as EC2-3NU, but with trimmed leads.
 * [NA-3W-K](https://octopart.com/search?q=NA-3W-K)

There may be other compatible relays on AliExpress or Ebay with a common footprint. Note that a lower 3.3V is used for K2 on the Hermes-Lite 2.

### Kit

The kitted Hermes-Lite 2.0 partial builds include a TR relay and CW key jack. Install these as pictured below.

[![hl2b7](pictures/hl2b7.jpg)](pictures/hl2b7.jpg)


## 0.1 Inch Daughter Board Connectors

Throughout the Hermes-Lite 2 schematic, there are connectors prefixed with DB. Common 0.1 inch connectors may be used as needed in these locations. For DB1, DB12 and DB13, there is enough room allocated for a ribbon cable connector to be used. In many places, Arduino stackable headers may be used. Besides AliExpress and Ebay, a good variety of 0.1 inch connectors may be found at [Pololu](https://www.pololu.com/category/19/connectors).

## AD9866 Heat Sink

The AD9866 may run hot in some builds of the Hermes-Lite 2. There are small heat sinks such as [this](https://www.digikey.com/product-detail/en/assmann-wsw-components/V5618A/AE10819-ND/3511413) or [this](https://www.digikey.com/product-detail/en/assmann-wsw-components/V2017B/AE10837-ND/3511410) which may be used to help cool the AD9866. The size of the AD9866 is 9mm by 9mm, so care must be taken to not short pins if any heat sink that size or larger is used.

A small custom PCB for AD9866 shielding and heat dissipation is planned. It will lay flat on top of the AD9866 and connect to ground at several of the large ground through holes near the AD9866. More details will be posted here and on the Google group once this PCB is designed. 


# Enclosure and Power Supply

The Hermes-Lite 2 is typically sold as only a partially assembled board. The user must provide an enclosure, adequete PA heat sinking and power.

Please see the [HL2 Build Completion](https://youtu.be/L_8KbabSFZ0) video for more help.


## Power Connector and Supply

A 2.1mm center pin 5.5mm outer diameter barrel connector is included by default at the front of the Hermes-Lite 2. A similar footprint at the rear is available for an alternate power connector. Both power supply connector footprints support these components:

 * [PJ-102AH](https://octopart.com/search?q=PJ-102AH) or from [AdaFruit](https://www.adafruit.com/product/373)
 * [PJ-102BH](https://octopart.com/search?q=pj-102bh) for 2.5mm center pin
 * [OSTTC020162](https://octopart.com/search?q=osttc020162) for terminal block, installed sideways

A 11V to 16V power supply capable of at least 2A is recommended. A list of low noise brands and suppliers is being compiled. 

An inexpensive source of cable with the correct barrel connector is your local thrift store. I bought a $0.99 wall wart with the correct connector, cut off the cable and attached a standard PowerPole connector to the other end. Now my Hermes-Lite 2.0 is compatible with all my shack batteries and power supplies.

### Kit 

The kit includes the proper barrel connector for the Hermes-Lite 2. Connect +12VDC and GND as pictured below. 

[![power](pictures/power.jpg)](pictures/power.jpg)

## Enclosure

See [this page](Hermes-Lite-2.0-Getting-Started#enclosure) describing the recommended enclosures and how to mount the Hermes-Lite 2.0 board there.

## N2ADR Companion Filter Card

Pictured below is the N2ADR companion filter card. If you have the kit, please install the 7 relays, phono plug for external PA TR switching, 2 SMA connectors, power sense toroid and the edge connector. The main RX/TX RF connector is nearest the power sense toroid. The second RF connector is low power TX out.

Note that some of the phono plugs shipped with the kit are intentionally a tight fit. First insert the rear (farthest from the edge) pin in the proper hole as far as it will go. Then, rock the front pins including the two plastic tabs into their respective holes. You may need to apply a little pressure towards the rear pin but everything will snap into place. 

[![filterboard1](pictures/filterboard1.jpg)](pictures/filterboard1.jpg)

Also, assemble the jumper board which consist of the small shorting PCB and the 2x20 pin female header. When installing the two 1x20 pin male headers, one on the HL2 and one on the filter card, it is easiest to do this when held together by this jumper board assembly.

Below is the power sense toroid winding. There are ten turns of the enamel wire and one single white wire through the center when attached to the PCB. 

[![filterwinding](pictures/filterwinding.jpg)](pictures/filterwinding.jpg)

Below is the power sense toroid installed on the N2ADR filter board.

[![pwr_t10l](pictures/pwr_t10l.jpg)](pictures/pwr_t10l.jpg)

Finally, a single wire (no loops) goes through the center of the power sense toroid as shown below.

[![pwr_1l](pictures/pwr_1l.jpg)](pictures/pwr_1l.jpg)

A completed Hermes-Lite 2.0 and N2ADR companion filter card in the recommended enclosre are pictured below.

[![hl2complete](pictures/hl2complete.jpg)](pictures/hl2complete.jpg)

For best accuracy, calibrate the power meter against a known reference with repeated readings. In Quisk this is under Config->Your Radio->Hardware->Power meter calibration. A similar calibration table can be made and/or used in Spark SDR.


