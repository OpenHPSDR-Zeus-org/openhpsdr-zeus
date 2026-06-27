The openHPSDR Thetis program now supports protocol 1 and so can be used with the Hermes Lite. A variant of the Thetis program has been modified to better integrate with the HL2 which can be downloaded [here](https://github.com/mi0bot/OpenHPSDR-Thetis/releases/). This page describes the setup procedure for this modified software.   

# Setup|General|H/W Select

![](pictures/thetis/setup-general-hardware.jpg) 

* Select HERMES LITE for the Radio Model.
* Select your Region to get the correct band plan for your location.

# Setup|General|F/W Set

![](pictures/thetis/setup-general-firmware-i2c.jpg) 

* Select RX1 Sample Rate of 384000.
* Hermes Lite Options allows for hardware specific changes to be made.

# Setup|General|Options|Options 1

![](pictures/thetis/setup-general-options-options1.jpg)

* Hermes Lite Step Attenuator for RX1 should be enabled.
* Auto Att allows the full rage of the ADC to be utilised. The Auto Delay determines how often the software tests for changes in the band conditions to determine if correct attenuation is being used. 

# Setup|General|OC Control

![](pictures/thetis/setup-general-occontrol-hf.jpg)

* If the correct pattern is not already setup, press the N2ADR Filter button. This will select the correct sequence for the N2ADR board. 

# Setup|General|PA Control|PA

![](pictures/thetis/setup-general-pacontrol-pa.jpg)

* Enable Full Duplex should be ticked.
* Enable PA should be ticked. 

# Setup|Audio|VAC 1

![](pictures/thetis/setup-audio-vac1.jpg)

* Enable VAC 1 should be ticked.

# Setup|Display

![](pictures/thetis/setup-display.jpg)

* Show Temp/Current should be ticked.
* Show Decimal should be ticked.

# Setup|Transmit

![](pictures/thetis/setup-transmit.jpg)

* Save the default profile by pressing the Save button. Press the OK for the Default profile in the pop up box. Select Yes to allow the default profile to be over written. This will ensure that the VAC is still selected when Thetis is restarted. 

# Setup|PA Settings

![](pictures/thetis/setup-pasettings-pagain.jpg)

* The gain settings should all be 100 for the bands that the HL2 supports. If not press the reset button to reset them.
* Click Apply, then Click OK.

![](pictures/thetis/drive-slider.jpg)

* Click the power button to connect to the Hermes-Lite 2.0
* The drive slider will default to -3.5dB. This will result in reduced transmit power levels. Slide this to the right to 0dB to increase to maximum power. This must be done for each band individually.



