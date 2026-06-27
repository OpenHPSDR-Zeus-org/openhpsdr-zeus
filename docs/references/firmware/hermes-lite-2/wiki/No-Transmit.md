


No RF power out during transmit is a common problem reported to the [Hermes-Lite Google Group](https://groups.google.com/g/hermes-lite) by new Hermes-Lite 2 owners. This problem is usually operator error, incorrect software configuration or a faulty Hermes-Lite 2 board. All Hermes-Lite 2 units are tested for full transmit power at Makerfabs, but problems can occur in shipping, assembly or after more extended use. To help diagnose your problem, please follow these steps.

1. Ensure your software is configured properly for transmit. All compatible software has similar configuration.
 - The PA must be enabled. [Thetis](Thetis-Setup#setupgeneralpa-controlpa)
 - Filters must be set to not attenuate your transmit signal. [Thetis](Thetis-Setup#setupgeneraloc-control)
 - Software settings which may attenuate transmit per band must be adjusted to maximum. [Thetis](Thetis-Setup#setuppa-settings)
 - The HL2 has a 7.5dB hardware gain. This should be adjusted to maximum. [Thetis1](Thetis-Setup#setuptransmit) [Thetis2](Thetis-Setup#setuppa-settings)
 - Audio to the software must be configured correctly and set to the proper sampling rate (48kHz) and levels. Some software such as Quisk will not transmit if no transmit audio device is specified. [Thetis](Thetis-Setup#setupaudiovac-1)
2. Configure your software for CW and ground the tip to shield of the front KEY/PTT stereo connector on the HL2. 
 - Does the HL2 switch to TX as indicated by the TX LED illuminating? [TX LED picture](Raspberry-Pi-Test-and-Program#test-flashed-gateware-and-clock)
 - Do you hear relays changing when engaging transmit?
 - Do you measure any power out on the ANT SMA connector (not the RF1 SMA connector) when terminated with a proper 50 Ohm load? 
 - With the radio set in full duplex mode, do you see any RF signal (single spike) on the spectrum graph?
 - Do the temperature readings from the HL2 reported by software increase? What is the temperature range you see during TX?
3. If there is no power out, check for these physical problems.
 - Do you have the N2ADR board properly connect to the main HL2 board with the small 20x2 jumper board? [Assembly](Final-Assembly#n2adr-filter-companion-board) 
 - Confirm that the center pin of the ANT SMA connector and your external coax make proper contact.
 - Check that you did not damage or short any components when installing the heat shim. In particular, J31 and J32 on the bottom of the HL2 and near the heat shim have been scraped off in the past and cause TX failure. Also, B106 and L34 on the top of the HL2 near the bolt and nut hole can be damaged. [Assembly](Final-Assembly#heat-shim)
4. Check for expected voltages and bias current
 - Configure your software for SSB and engage TX via software or external PTT. Software should report bias current in the 180-220mA range. There is no need to set your bias current as this is configured at the factory and stored in nonvolatile memory on the HL2. This [YouTube video](https://youtu.be/mEUiqmx37L8?si=SJV2gefGrGlIeX3N) shows how to use Quisk to read (and set, which you don't need to do) the PA bias current. Other software has similar functionality.
 - With TX engaged, is the DC voltage measured at Vpa 8.0V?  [HL2 with voltage measurement points labeled.](Hardware)
 - With TX engaged, is the DC voltage measured at Vop 10.15V?  [HL2 with voltage measurement points labeled.](Hardware)
 - With TX engaged, is the DC voltage measured at Vbias 10.0?  [HL2 with voltage measurement points labeled.](Hardware)
5. Disable the PA in software and then engage CW transmit again as described in step 2. 
 - Do you measure any output when a proper 50 OHm load is connected to RF1, the low power output?
 - With the radio set in full duplex mode, do you see any RF signal (single spike) on the spectrum graph?
 - If you have an oscilloscope, configure software to 80M and measure any RF output on DB3 pin 4 to ground and DB3 pin 3 to ground during CW transmit. What is the peak to peak voltage, and is it a clean sinusoid at the 80M frequency? DB3 is in the lower middle section of the HL2 between the AD9866 (chip which takes the smaller heat sink) and 6 blue inductors as seen in the [HL2 with voltage measurement points labeled.](Hardware) picture.
6. Check for an intermittent or poorly grounded AD9866.
 - With TX engaged, apply finger pressure to the top of the AD9866 down towards the board. The AD9866 is U7 and takes the smaller heat sink as seen [here](Final-Assembly#apply-thermal-compound-and-pressure). While applying pressure in various amounts and slightly different directions, does RF power out appear?
 - On the bottom of the HL2 board, there is a larger hole which exposes the thermal pad of the AD9866. The thermal pad is soldered to ground at the factory and you will find it hard to notice any solder if done properly. Still, there have been cases when improving this ground solves transmit problems. Try filling this hole with solder to provide a possibly better ground to the AD9866. It will take a bit of time for the hole and surrounding ground plane to heat up enough to melt and take solder.


If you still have no power out, please post to the [Hermes-Lite Google Group](https://groups.google.com/g/hermes-lite) your results for all the tests above you were able perform. Also, initiate the repair/return process as described [here](Repair).

