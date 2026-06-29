
## Where can I purchase a complete tested unit?

The Hermes-Lite 2.0 is an experimental project which targets homebrewers. Some people buy all the parts and PCBs on their own and build a unit. We also have [group buys](Group-Buy) for completed units. 

## What frequencies does the Hermes-Lite 2.0 cover?

The Hermes-Lite 2.0 covers the HF frequencies, 0 to 38.4 MHz. Due to filtering, there may be some attenuation in the 30 to 38.4 MHz range. Unlike the Hermes, there is no 6M coverage. There are past [community projects](https://github.com/softerhardware/Hermes-Lite2/wiki/Community-Projects) to add 6M coverage via undersampling.

Although the AD9866 is specified to perform down to 0Hz, alterations to the hardware or software may be required for successful use below 130kHz.

## Where is the audio jack?

There is no audio out on the Hermes-Lite 2.0. In openHPSDR architectures, software running on the host PC does the final processing to audio frequencies. It can not be done on the FPGA alone. Rather than send the audio data back to the Hermes-Lite 2.0 and add a sound card on the Hermes-Lite 2.0, this audio data is sent directly to any sound card attached to the host PC. The Hermes-Lite 2.0 is more about making use of what already exists in the host PC. This also allows the Hermes-Lite 2.0 to be at a remote location.

## What is the power output?

The Hermes-Lite 2.0 is a QRP transceiver and achieves 5W out on all HF amateur radio bands. There is a secondary low power instrumentation output that provides maximum 17dBm. Either power output can be lowered by up to 7.5 dB using internal attenuation of the AD9866, not just by lowering the audio input levels.

## What is the ADC and DAC bit resolution?

The AD9866 ADC and DAC run at 12-bits. The ADC samples at 76.8MHz. The DAC runs at 153.6MHz to allow for interpolation on the output.

## How many slice receivers are supported?

Currently 4. There are gateware variants that can be loaded easily into the Hermes-Lite 2.0. One gateware variant allows for 9 slice receivers.

## What bandwidth is supported?

Since the Hermes-Lite is intended for amateur radio HF use, large bandwidths are not required. Each receiver supports a maximum of 384 kHz. Raw ADC samples are sent to the host PC periodically so that a good visualiation of the entire HF spectrum can be created.

## What does the Key/PTT jack do?

The Hermes-Lite has a 3.5 mm stereo jack CN4 on the front panel. The ring connector is the push-to-talk input. Ground it to put the Hermes-Lite into transmit mode. The tip connector is the key input. Ground it to put the Hermes-Lite into transmit mode and generate a CW signal. The Hermes-Lite does not have an internal keyer, so connect your external keyer or a straight key to the CN4 tip. Starting with gateware 71p2 you can connect a foot switch to the ring to turn on transmit and then key CW using the tip. Previously the functions were separate.

The ring and tip status are sent to the PC so that your SDR software can react to them. For details see the [protocol page](https://github.com/softerhardware/Hermes-Lite2/wiki/Protocol). The PC can also set transmit mode and send CW without using CN4. See the documentation for your SDR software.

## Where are all specifications?

There are some performance numbers on the [Performance Tests](Performance-Tests) wiki page. The Hermes-Lite 2.0 project is more about making the best use of the inexpensive commodity AD9866 part, rather than meeting various performance targets. As such, performance numbers are largely set by the AD9866 part which is well documented in the [datasheet](https://www.analog.com/media/en/technical-documentation/data-sheets/AD9866.pdf). In general, the Hermes-Lite 2.0 provides good performance for its price range.

## I am experiencing problems using a WiFi connection. Any tips?

Some users may find that their WiFi connection causes problems with either; their SDR software reporting errors or the sound of relays clicking at a fast rate. This could be one of many problems, but a poor WiFi connection is a likely reason. If possible, use a wired connection to the Hermes-Lite to understand if the problem is related to WiFi connection. Next, try increasing the size of the TX IQ buffers in the Hermes-Lite. This can be done using the Hermes-Lite [python module](https://github.com/softerhardware/Hermes-Lite2/blob/master/software/hermeslite/hermeslite.py). If problems are still experienced, there is [experimental gateware](https://github.com/softerhardware/Hermes-Lite2/tree/master/gateware/bitfiles/testing/20200803_72p3) that expands the size of the buffer even further.

User experience has shown that using 5 GHz WiFi can [improve]( https://groups.google.com/d/msg/hermes-lite/4Iagw7nhLpY/GZd4PEgdCAAJ) the quality of service. [Others](https://groups.google.com/d/msg/hermes-lite/Q2A2H6eDSXQ/zapx5GeUAgAJ) have not been so successful. Problems are likely to be a complex mix of WiFi environment, network traffic and WiFi hardware. Searching the [forum](https://groups.google.com/forum/#!forum/hermes-lite) may provide some clues if others have experienced problems and have a solution.  

See also the Wifi section of the [Troubleshoot Network](Troubleshoot-Network) page.

## I see a lower decode rate than expected using WSJT-X/FT8 and frequency drift is observed, any tips?

Some Windows users have seen a WSJT-X FT8 decode rate 60%-70% lower than expected and frequency drift is observed in the WSJT-X waterfall.  This issue has been seen using multiple Thetis and PowerSDR versions.  Testing by monitoring a stable frequency indicated the RF appeared stable but WSJT-X's waterfall was showing frequency drift between transmit cycles. It was cured by checking the "Force" boxes under Setup/Audio/VAC1 VAC1 Monitor and confirming all the virtual audio devices were set to the same sample rates. The VB cables often default to 44.1K on my system and changing them to 48K is advised.  If you are using VB Banana or similar, check for similar settings.

