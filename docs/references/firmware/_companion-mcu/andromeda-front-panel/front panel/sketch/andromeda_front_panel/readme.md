This is part of the repository for the Andromeda front panel

This folder is an obsolete Arduino sketch for the Andromeda front panel controller using an Arduino Due processor . The newest code and hardware is written for an Arduino Nano Every board.



Arduino libraries install into a folder that has been created by your Arduino IDE. Usually that folder is in "documents\arduino\libraries".

To build this code you will need to install the following libraries into your documents\arduino\libraries folder:

DueFlashStorage
DueTimer
Encoder


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
3. in the library manager type "encoder" where it says "filter your search" and hit enter
4. find "encoder" by Paul Stoffregen and click "install"

Due Timer:
1. open the Arduino IDE
2. click "Sketch | Include Library | Manage libraries..." on the menu
3. in the library manager type "due timer" where it sayds "filter your search" and hit enter
4. find "DueTimer" by Ivan Seidel and click "install"



Downloading the Andromeda software repository
========================================
1. download the repository from "https://github.com/laurencebarker/Andromeda_front_panel"
2. you will get a zip file; unzip it into a folder on your computer - for example "documents\sdr"
3. At that point you will have a folder "documents\sdr\andromeda-front-panel-master"
5. To open the Andromeda software sketch:
6. Run the Arduino IDE
7. Use the "File|Open..." menu command
8. Navigate to "andromeda_front_panel.ino" in folder "Documents\SDR\andromeda\sketch\andromeda-front-panel" and click "open"
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