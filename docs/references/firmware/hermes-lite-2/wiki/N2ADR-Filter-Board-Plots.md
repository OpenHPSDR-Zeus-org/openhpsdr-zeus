
The [N2ADR filter board](http://james.ahlstrom.name/hl2filter/) is designed to work with the Hermes-Lite 2.0 and provide required low pass filtering for transmit. It also includes a high pass filter to provide more RF ADC dynamic headroom on receive for 3.5 MHz and higher. It also includes circuitry for forward and reverse power measurement.

# DK1CG's measurements

Here is the response of the various filters measured with a VNA

[![passband_losses](pictures/n2adr/N2ADR_FB_losses.png)](pictures/n2adr/N2ADR_FB_losses.png)

the filters losses in the intended bands are in general below 1 dB.

Here are the same responses over a wider frequency range

[![wideband_losses](pictures/n2adr/N2ADR_FB_wideband.png)](pictures/n2adr/N2ADR_FB_wideband.png)

in the wideband response a little stray coupling can be observed; it reduces the filters ultimate attenuation but it does not cause any issue in practice.

Below are spectrum plots for each amateur radio band with an Hermes-Lite 2.0 beta3 running at maximum power. The measurements were made after the N2ADR filter board, with the filter board connected to a spectrum analyzer via a suitable attenuator.
On every picture, the first marker shows the carrier power at the TX frequency, the one below is the delta of the highest harmonic seen over the entire spectrum and the last marker is the actual highest harmonic power.
Marker frequencies appear a little off, especially for the lower bands, due to the way the spectrum analyzer works but the power readings are correct.

## 160M

[![DK1CG_b3_160](pictures/n2adr/DK1CG_H-Lv2b3_160m.png)](pictures/n2adr/DK1CG_H-Lv2b3_160m.png)

## 80M

[![DK1CG_b3_80](pictures/n2adr/DK1CG_H-Lv2b3_80m.png)](pictures/n2adr/DK1CG_H-Lv2b3_80m.png)

## 60M

[![DK1CG_b3_60](pictures/n2adr/DK1CG_H-Lv2b3_60m.png)](pictures/n2adr/DK1CG_H-Lv2b3_60m.png)

## 40M

[![DK1CG_b3_40](pictures/n2adr/DK1CG_H-Lv2b3_40m.png)](pictures/n2adr/DK1CG_H-Lv2b3_40m.png)

## 30M

[![DK1CG_b3_30](pictures/n2adr/DK1CG_H-Lv2b3_30m.png)](pictures/n2adr/DK1CG_H-Lv2b3_30m.png)

## 20M

[![DK1CG_b3_20](pictures/n2adr/DK1CG_H-Lv2b3_20m.png)](pictures/n2adr/DK1CG_H-Lv2b3_20m.png)

## 17M

[![DK1CG_b3_17](pictures/n2adr/DK1CG_H-Lv2b3_17m.png)](pictures/n2adr/DK1CG_H-Lv2b3_17m.png)

## 15M

[![DK1CG_b3_15](pictures/n2adr/DK1CG_H-Lv2b3_15m.png)](pictures/n2adr/DK1CG_H-Lv2b3_15m.png)

## 12M

[![DK1CG_b3_12](pictures/n2adr/DK1CG_H-Lv2b3_12m.png)](pictures/n2adr/DK1CG_H-Lv2b3_12m.png)

## 10M

[![DK1CG_b3_10](pictures/n2adr/DK1CG_H-Lv2b3_10m.png)](pictures/n2adr/DK1CG_H-Lv2b3_10m.png)

worst-case harmonics are 52 dB down, great work!


# KF7O's measurements

The filter unit used by KF7O is pictured below.

[![n2adr_filterboard](pictures/n2adr/n2adr_filterboard.jpg)](pictures/n2adr/n2adr_filterboard.jpg)

Below are spectrum plots for each amateur radio band with the Hermes-Lite 2.0 running at maximum power. The measurements were made after the N2ADR filter board, with the filter board connected to a dummy load.

## 160M

[![b5_160](pictures/n2adr/b5_160.png)](pictures/n2adr/b5_160.png)

## 80M

[![b5_80](pictures/n2adr/b5_80.png)](pictures/n2adr/b5_80.png)

## 60M

[![b5_60](pictures/n2adr/b5_60.png)](pictures/n2adr/b5_60.png)

## 40M

[![b5_40](pictures/n2adr/b5_40.png)](pictures/n2adr/b5_40.png)

## 30M

[![b5_30](pictures/n2adr/b5_30.png)](pictures/n2adr/b5_30.png)

## 20M

[![b5_20](pictures/n2adr/b5_20.png)](pictures/n2adr/b5_20.png)

## 17M

[![b5_17](pictures/n2adr/b5_17.png)](pictures/n2adr/b5_17.png)

## 15M

[![b5_15](pictures/n2adr/b5_15.png)](pictures/n2adr/b5_15.png)

## 12M

[![b5_12](pictures/n2adr/b5_12.png)](pictures/n2adr/b5_12.png)

## 10M

[![b5_10](pictures/n2adr/b5_10.png)](pictures/n2adr/b5_10.png)