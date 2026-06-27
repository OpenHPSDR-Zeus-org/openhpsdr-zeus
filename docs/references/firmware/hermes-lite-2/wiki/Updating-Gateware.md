Gateware updates are announced on the [Google Groups](http://www.hermeslite.com) discussion forum.

Currently the Hermes-Lite 2 gateware can be updated with either
- Ethernet (Recommended)
- an Altera USB Blaster
- a Raspberry PI


## Ethernet

Gateware update via Ethernet is the recommended update procedure. Ethernet update uses the .rbf file format. Be sure to use the .rbf file that matches your HL2 board version. For example, most people will have a HL2build5 or later and should use hl2b5up_main.rbf. After a successful gateware update, the HL2 will automatically restart. This allows for full remote gateware updates.

Gateware update via ethernet is the only mechanism to write an application image to slot2. See [Gateware Factory and Application Images](#gateware-factory-and-application-images) for more details.

### SparkSDR

[SparkSDR](http://www.ihopper.org/radio/) is able to update the gateware over ethernet. When disconnected to the HL2, right click on the radio you wish to update as shown below and continue to select the new .rbf gateware image file.

[![sparksdr_gateware_update](pictures/sparksdr_gateware_update.png)](pictures/sparksdr_gateware_update.png)

### Quisk

[Quisk](https://james.ahlstrom.name/quisk/) is able to update the gateware over ethernet. Select config, radio and hardware as shown below. Next, select "Program from RBF file.." which will allow you to select the new .rbf gateware image file and update the HL2.

[![quisk_gateware_update](pictures/quisk_gateware_update.png)](pictures/quisk_gateware_update.png)


## Hermes Lite Python Module

The [Hermes-Lite Python Module](https://github.com/softerhardware/Hermes-Lite2/tree/master/software/hermeslite) can be used to update the gateware by those comfortable with software development and command line tools.

Once the Python environment and the Hermes Lite Python Module are installed (one example of doing this is [here](Address-Management-Python/Address-Management-Python), then you can use IDLE to load the module.  Then you can run the following commands in the IDLE Shell Window to install the **newest version** of gateware directly from GitHub:

    import hermeslite
    x = hermeslite.discover_first()
    x.update_gateware_github()

Make sure the output of the 'discover_first()' method indicates a HL2 device has been discovered.  If not, follow the troubleshooting section of this wiki.

If you prefer to load a specific version of gateware from GitHub, run the following commands in the IDLE Shell Window instead:

    import hermeslite
    x = hermeslite.discover_first()
    v = 'stable/20200529_71p3/hl2b5up_main/hl2b5up_main.rbf'
    x.update_gateware_github(v)

Note that the variable 'v' needs to be set to a file path relative to https://github.com/softerhardware/Hermes-Lite2/raw/master/gateware/bitfiles i.e. the 'bitfiles' folder in the master branch of the online HL2 github repo.


If you prefer to load a specific version of gateware from a file you've created or downloaded, run the following commands in the IDLE Shell Window instead:

    import hermeslite
    x = hermeslite.discover_first()
    f = 'hl2b5up_main/hl2b5up_main.rbf'
    x.update_gateware(f)

Note that the variable 'f' needs to be set to a file path relative to the current directory/folder containing a valid '.rbf' file.

Again, please make sure the output of the 'discover_first()' method indicates a HL2 device has been discovered. 

## USB Blaster Clone

This [Altera USB Blaster](https://www.ebay.com/itm/altera-Mini-Usb-Blaster-Cable-For-CPLD-FPGA-NIOS-JTAG-Altera-Programmer/200943750380) programming cable may be used to update the FPGA gateware. These are available from many sources found if one searches for [Altera USB Blaster](http://lmgtfy.com/?q=altera+usb+blaster). There are a variety of units, but the least expensive ones in the $3 to $7 range work fine. There are some issues with Windows 10 and STM-based models. Please see [this thread](https://groups.google.com/d/msg/hermes-lite/wCY4_vN7t8Y/D5_vkipYGgAJ) and [this thread](https://groups.google.com/d/msg/hermes-lite/g9vzzf84PGQ/g5xdpH5oAgAJ).

If using an Altera USB Blaster or clone with Quartus software to program the Cyclone IV EP4CE22 FPGA, please see the [HL2 Firmware Update]( https://youtu.be/5m2kUmod0yQ ) video. 

A genuine Altera USB Blaster Rev B dates 2004 has been reported to work too as well as later Rev C units.

Quartus Prime Version 14 and above are only compatible with 64 bit Windows. So if you are using 32 Bit windows then you will probably need to choose V13.1. If you do have 64 bit Windows, V18.1 Lite is the highest indicated for Cyclone IV E hardware on Windows. V19.1 Lite is the highest release number indicated for Linux (Windows V19.1 version "available soon"). 

To save disk space and download time, you do not need to download the entire Quartus Prime Suite. Instead, go to "Additional Software" -> "Stand-Alone Software" and get "Quartus Prime Programmer and Tools". 

Linux users will likely need to add the following udev rule (suggest putting it in /etc/udev/rules.d/55-altera-blaster.rules)(also be sure to add your user account to the plugdev group, if it isn't already):
SUBSYSTEM=="usb", ENV{DEVTYPE}=="usb_device", ATTRS{idVendor}=="09fb", ATTRS{idProduct}=="6001", MODE="0660", SYMLINK+="usbblaster", GROUP="plugdev"

Basic USB blaster usage will only write a factory image to slot1. You should also erase the entire EEPROM to remove any residual application images. See [Gateware Factory and Application Images](#gateware-factory-and-application-images) for more details.

## Raspberry Pi

Please see the [Raspberry Pi Test](Raspberry-Pi-Test-and-Program) page for details on how to program and test a Hermes-Lite 2.0 with only a Raspberry Pi.

Basic Raspberry Pi gateware programming will only write a factory image to slot1. See [Gateware Factory and Application Images](#gateware-factory-and-application-images) for more details.

## Gateware Factory and Application Images

The HL2 EEPROM can hold two full gateware images stored at slot1 and slot2. Prior to gateware release 20200329 or version 7.0, only slot1 was ever used. All methods of programming only wrote gateware images to slot1. This can lead to problems if a programming step fails as there is no working gateware image.

Starting with gateware release 20200329 or versions 7.0 and higher, the HL2 has a factory image stored in slot1 and an application image stored in slot2. Unlike openhpsdr radios, these two images are interchangeable for the HL2. The difference between factory or application image is only due to the slot they are stored in. When the HL2 is powered on, it first loads the factory image. The factory image then attempts to load the application image. If successful, the application image executes. If not successful, the backup factory image executes. This provides more robustness in cases where programming the application image fails.

Both the factory and application images are fully functional gateware installs. In fact, Makerfabs will only install the factory image. All subsequent user updates will write the application image in slot2.

For most users, once you update to release 20200329 (version 7.0) or later, and provided you don't later try to downgrade to an earlier gateware, updating over ethernet via SparkSDR or Quisk will be sufficient.

### Gateware Update Problems

If the ethernet update step fails, just power cycle the HL2 and attempt to update the gateware via ethernet again. You can detect update failure if the factory version executes, identified as 7.0 in most software.

If you upgrade your gateware to versions 7.0 or later, and then attempt to downgrade to a version earlier than 7.0, then the pre 7.0 application image in slot2 only knows how to write slot1. Future updates will overwrite slot1 but a residual application image will always remain. To be able to write to slot2, use the force factory boot as described below. Note that this is a mechanism to update the factory image: update to pre7.0, update to new factory, force factory boot. Hopefully update of the factory image will not be required, but if it becomes necessary, a simpler route will be provided.

### Force Factory Image Boot

To force factory image boot and prevent use of the application image, power on the HL2 with tip and ring of CN4 gounded. The tip and ring only need to be grounded for about 5 seconds during power on. This can be done with an audio cable and aluminum foil as shown below. Wrap the aluminum foil around the connector and squeeze during power on.

[![hl2_factory_boot](pictures/hl2_factory_boot.jpg)](pictures/hl2_factory_boot.jpg)
