The Fan PWM output of the Hermes-Lite can be re-purposed to provide a DC voltage which represents the current transmission band. The output circuitry of the Fan PWM has also been re-purposed to provide a filtered DC voltage. 

The output has been designed to drive the band control input of the XPA125B amplifier which has an input impedance of about 90K ohms.  

# Circuit

![](pictures/BVCircuit.jpg) 

Two additional resistors have been added to the circuit and C4 has been increased in value. 

# BOM

| Reference | Quantity | Component |
| --------- | -------- | --------- |
| Q1 | 1 | [DTC144E](https://www.digikey.com/products/en?keywords=ddtc144tuadict-nd) |
| Q2 | 1 | [IRLM6402](https://www.digikey.com/products/en?keywords=irlml6402pbfct-nd) |
| Q2 (alt) | 1 | [PMV50UPE](https://www.digikey.co.uk/en/products/detail/nexperia-usa-inc/PMV50UPE-215/3679494) |
| R1 | 1 | [2K1 0805](https://www.digikey.co.uk/en/products/detail/yageo/RC0805FR-072K1L/727670) |
| R18 | 1 | [1K 0805](https://www.digikey.com/product-detail/en/yageo/RC0805FR-071KL/311-1.00KCRCT-ND/730391) |
| R19 | 1 | [5k1 0805](https://www.digikey.com/en/products/detail/yageo/RC0805FR-075K1L/311-5.10KCRCT-ND/727988) |
| R20 | 1 | [511 0805](https://www.digikey.com/en/products/detail/yageo/RC0805FR-07511RL/728018) |
| C2 | 1 | [0.1uF 0805](https://www.digikey.com/product-detail/en/kemet/C0805C104Z5VACTU/399-1177-1-ND/411452) |
| C4 | 1 | [10uF 1206](https://www.digikey.com/en/products/detail/kemet/T491A106M016AT/818548) |
| C5 | 1 | [0.01uF 0805](https://www.digikey.com/en/products/detail/kemet/C0805F103M5RACAUTO/10232836) 

# Track Mod

The original circuit layout has been utilised but one output pad needs to be cut. This can be done with a sharp blade carefully scraped across the track.

![](pictures/BVCutTrack.jpg)

# Layout

The position of C4 has moved and its original pads are now used for R20. R19 straddles the new cut track and C4 is placed across the output pads, after R19.

![](pictures/BVBackPlane.jpg)

# Connections

The pick-up point for the Band Volts PWM is on Pin 4 of DB1 and Pin 20 supplies the 3v3 which is connected to VSUP on the end plate.

![](pictures/BVConnect.jpg)

# Software Prerequisites

* You need an up to date Gateware on your HL2 (Band Volt Feature where added at Gateware 72p5)
* SDR Software shoud have enabled the "Dither" Feature to activate the Band Voltage output in the gateware. (in piHPSDR you will find the setting at "Menu - RX")


# Voltage Output

The output voltage for a particular band is given in the following table, with the actual recorded output from the prototype:

|   Band   | Level(mV) | Actual |
|----------|-----------|--------|
| 1.8 MHz  |    230    |   270  |
| 3.8 MHz  |    460    |   502  |
| 5.0 MHz  |    690    |   720  |
| 7.0 MHz  |    920    |   958  |
| 10.0 MHz |   1150    |  1183  |
| 14.0 MHz |   1380    |  1402  |
| 18.0 MHz |   1610    |  1620  |
| 21.0 MHz |   1840    |  1836  |
| 24.0 MHz |   2070    |  2048  |
| 28.0 MHz |   2300    |  2257  |
