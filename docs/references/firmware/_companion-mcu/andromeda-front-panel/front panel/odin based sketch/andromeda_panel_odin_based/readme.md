This is the Arduino sketch for the Odin SDR console. It is written for an Arduino Due board.

Arduino libraries install into a folder that has been created by your Arduino IDE. Usually that folder is in "documents\arduino\libraries".

To build this code you will need to install the following libraries into your documents\arduino\libraries folder:

DueFlashStorage
DueTimer
Encoder
ITEADLIB_Arduino_Nextion


To install the libraries
========================
To add more libraries, you need to add a correctly names folder to that location. 

DueFlashStorage: 
1. download from https://github.com/sebnil/DueFlashStorage
2. unzip into a folder "DueFlashStorage" in your documents\arduino\libraries folder
3. (if you end up with a folder DueFlashStorage-master, rename it to remove the "-master")

Encoder: 
1. open the Arduino IDE
2. click "Sketch | Include Library | Manage libraries..." on the menu
3. in the library manager type "encoder" where it sayds "filter your search" and hit enter
4. find "encoder" by Paul Stoffregen and click "install"

Due Timer:
1. open the Arduino IDE
2. click "Sketch | Include Library | Manage libraries..." on the menu
3. in the library manager type "due timer" where it sayds "filter your search" and hit enter
4. find "DueTimer" by Ivan Seidel and click "install"

ITEADLIB_Arduino_Nextion:
1. Download from https://github.com/itead/ITEADLIB_Arduino_Nextion
2. unzip into a folder "ITEADLIB_Arduino_Nextion" in your documents\arduino\libraries folder
3. (if you end up with a folder ITEADLIB_Arduino_Nextion-master, rename it to remove the "-master")
4. (This is the library published by the display manufacturer. Be aware there is some foul language in thr "html" folder - delete the entire "html" folder if you do not want that)


Downloading the Odin software repository
========================================
1. download the repository from "https://github.com/laurencebarker/odin-SDR-console"
2. you will get a zip file; unzip it into a folder on your computer - for example "documents\sdr"
3. At that point you will have a folder "documents\sdr\odin-SDR-console-master"
4. you will now need to replace 3 of the files in the Nextion library with files that are stored in the Odin repository.
	- copy the files from "documents\sdr\odin-SDR-console-master\nextion display\arduino_library_update" to "documents\arduino\libraries\ITEADLIB_Arduino_Nextion"
	- the 3 files are nexconfig.h, nexhardware.cpp, nexhardware.h
5. To open the Odin software sketch:
6. Run the Arduino IDE
7. Use the "File|Open..." menu command
8. Navigate to "odin_sdr_console.ino" in folder "Documents\SDR\odin-SDR-console-master\sketch\odin_sdr_console" and click "open"
9. you should now see the files listed in tabs above the editor window



To build the Arduino code
=========================
In the Arduino IDE:
1. Click "board" on the "tools" menu and select "Arduino Due (programming Port)"
2. Click "Verify/compile" on the "sketch" menu to compile
3. (this will take around a minute and should result in a message saying the % of program space used)


To upload to your arduino
=========================
1. Connect the Arduino programming port to your PC using a USB cable
2. Click "port" from the "tools" menu and select the serial port your board is using
3. Click "Upload" on the "sketch" menu to upload to the Arduino
4. A simple progress bar will show in the bottom window of the IDE, twice - for each of "programming" and "verify"
5. When it has successful finished the last message will be "CPU reset"