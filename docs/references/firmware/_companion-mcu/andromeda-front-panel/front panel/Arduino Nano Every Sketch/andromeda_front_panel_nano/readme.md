This is the folder for the most recent, "Nano Every" sketch for the Andromeda front panel




Arduino libraries install into a folder that has been created by your Arduino IDE. Usually that folder is in "documents\arduino\libraries".

To build this code you will need to install support for the processor: "Arduino Mega AVR boards by Arduino"




Downloading the Andromeda software repository
========================================
1. download the repository from "https://github.com/laurencebarker/Andromeda_front_panel"
2. you will get a zip file; unzip it into a folder on your computer - for example "documents\sdr"
3. At that point you will have a folder "documents\sdr\andromeda-front-panel-master"
5. To open the Andromeda software sketch:
6. Run the Arduino IDE
7. Use the "File|Open..." menu command
8. Navigate to "andromeda_front_panel.ino" in folder "Documents\SDR\andromeda-front-panel-master\front panel\Arduino Nano Every Sketch\andromeda_front_panel_nano" and click "open"
9. you should now see the files listed in tabs above the editor window




To build the Arduino code
=========================
In the Arduino IDE:
1. Click "board" on the "tools" menu and select "Arduino mega AVR boards -> Arduino Nano every"
Choose Tools -> register emulation -> none(atmeda4809)
2. Click "Verify/compile" on the "sketch" menu to compile
3. (this will take around a minute and should result in a message saying the % of program space used)


To upload to your arduino
=========================
1. Connect the Arduino programming port to your PC using a USB cable
2. Click "port" from the "tools" menu and select the serial port your board is using
3. Click "Upload" on the "sketch" menu to upload to the Arduino
4. A simple progress bar will show in the bottom window of the IDE, twice - for each of "programming" and "verify"
5. When it has successful finished the last message will be "CPU reset"