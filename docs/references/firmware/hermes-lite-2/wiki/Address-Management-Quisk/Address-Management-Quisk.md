## Introduction

Quisk is a full-featured, Python-based SDR application.

I will show the basics of installing it for use for address management.

My computer currently has Windows 7 on it (still) so I will use that as my host OS.  

These instructions will largely be the same for Mac or Linux with small variations on the installation steps and file/pathname syntax. 

## Install Python

The newest supported Python **for Windows 7** at the time of this writing is available using the following link: [Python 3.8.8](https://www.python.org/downloads/release/python-388/).  I select the 64 bit installer, download it and run it.  I use the Advanced Option 'Add Python to the environment' so I can run it from the command line without having to set the PATH variable.  The Start Menu now has a `Python 3.8 (64 bit)` item I can select.  

The 'Advanced Options' I selected looks like:

[![python advanced options](Address-Management-Quisk/Capture00.PNG)](Address-Management-Quisk/Capture00.PNG)

[Click here](Address-Management-Quisk/Capture01.PNG) to see the versions of Python and Pip that were installed on my PC.

## Install Quisk

The [Quisk Documentation Page](https://james.ahlstrom.name/quisk/docs.html) section 'Windows Initial Installation' contains all the required steps.

[Click here](Address-Management-Quisk/Capture02.PNG) to see the steps I followed to install Quisk.  Note I used 'notepad' to make a file called 'pipper.bat' to run the commands to install all the required dependencies along with quisk.  Note also there is an error message when upgrading 'pip' itself.  This error message can be safely ignored.

The Quisk Documentation gives several ways to run the 'quisk' code.  

For me, the simplest way to run quisk that worked was to start a command window (Start Menu -> "command prompt" -> Enter)  then type:

    python -m quisk

The Quisk Documentation gives some hints as to where the key files are installed.  Following that guidance, 
  * Quisk Executable: 'C:\Users\Shaq\AppData\Roaming\Python\Python38\Scripts\quisk.exe'
  * Quisk Code: 'C:\Users\Shaq\AppData\Roaming\Python\Python38\site-packages\quisk\quisk.py'

Substitute your Windows login name for 'Shaq', that's the name of my ham shack computer (cute, eh?).

There are ways to run these files via shortcuts and/or file associations, but these are beyond the scope of this guide.

## Setting up Quisk with HL2 

[This YouTube video](https://www.youtube.com/watch?v=1pPbQplSBoo) goes through all the steps needed to configure Quisk with HL2, including filter settings.  [This YouTube video](https://www.youtube.com/watch?v=mEUiqmx37L8) goes through all the steps needed to adjust TX bias.  This should not be necessary for factory-assembled units.

## Address Management with Quisk

The address management information can be modified using Quisk by using the Config button to bring up the configuration screen.  It has tabs for each radio you have defined.  In my case I have defined a radio I called 'HL2' so I click on this tab.  The following is what I see:

[![quisk hl2 options](Address-Management-Quisk/Capture04.PNG)](Address-Management-Quisk/Capture04.PNG)

We can see in the title bar that it reports the HL2 is using MAC address '00:1c:c0:a2:13:dd', code version 72, board ID 6, and IP address '192.168.1.30'.  The front panel LEDs indicate I am using DCHP, which is correct.  

The MAC address '00:1c:c0:a2:13:dd' shown above is the default address, and it is programmed into the FPGA gateware. But if you have two HL2's on your network they must each have a unique MAC address, so you must change one of them to a different address. The HL2 has an EEPROM that can store the last two bytes of an alternate Ethernet address. When Quisk starts, it reads these values and displays them on the Hardware screen. The "Eeprom MAC Address" shows the two bytes in EEPROM, and the "Eeprom MAC Usage" shows the EEPROM bit that determines whether to use them. "Ignore" means use the default address and "Set address" means change the MAC address. If you change these values in Quisk, they are written to the HL2 EEPROM. Note that the HL2 only uses these values on power up.

So to change the MAC address on one of your HL2's, first disconnect all other HL2's so that the one to change is the only one on the network. Then use Quisk to change the EEPROM addresses and set the valid bit to "Set address". Then exit Quisk and restart it. You should see the new values you entered on the Hardware screen. Now exit Quisk and power the HL2 off and then on. Remember that the HL2 only uses these values on power up. Now start Quisk. You should see your new MAC address in the title bar.

The EEPROM can also store a fixed IP address. You must have a unique MAC address for each HL2, but using a fixed IP address is optional. The HL2 uses DHCP to get its IP address. Typically your home router is a DHCP server that assigns IP address from some range, such as 192.168.1.50 to 192.168.1.200. It will provide an IP address to each HL2. But if you need to know the IP address or want a fixed address, there are two options. Your home router can probably be programmed to assign a fixed IP address based on the MAC address of the requesting hardware. Or you could set the IP address in "Eeprom IP Address" and set "Eeprom IP Usage" to "Set address". Be sure to use an address outside the DHCP range, 50 to 200 in the above example. Otherwise your DHCP server could assign the same address. Power the HL2 down and then up. You should see your new IP address in the title bar.
