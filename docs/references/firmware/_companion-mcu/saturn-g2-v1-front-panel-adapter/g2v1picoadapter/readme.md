This is the folder for the most recent, RP2040-piZero sketch to allow a G2V1 front panel to be interfaced via USB serial. 





Arduino libraries install into a folder that has been created by your Arduino IDE. Usually that folder is in "documents\arduino\libraries".

To build this code you will need to install support for the processor: "Arduino Mega AVR boards by Arduino"




Downloading the G2V1 adapter software repository
================================================
1. download the repository from "https://github.com/laurencebarker/SaturnG2V1FrontPanelAdapter"
2. you will get a zip file; unzip it into a folder on your computer - for example "documents\sdr"
3. At that point you will have a folder "documents\sdr\andromeda-front-panel-master"
5. To open the Andromeda software sketch:
6. Run the Arduino IDE
7. Use the "File|Open..." menu command
8. Navigate to "g2v1picoadapter.ino" in folder "Documents\SDR\SaturnG2V1FrontPanelAdapter\g2v1picoadapter" and click "open"
9. you should now see the files listed in tabs above the editor window




To build the code
=================
In the Arduino IDE:
1. Click "board" on the "tools" menu and select "Wqveshare RP2040-piZero"
2. Click "Verify/compile" on the "sketch" menu to compile
3. (this will take around a minute and should result in a message saying the % of program space used)


To upload to your RP2040-piZero
=========================
1. Connect the RP{2040-piZero USB (NOT the one marked pio) port to your PC using a USB cable
2. Click "port" from the "tools" menu and select the serial port your board is using
3. Press the "Boot" button on your RP2040-piZero module
4. Click "Upload" on the "sketch" menu to upload to the RP2040-piZero
5. A simple progress bar will show in the bottom window of the IDE
6. Press the "run" button on your RP2040-piZero module