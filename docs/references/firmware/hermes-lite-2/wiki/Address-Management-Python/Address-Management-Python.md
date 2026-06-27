## Introduction

Python is an easy to use environment and it is supported across all the major operating systems, so I will use it and the Hermes Lite Python Module to show how to do Address Management.  

My computer currently has Windows 7 on it (still) so I will use that as my host OS.  

These instructions will largely be the same for Mac or Linux with small variations on the installation steps and file/pathname syntax. 

## Install Python

The newest supported Python **for Windows 7** at the time of this writing is available using the following link: [Python 3.8.8](https://www.python.org/downloads/release/python-388/).  I select the 64 bit installer, download it and run it.  I use the Advanced Option 'Add Python to the environment' so I can run it from the command line without having to set the PATH variable.  The Start Menu now has a `Python 3.8 (64 bit)` item I can select.  

The 'Advanced Options' I selected looks like:

[![python advanced options](Address-Management-Python/Capture00.PNG)](Address-Management-Python/Capture00.PNG)

## Install the Hermes Lite Python Module

If you click on [this link](https://downgit.github.io/#/home?url=https://github.com/softerhardware/Hermes-Lite2/tree/master/software/hermeslite) it will open a page that will download a zip file called 'hermeslite.zip' into your chosen Downloads folder.  If you are knowledgeable about Git and have it installed, you can either download or clone the entire HL2 repository at 'https://github.com/softerhardware/Hermes-Lite2' and find the 'software/hermeslite' folder to get the same files.  

In my case I use Windows Explorer to navigate to 'Downloads -> hermeslite -> hermeslight' so I see the following:

[![finding hermeslite.py file](Address-Management-Python/Capture01.PNG)](Address-Management-Python/Capture01.PNG)

To make things simple for now, let's copy (right click -> Copy) on the highlighted 'hermeslite' Python file and paste it onto the Desktop (move mouse to Desktop, right click -> Paste).

Later we can put the any/all of the contents in this folder into its own folder if we want.

##  Starting IDLE

Since we are going to work with this module interactively, click on the Start Menu then type 'idle' into the Search text input box at the bottom of the Start Menu.  This will bring up an entry 'IDLE (Python 3.8 64 bit)' which you should select.  IDLE is [Python's Integrated Development and Learning Environment](https://docs.python.org/3.8/library/idle.html).  If you want to learn more about IDLE you can use its 'File -> Help -> Idle' menu entry to bring up help text.

##  Starting the Hermes Lite Python Module under IDLE

Now that IDLE is running, you should use its 'File -> Open' menu to navigate to the Desktop and open the 'hermeslite' file from the Desktop:

[![starting hermeslite.py file](Address-Management-Python/Capture02.PNG)](Address-Management-Python/Capture02.PNG)

##  Troubleshooting an IP Address Assignment Issue using the Hermes Lite Python Module

As per the troubleshooting page, if the HL2 does not receive a DHCP reply in 15 seconds after power up it will flash FIXME LEDs.  

And in this case we expect it to fall back to APIPA mode and use IP address 169.254.19.221 with netmask 255.255.0.0.

So, let us configure the PC's Local Area Connection (LAN) to use address 169.254.1.1. with netmask 255.255.0.0, connect an Ethernet cable from the PC to the HL2 directly, and see if the hermeslight.py module can discover the HL2 when it is in this state.

This configuration corresponds to what one might do if one chooses to directly cable the HL2 to an unused Ethernet port on a PC and that PC is not running a DHCP server (which typically is true).

To do this configuration, select 'Start Menu -> Control Panel -> Network and Internet -> Network and Sharing Center' then select the Local Area Connection to bring up its Status.  This will let you select Properties.  

[![configure network](Address-Management-Python/Capture03.PNG)](Address-Management-Python/Capture03.PNG)

Then we use Properties to get to the screen to configure its Internet Protocol V4 Properties:

[![configure lan](Address-Management-Python/Capture04.PNG)](Address-Management-Python/Capture04.PNG)

As shown, we can configure the PC's Local Area Connection (LAN) to use address 169.254.1.1. with netmask 255.255.0.0 then hit 'OK' in the three dropdowns so the new address is put into use.

Now, in the IDLE window containing the hermeslite.py code we can use the 'Run -> Run Module' menu entry (or just use the F5 key) to see if the hermeslight.py module can discover the HL2 when it is in this state.

[![run hermeslite module](Address-Management-Python/Capture05.PNG)](Address-Management-Python/Capture05.PNG)

We see the output in the 'IDLE Shell' window has the output in blue:

    Discover response from 169.254.19.221:1025

This means the hermeslite.py module has discovered the device at the IP address we expected, 169.254.19.221

We can confirm this is true using 'Start Menu -> Command Prompt' to ping this address.

[![command output](Address-Management-Python/Capture08.PNG)](Address-Management-Python/Capture08.PNG)

Note also that the output of 'arp -a' shows:

    169.254.19.221        00-1c-c0-a2-13-dd     dynamic

This means that IP address '169.254.19.221' corresponds to MAC address '00-1c-c0-a2-13-dd' which was discovered dynamically.

##  Using IDLE Shell to issue Hermes Lite Python Module commands

We can use the 'IDLE Shell' to issue Hermes Lite Python Module commands. 

This is an example of what the output looks like:

[![run hermeslite commands ](Address-Management-Python/Capture06.PNG)](Address-Management-Python/Capture06.PNG)

Let's go through the output.

    Python 4.8.8 (tags/v3.8.8:024d805, Feb 19 2021, 13:18:16) [MSC v.1928 64 bit (AMD64)] on win32
    Type "help", "copyright", "credits" or "license()" for more information.
    >>> 
    ================= RESTART: C:\Users\Shaq\Desktop\hermeslite.py =================
    Discover response from 169.254.19.221:1025

This is the result of using the 'Run -> Run Module' menu entry (or just use the F5 key) in the hermeslite.py IDLE editor window , showing that the module discovered the HL2 even though there is no DHCP server present.

    >>> import hermeslite

This tells the IDLE Shell window that we want to use the hermeslight Python Module.

    >>> x = hermeslite.discover_first()
    Discover response from 169.254.19.221:1025

This creates an object known as 'x' that contains the result of calling the hermeslite.discover_first() method.  As you see, the output is the same as the output we got when we ran the module.  I chose the name 'x' randomly.

    >>> x.response()
    Retrying send
    Retrying send
    Response(type=2, mac='00:1c:c0:a2:13:dd', gateware='72.8', radio_id=6, use_eeprom_ip=False, use_eeprom_mac=False, favor_dhcp=False, eeprom_ip='0:0:0:0', eeprom_mac='00:1c:c0:a2:00:00', receivers=4, board_id=5, wideband_type=1, response_data=0, ext_cw_key=False, ptt_resp=False, pa_exttr=False, pa_inttr=False, tx_on=False, cw_on=False, adc_clip_cnt=3, temperature=28.077636718749996, fwd_pwr=2, rev_pwr=4, bias=0.0, txfifo_recovery=False, txfifo_msbs=0, rem=b'\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00')

This creates an Response object for the discovered HL2 and prints its representation to the screen.  If I reformat the output, you can see this Response object has a lot of interesting information in it:

    Response(type=2
      mac='00:1c:c0:a2:13:dd',
      gateware='72.8',
      radio_id=6,
      use_eeprom_ip=False,
      use_eeprom_mac=False,
      favor_dhcp=False,
      eeprom_ip='0:0:0:0',
      eeprom_mac='00:1c:c0:a2:00:00',
      receivers=4,
      board_id=5,
      wideband_type=1,
      response_data=0,
      ext_cw_key=False,
      ptt_resp=False,
      pa_exttr=False,
      pa_inttr=False,
      tx_on=False,
      cw_on=False,
      adc_clip_cnt=3,
      temperature=28.077636718749996,
      fwd_pwr=2,
      rev_pwr=4,
      bias=0.0,
      txfifo_recovery=False,
      txfifo_msbs=0,
      rem=b'\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00')

In particular, you can see 'use_eeprom_ip' is False, as is 'use_eeprom_mac'.  These are important fields for troubleshooting networking issues.  Ideally one should be able to use DHCP to set the IP address and one should be able to use the pre-programmed MAC address rather than using the one set into the EEPROM device, so they should be False.  There are cases it is helpful to store IP and MAC addresses in the EEPROM device, but they are the exception rather than the rule.

    >>> help(x.clear_use_eeprom_ip)
    Help on method clear_use_eeprom_ip in module hermeslite:
    
    clear_use_eeprom_ip() method of hermeslite.HermesLite instance
        Clear the use_eeprom_ip flag

This demonstrates how to print the help information for any field of the hermeslite discovery object.
  
    >>> x.clear_use_eeprom_ip()
    Retrying send
    Retrying send
    
This demonstrates how clear the 'use_eeprom_ip' boolean using the hermeslite discovery object.

##  How do you clear the flag that tells HL2 to use the IP Address stored in the EEPROM device?

Use the instructions above to install Python and the Hermes Lite Python Module.  Then, use IDLE to load the module, then run the following commands in the IDLE Shell Window:

    import hermeslite
    x = hermeslite.discover_first()
    x.clear_use_eeprom_ip()

Make sure the output of the 'discover_first()' method indicates a HL2 device has been discovered.  If not, follow the troubleshooting section of this wiki.

##  How do you clear the flag that tells HL2 to use the MAC Address stored in the EEPROM device?

Use the instructions above to install Python and the Hermes Lite Python Module.  Then, use IDLE to load the module, then run the following commands in the IDLE Shell Window:

    import hermeslite
    x = hermeslite.discover_first()
    x.clear_use_eeprom_mac()

Make sure the output of the 'discover_first()' method indicates a HL2 device has been discovered.  If not, follow the troubleshooting section of this wiki.

##  What is the full help output for the Hermes Lite Python Module Discovery Object?

Here it is, as of the time of this writing, March 2021.

    >>> help(x)
    Help on HermesLite in module hermeslite object:
    class HermesLite(builtins.object)
     |  HermesLite(ip_port)
     |  
     |  Methods defined here:
     |  
     |  __init__(self, ip_port)
     |      Initialize self.  See help(type(self)) for accurate signature.
     |  
     |  clear_favor_dhcp(self)
     |      Clear the favor_dhcp flag
     |  
     |  clear_use_eeprom_ip(self)
     |      Clear the use_eeprom_ip flag
     |  
     |  clear_use_eeprom_mac(self)
     |      Clear the use_eeprom_mac flag
     |  
     |  command(self, addr, cmd, sleep=0.2)
     |      Send command at address to HL2, cmd may be bytes or number.
     |      Returns a response.
     |  
     |  config_txbuffer(self, latency=10, ptt_hang=4)
     |      Set buffer latency and ptt hang time in ms.
     |  
     |  desynchronize_radios(self)
     |  
     |  disable_cl1(self)
     |      Stop using CL1 and revert to default xtal oscillator input
     |  
     |  disable_cl2(self)
     |      Disable CL2 clock output
     |  
     |  disable_txlna(self, gain=-12)
     |      Disable the hardware managed LNA for TX
     |  
     |  enable_ad9866_2xclk(self)
     |  
     |  enable_cl1_direct(self)
     |      Pass CL1 input directly with buffering to AD9866
     |  
     |  enable_cl1_pll1(self)
     |      Use CL1 as input to PLL1 and then to AD9866
     |  
     |  enable_cl2_61p44(self)
     |      Enable CL2 output at 61.44MH
     |  
     |  enable_cl2_copy_ad9866(self)
     |      Enable CL2 output, copy of clock to AD9866.
     |  
     |  enable_cl2_sync_76p8(self, iskw=0, fskw=31)
     |      Enable CL2 synchronous output at 76.8MHz
     |  
     |  enable_txlna(self, gain=-12)
     |      Set and enable the hardware managed LNA for TX
     |  
     |  get_eeprom_ip(self)
     |      Get fixed IP
     |  
     |  get_eeprom_mac(self)
     |      Read last two digits of alternate MAC
     |  
     |  get_favor_dhcp(self)
     |      Get the current favor_dhcp flag
     |  
     |  get_use_eeprom_ip(self)
     |      Get the current use_eeprom_ip flag
     |  
     |  get_use_eeprom_mac(self)
     |      Get the current use_eeprom_mac flag
     |  
     |  read_bias(self)
     |      Read configuration setting for bias0 and bias1
     |  
     |  read_eeprom(self, addr, fullresponse=False)
     |      Read values from the MCP4662 EEPROM registers
     |  
     |  read_versa5(self, addr, fullresponse=False)
     |      Read from Versa5 clock chip via i2c.
     |  
     |  reset_versa5(self)
     |      Force reset of both PLL counters to synchronize clocks
     |  
     |  response(self)
     |      Retrieve a response without sending address and command.
     |  
     |  set_cwhangtime(self, hangtime=10)
     |      Set CW hang time 0 to 1023 ms
     |  
     |  set_eeprom_ip(self, ip='0.0.0.0')
     |      Set fixed IP. ip is string like '192.168.33.1'
     |  
     |  set_eeprom_mac(self, mac='0:0')
     |      Set last two digits of alternate MAC. mac is hex string like 'bf:10'
     |  
     |  set_favor_dhcp(self)
     |      Set the favor_dhcp flag
     |  
     |  set_use_eeprom_ip(self)
     |      Set the use_eeprom_ip flag
     |  
     |  set_use_eeprom_mac(self)
     |      Set the use_eeprom_mac flag
     |  
     |  synchronize_radios(self)
     |  
     |  update_gateware(self, filename, filename_checks=True)
     |      Program gateware with .rbf file
     |  
     |  update_gateware_github(self, version='', delete=True)
     |      Update the gateware given a version string. Version set to 'stable/20200529_71p3/hl2b5up_main/hl2b5up_main.rbf'
     |      will update to that stable version.
     |  
     |  write_ad9866(self, addr, data)
     |      Write to AD9866 via SPI.
     |  
     |  write_eeprom(self, addr, data)
     |      Write values into the MCP4662 EEPROM registers
     |  
     |  write_versa5(self, addr, data)
     |      Write to Versa5 clock chip via i2c.
     |  
     |  ----------------------------------------------------------------------
     |  Data descriptors defined here:
     |  
     |  __dict__
     |      dictionary for instance variables (if defined)
     |  
     |  __weakref__
     |      list of weak references to the object (if defined)
    
