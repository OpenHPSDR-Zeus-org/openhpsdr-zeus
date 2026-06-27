This is the repository for the Andromeda front panel

It includes an Arduino sketch for the Andromeda front panel controller. The newest code and hardware is written for an Arduino Nano Every board.

There are three sketches here:
1. The newest sketch, using an Arduino Nano Every board.
2. The new, intended front panel controller, using and Arduino due board
3. an interim one, based on Odin code using CAT commands


There are instructions in the documentation for building the older versions. For the newesT, Nano Every Arduino:



Downloading the Andromeda software repository
========================================
1. download the repository from "https://github.com/laurencebarker/odin-SDR-console"
2. you will get a zip file; unzip it into a folder on your computer - for example "documents\sdr"
3. At that point you will have a folder "documents\sdr\andromeda-front-panel-master"
5. To open the Andromeda software sketch:
6. Run the Arduino IDE
7. Use the "File|Open..." menu command
8. Navigate to "andromeda_front_panel.ino" in folder "Documents\SDR\andromeda\sketch\andromeda-front-panel" and click "open"
9. you should now see the files listed in tabs above the editor window



To add support for the Nano Every
=================================
1. open board manager on the IDE
2. select and install “Arduino mega AVR boards”
3. Select tools > boards > Arduino megaAVR boards > Arduino Nano Every
4. select tools -> registers emulation -> none (ATMEGA4809)



To build the Arduino code
=========================
In the Arduino IDE:
1. Click "Verify/compile" on the "sketch" menu to compile
2. (this will take around a minute and should result in a message saying the % of program space used)


To upload to your arduino
=========================
1. Connect the Arduino programming port to your PC using a USB cable
2. Click "port" from the "tools" menu and select the serial port your board is using
3. Click "Upload" on the "sketch" menu to upload to the Arduino
4. A simple progress bar will show in the bottom window of the IDE, twice - for each of "programming" and "verify"
5. When it has successful finished the last message will be "CPU reset"