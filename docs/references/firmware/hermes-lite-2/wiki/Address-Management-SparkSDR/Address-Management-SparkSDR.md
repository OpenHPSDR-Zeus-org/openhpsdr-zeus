## Introduction

[SparkSDR](https://www.sparksdr.com) is a SDR application with a number of features:
  * You can connect to multiple radios at a time each with as many receivers as they support.
  * wspr, JT9-65 & FT8 decoding and encoding are built in using WSJTX, this avoids all vac & cat configuration issues .
  * low latency audio using linear interpolation to match the radio clock to the audio clock, this consumes very little cpu and seems fine for listening to audio, it may not be ideal for digital modes over vac but this is not an issue with the built in digital modes.
  * The rx and tx bandpass filters use a partitioned fft fir which reduces latency whilst allowing long/steep filters at the cost of increased cpu load.
  * ssb compression using the controlled envelope ssb clipping algorithm.
  * adc signal level meter using the raw bandscope data combined with the adc overflow flag which is useful for setting the rf gain.
  * Unlimited virtual receivers can share the bandwidth of each firmware slice receiver.
  * Touch friendly UI
  * Undersampling reception and transmission, depending on the filters in front of the radio you can tune to above the Nyquist frequency and odd/even spectrum folding is transparently corrected.
  * With a CVA9 fpga board and Hermes Lite it is no problem to simultaneously decode wspr, JT65/9, FT8 & psk on all bands below 30Mhz.
  * Radio frequency correction using network time protocol.
  * PSK31 decoder.
  * SparkSDR runs on windows 7,8 and 10, linux x64, linux arm(rpi) and mac.

  SparkSDR has a [Google Group](https://groups.google.com/forum/#!forum/sparksdr) that may be used to address any issues you may encounter.
  
  ## Installing SparkSDR

The [SparkSDR Downloads Page](https://www.sparksdr.com/downloads) shows the various software downloads available for different operating systems.

I downloaded the zip file to my Downloads folder (despite a few complaints from Chrome about malware), copied this file to a new folder, used right click -> Expand to unzip this into a new folder, then double-clicked on the 'SparkSDR' application.

The application ran without any need to install any other dependencies.

## Getting Started with SparkSDR

The '?' icon on the upper left can be used to open a help page, which has some tips for getting started using the application.

## Address Management with SparkSDR

Once the main screen opens, you can click on the 'three dots' icon in the upper right to get the Radio Settings menu.  The user interface appears as follows:

[![sparksdr radio options](Address-Management-SparkSDR/Capture01.PNG)](Address-Management-SparkSDR/Capture01.PNG)

We can see selecting the General tab (which is the default) allows us to store MAC and/or IP addresses in EEPROM, and allows us to set the flag to favor DHCP.  My configuration shows none of these are set because I am defaulting to DHCP address management.
