**WARNING - You may cause permanent damage to your HL2 if you misuse this control.**

Thetis has been updated to allow direct user control of the two I2C buses on the Hermes-Lite2. This control will allow changes to be made to the programmable clock generator, the digital POT controlling the PA bias, N2ADR filter board or any other device which may be added to the I2C buses in the future. It is possible that you may accidentally change the PA bias and destroy your devices if you don't know exactly what you are doing - you have been warned. This control's main purpose is to allow experimenters to change parameters in any of the I2C devices, particularly the programmable clock generator.   

# Setup|General|F/W Set

The control has been placed on the F/W Set panel in setup.

![](pictures/thetis/setup-general-firmware-i2c.jpg) 

The Radio buttons allow selection of the two different buses. The I2C 1 bus is used to control the programmable clock generator and the I2C 2 bus is used to configure the digital POT and N2ADR filter board.

The individual devices on each of the buses is selected using the I2C Address spin box. The normal address range for I2C devices is 7 bits but some manufactures specify it as an 8 bit address. The programmable clock generator is a case in point, it is specified as 0xd4. If the value of an address is greater than 0x7f, it will be right shifted by one to make it a 7 bit address. The bus and addresses for a standard HL2 with a N2ADR board are

* Bus 1, address 0xd4 - Programmable clock generator
* Bus 2, address 0x2c - Digital POT
* Bus 2, address 0x20 - N2ADR filter selection switch
* Bus 2, address 0x1D - I/O Board

Data locations within a device are selected via the Reg/Control spin boxes. They have been split into a upper and lower nibble for easier setup of some devices.

The Read button initiates a read and the results are displayed in the two boxes below the button. The right box displays the byte at the given address and the ones to the left, the data in the address+1, +2 & +3.

The write button has an enable tick box to avoid accidental writes. The data to be written is entered in the Write Data spin box and the operation is initiated with the Write button. There is no automatic read back of the written data, so it is recommended that any values written should be read back for verification. Sometimes how this is done is not as obvious as you would think - more on this later.  

# Programmable Clock Generator  

The programmable clock generator provides the main clocks for the HL2. It has a spare generator which can be used to generate a specify frequency and may be used for transverters or any other custom circuity requiring a clock. Details on the programming can be found from the data sheet but basic details can be found at [External Clocks](https://github.com/softerhardware/Hermes-Lite2/wiki/External-Clocks). The initial image above shows the control panel looking at register locations 0x17 and 0x18.

# Digital POT with NV Memory

The digital POT performs to two functions. It controls the bias of the PA FETs and provides a small amount of NV memory used to store IP and MAC addressing information. Changing values in this device could lead to PA FET damage or connection issues if the IP or MAC are changed. There is also flags which control preferences for DHCP or static IP addressing. The memory map can be found in the [Protocol](https://github.com/softerhardware/Hermes-Lite2/wiki/Protocol) page.

Reading and writing of this device is more complicated and reference should be made to the device's data sheet to understand how the Reg/Control is used [MCP4662](https://ww1.microchip.com/downloads/en/DeviceDoc/22107B.pdf)

![](pictures/thetis/setup-general-firmware-i2c-command-byte.jpg) 

For many devices the Reg/Control byte gives the address within the device that stores the data. The digital pot device is slightly different. The upper nibble is the address information but the lower nibble defines commands and extra data bits. The device can store 9 bits of data per address but that is not used in the HL2.

 ![](pictures/thetis/setup-general-firmware-i2c-nv.jpg) 

The above panel shows a read of address 0xd in the digital pot device. Notice that the lower nibble is set to 0xc (b1100), so specifying a read operation. Also notice that the data read is in the left-hand data window. This is due to the read data being 9 bits and the left window will show the lower 8 bits. 

 ![](pictures/thetis/setup-general-firmware-i2c-nv-write.jpg) 

The panel above shows a NV write. Notice that the lower nibble is 0x0 specifying a write and the data to be written is 0x02.

![](pictures/thetis/setup-general-firmware-i2c-nv-write-read.jpg) 

As previous mentioned, a read after a write is required to verify that the correct data had been written. Notice in the above panel that the lower nibble had to be changed back to 0xc (b1100) in order to read the data. This makes this device a little bit more awkward to work with.

There is no need to read/write the filter switch on the N2ADR boards, as it is already controlled in the Thetis software. In the future there may be additional companion boards developed using I2C devices. This control panel will allow developers to test new circuits and if the companion boards become mainstream, Thetis can be update to expose the new functionality.    



 