## Visual Inspection

Look for any obvious solder shorts or bridges, misaligned components, or poorly soldered components. In particular, check for tiny balls of solder near or in between the fine pitched leads of U2, U4, U6 and U7.

## Power Supply Shorts

With an Ohm meter, it is recommended to check that there are no shorts (should measure >500 Ohm) between the following points labeled in the photograph.

 * +3V3 and GND
 * +2V5 and GND
 * +1V2 and GND
 * Vbias and GND
 * Vsupply and GND
 * Vop and GND
 * Vpa and GND
 * +3V3 and +2V5
 * +3V3 and +1V2
 * +2V5 and +1V2

![](pictures/rpi/hl2b5topwithpwr.jpg)

# RX Only Test and Use

As an initial test, you can connect your Hermes-Lite 2.0 directly to a long wire antenna and check out receive. Use the 6th pin from the end on the companion card connector for the long wire antenna. A picture without the male header is shown below.

[![hl2justrx](pictures/hl2justrx.jpg)](pictures/hl2justrx.jpg)

# Heat Shim

[40mm enclosure heat shim](https://github.com/softerhardware/Hermes-Lite2/tree/master/hardware/enclosure/heatshim/enclosure_40mm)


