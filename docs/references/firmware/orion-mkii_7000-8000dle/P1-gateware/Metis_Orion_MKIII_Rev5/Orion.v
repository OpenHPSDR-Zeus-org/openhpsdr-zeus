/***********************************************************
*
*	Orion
*
************************************************************/

//
//  HPSDR - High Performance Software Defined Radio
//
//  Orion code. 
//
//  This program is free software; you can redistribute it and/or modify
//  it under the terms of the GNU General Public License as published by
//  the Free Software Foundation; either version 2 of the License, or
//  (at your option) any later version.
//
//  This program is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//  GNU General Public License for more details.
//
//  You should have received a copy of the GNU General Public License
//  along with this program; if not, write to the Free Software
//  Foundation, Inc., 59 Temple Place, Suite 330, Boston, MA  02111-1307  USA

// (C) Phil Harman VK6APH, Kirk Weedman KD7IRS  2006, 2007, 2008, 2009, 2010, 2011, 2012, 2013 
// (C) Joe Martin K5SO 2015





/* 	This program interfaces the LTC2208 to a PC over Ethernet.
	The data from the LTC2208 is in 16 bit parallel format and 
	is valid at the positive edge of the LTC2208 122.88MHz clock.
	
	The data is processed by a CORDIC NCO to produce I and Q
	outputs.  These are decimated by 640/1280/2560 in CIC and CFIR filters to 
	give output data at 192/96/48kHz to feed to the PHY and hence via 
	the Ethernet to a PC.
	
	The program takes microphone/line-in samples from an ADC at 48kHz
	and passed them to the PC via Ethernet. The PC process these and returns them as 
	I&Q signals.  These are passed to CIC interpolating filters (x2560) then 
	to a complex input CORDIC NCO. The ouput of the CORDIC is at the required user
	frequency and passed to the final DAC.  
	
	The data format over Ethenet is the same as that used my Metis.
	
	Change log:

	 2  Mar  2012  - Released as V1.3
	 5  April      - Added wide spectrum support. Set serial numbers for Penny and Mercury to zero
	               - Added support for second receiver - Joe Martin, K5SO
	               - Released as V1.4
	13  April      - Fixed bug in TLV320 code that muted audio when Line-in select.
	               - J13 in selects Apollo and out Alex
	               - Released as V1.5
	14  April      - Added user input IO8
					   - Released as V1.6
	21  April      - Added support for Alex in auto mode
						- tidy LED designations
						- Designated V1.7
	28  April      - Increased Apollo clock from 30kHz to 150kHz	
	 5  July       - Fixed sync byte error in Apollo.v and MAC address read error in EEPROM.v
	               - Increased wide spectrum FIFO from 4k to 16k
	13             - Increased wide spectrum FIFO to 32k for testing by DL3HVH
	18             - Test code for Hermes VNA, set to one receiver
	                 In VNA mode set the Rx and Tx CORDIC phase words to be equal. Set the I input of the Tx CORDIC to
						  0 and the Q to 0x7FFF/1.7 to remove the CORDIC gain.  Run the VNA when the PTT from the PC is active.
	 6  Aug        - using CIC and CFIR outputs with I of Tx cordic set to 0 and Q to 0x7FFF/1.7	. See line 1307.	
   11  Sep        - Use C&C (when C0 = 0001 001x, C2[7]]) to enable VNA mode. 
   15  Sep        - Set wide spectrum FIFO to 16k.  Enabled second receiver.
					   - Alex/Apollo selected via C&C rather than J13 											
                  - released as V1.7
	23  Sep			- changes by Joe K5SO
							- Modified FilterSelect to match USB protocol document v1.42 spec for C2[5] when C0[7:1]=0001001
							- Added dual-Rx automatic Alex LPF/HPF filter switching logic 
							- Added additional Alex LPF filter switching logic during transmit to accommodate SPLT mode operation correctly
							- Modified Alex Tx RED LED operation to illuminate when transmitting
							- Modified HPF/LPF automatic frequency switch-point logic  
							- Added manual Alex switching logic
							- Added line-in gain control
						- Renamed version number to V1.8
	28 Oct			- changes by Joe K5SO
							- Added 0-31 dB step option for input attenuator
						- Renamed version number to V1.9
	27 Nov			- added TimeQuest Hermes.sdc timing constraint file 
						- commented out the Apollo module
						- implemented four receivers
						- renamed the version number to v4.5
						- reduced the number of receivers to two once again
						- added back the Apollo module
						- used a new TimeQuest .sdc file from Phil
						- fixed bug with automatic Alex filter selection
						- renamed the version number to v4.6
	29 Nov			- increased # of receivers to four
						- modified the Alex switching code
						- renamed version to v2.0
	6  Dec			- added 5 receivers, using Alex VE3NEA's rx modules
						- modified Alex automatic switching
	8  Dec			- fixed bug with Rx 5 operation
	14 December		- modified the receiver module to yield 6 dB greater overall gain, to match Mercury rx module gain
	17 Dec         - Modified Rx_MAC to use a FIFO to convert from nibble to byte.
					   - Enabled directed ARP rather than just broadcast
	30 Dec			- added Alex T/R relay disable option (C&C bit C3[7] when C0=0001_001x, 0=T/R relay enabled, 1=T/R relay disabled)
						- added abilty to set/read IP address without being in Bootloader mode.
						- now using Quartus II V12.1
						- released as v2.1
	8 Jan 2013		- fixed ethernet ARP request response bug
					   - modified Apollo code so PTT timer works
						- changed FSM from 1 Hot to User Encoded so Apollo code works.
						- Changed Apollo PLL clock from 150kHz to 30kHz.
						- released as v2.2
	26             - Replaced FIR with Polyphase FIR. Modified variCIC to decimate from 2...40. Increased max sampling rate to 960kHz
	               - Reduced receivers to 4 with sampling rates of 48/96/192/384ksps.
						- Added UDP/IP set IP address
	10             - Modified Polyphase filter. 
						- Increased time out for ARP and ping
						- 4 receivers with 48/96/192/384ksps.
	12             - 5 receivers - 96% full
	16					- released as version 2.3
	2 Mar          - Added ARP/Ping time out mod from Metis.
						- replace pin defs ready for 1000T code - released as same version
	7 May				- fixed Alex 6m preamp switching bug
						- reduced the Alex SPI bus speed by half to permit longer ribbon cable connections to Alex, 
						- assigned unused Rx freqs to the Tx freq,
						- changed the 1.5MHz HPF filter switchover freq to 1416 KHz and the 80m LPF switchover freq
						  to 2400 KHz to accommodate "stitched" mode rx at up to 384 ksps sampling rates,
						- changed version number to 2.4
	27 May			- temporary predistortion version - assigned Tx output to Rx5 input - changed version to v0.4
	 1 Nov         - Move DAC data to Rx 2 for testing. Carrier +18dBm, noise floor approx -125dBm - works well. 
	               - Replace CIC with cFIR and CIC from HiQSDR. Works OK, need to swap I&Q. 92% full.
	18 Nov         - Swap I&Q. OK
	               - Move DAC data to Rx5. All OK
	20             - Try by2/by4 on Rx4 only. All OK
	               - Try by2/by4 on Rx5 as well. No
	21             - Edit sdc file to remove frequency path. OK now 
	25             - Edit sdc file to set DACD outputs with -9. Not sure this is better (Warren reports OK), perhaps use posedge of clock on DACD.
	26             - Use positive edge of clock on DACD and -2 in sdc file.  Rx1 jumps for Warren
	 2 Dec         - Revert to negeative edge and -9. Meets timing. Reduce gain of by4 FIR buy 7.2dB to match other FIR gain. All OK
	 4             - Revert By4 gain to unity and increase other FIR to unity.  Reduce peak frequency error by making error symetrical.
	 6             - Increase cFIR to 1024 coefficients. 
	 9             - Alternative cFIR with same gain as previously.
	11             - Corrected cFIR address counter. Works OK.
	12             - Test Warrens CFIR coefficients. Reduce by4 gain by 7.2dB.
	13             - Testing truncation on output data of interpolating FIR.  No effect.
	18             - Enabled VNA features from Hermes V2.5 development and select Rx5 on Rx 
**********************
	27 Dec 2013		- ported Hermes design to Angelia hardware...K5SO
						- increased number of receivers to 7
						- added support for dual ADCs
						- added support for independent attenuator control for inputs to ADC1 & ADC2
						- set version number to v2.1
    8 Jan 2014	   - Moved design to EP4CGX150DF31C7
                  - Added support for Mic Signal, Bias, PTT switching (Joe, K5SO's code)
						- Added support for Automatic 10Mhz Clock switching (Joe, K5SO's code)
						- Added support for ADC3 and attenuator (Joe, K5SO's code)
						- Set version number to v1.0
	 9 Jan 2014    - Changed APR & Ping requests clock to Tx_Clock; vesion number changed to v1.1
	 27 Jan 2014	- Changed FPGA image code location in the EEPROM to the 2MB position instead of 1MB to 
						  accommodate the larger size of the Orion bootloader code
						- Changed version number to v1.2
	 7 Feb 2014	   - fixed bug with erase size in the ASMI_interface module
						- Changed version number to v1.3
	 3 Mar 2014		- Constrained all clocks, IO ports, and IO paths via constraints in the Orion.sdc file, meets timing 100%
						- Changed version number to v1.4
	 4 Mar 2014		- Changed ADC assignements to allow for easier testing of the daughterboard:
							RX1->ADC0
							RX2->ADC1
							RX3->ADC2
							RX4->ADC0
							RX5->ADC0...switched to Tx DAC on Tx
							RX6->ADC0
							RX7->ADC0
						- Changed version number to v1.5
	 4 Mar 2014		- Added bootloader mode option switching to pin 25 on J43 of the daughterboard header, OR'd with SW1
						- Added ethernet switching 1000T/100T function to jumper J14 (no jumper = 1000T, jumper = 100T)...1000T ops not implemented yet
						- Added option to assign ADCs to the receiver modules via the C&C byte stream as follows: 
							when C0 = 0001_100x, 
							C1[1:0] = assign ADCn to RX1: 00 = ADC0, 01 = ADC1, 10 = ADC2
							C1[3:2] = assign ADCn to RX2: 00 = ADC0, 01 = ADC1, 10 = ADC2
							C1[5:4] = assign ADCn to RX3: 00 = ADC0, 01 = ADC1, 10 = ADC2
							C1[7:6] = assign ADCn to RX4: 00 = ADC0, 01 = ADC1, 10 = ADC2
							C2[1:0] = assign ADCn to RX5: 00 = ADC0, 01 = ADC1, 10 = ADC2, except on Tx assign Tx DAC as input to RX5
							C2[3:2] = assign ADCn to RX6: 00 = ADC0, 01 = ADC1, 10 = ADC2
							C2[5:4] = assign ADCn to RX7: 00 = ADC0, 01 = ADC1, 10 = ADC2
						- Changed version number to v1.6
	  5 Mar 2014	- Changed the C0 C&C byte used for ADC selection, as follows: 
							when C0 = 0001_110x, 
							C1[1:0] = assign ADCn to RX1: 00 = ADC0, 01 = ADC1, 10 = ADC2
							C1[3:2] = assign ADCn to RX2: 00 = ADC0, 01 = ADC1, 10 = ADC2
							C1[5:4] = assign ADCn to RX3: 00 = ADC0, 01 = ADC1, 10 = ADC2
							C1[7:6] = assign ADCn to RX4: 00 = ADC0, 01 = ADC1, 10 = ADC2
							C2[1:0] = assign ADCn to RX5: 00 = ADC0, 01 = ADC1, 10 = ADC2, except on Tx assign Tx DAC as input to RX5
							C2[3:2] = assign ADCn to RX6: 00 = ADC0, 01 = ADC1, 10 = ADC2
							C2[5:4] = assign ADCn to RX7: 00 = ADC0, 01 = ADC1, 10 = ADC2
						- Changed version number to v1.7
	  12 Mar 2014  - Chanaged legacy references of "Angelia" to "Orion" in the Verilog variable names and in the Orion.sdc 
						   constraint file
						- Created a new output_file.pof with new bootloader code (SW17 implemented) and Orion_v1.8
						- Changed version number to v1.8
	  13 Apr 2014	- Fixed bug with PureSignal implementation, timing
						- Changed M3 value for proper rounding of frequency phase word value
						- Changed version number to v1.9
		28 Apr 2014 - Added FPGA CW code and implemented C1[6] when C0 = 0001_010x for Orion PTT disable (enable= 0, disable = 1)
						- Changed version number to 2.0
						
			May 2014	- versions 2.1 and 2.2 are gigabit testing versions (disregard these two versions)
			
		17 May 2014 - Added Iambic keyer. Enable keying using I[1:0] mapped to dot:dash so that CWX can use the keyer.
						- Changed version number to v2.3
		6 Jun 2014	- Added PC control of "atten_on_Tx" via C&C bits C3[4:0] when C0 = 0001_110x
					   - Changed version number to v2.4
		14 Jun 2014 - Added ADC overflow for ADC2 and ADC3 in TXFC module	
					   - Changed version number to v2.5
		2 Jul 2014	- Modified timing constraints to improve overall stability of the firmware design
						- Changed version number to v2.6
		31 Aug 2014 - Fixed PTT bug that prevented keying of an external amp via accy PTT input and improper
							non-break-in CW PTT operation
						- Change version number to v2.7
		11 Nov 2014 - Moved C122_Alex_data into SPI_clk domain
						- Changed version number to v2.8						
		14 Dec 2014 - Fixed intermittent PTT to Alex/PA board problems by sending Alex/PA relay data three times
						  via the SPI bus when the Alex data changes, ensuring positive relay control to all 
						  Alex/PA relays
						- Changed version number to v2.9
		16 Jan 2015 - Changed back to sending Alex/ANAN PA data once each time Alex data is changed
						- Added de-bounced PTT signal to FPGA_PTT generation code to fix intermittent Alex
						  T/R relay operation
						- Changed version number to v3.0
		 6 Feb 2015 - Ported Angelia_v4.7 to Orion as Orion_v3.1
						  -- including
								- set Alex relays to off at power on
								- merged clocking, CW generation and I2S audio from new protocol code
								- temp disable Apollo interface
								- clock TX DAC data at 90 degrees to ensure data is stable
						- Changed version number to v3.1
						
		8 Feb 2015  - Fixed Orion_micPTT_disable bug
						- Fixed Line-In bug
		13 Feb 2015 - Fixed ext 10MHz ref input bug
						- Changed to clock TX DAC data at 30 degrees instead of 90 degrees phase shift
						- Changed version number to v3.2
		14 Feb 2015 - Removed clock phase delay on TX DAC data transfers entirely, using C122_clk. 
						- Changed version number to v3.3
		27 Feb 2015	- Changed switch point for LPF filter to switch to 12/10m filter if selected freq
		              is greater than 22000000Hz, to obtain rated 100W power out on 12m for ANAN-200D
						- Increased the amount of EEPROM that is erased to 6MB from the previous 4MB
						- Changed version number to v3.4
		 3 Mar 2015 - Created a 90-degree phase shifted clock to clock data into the TX DAC, to ensure 
							TX DAC data is stable before clocking it into the TX DAC
						- Increased the amount of EEPROM that gets erased upon a new firmware upload from
						  4MB to 6MB in ASMI_interface.v
						- Changed version number to v3.5
		25 Apr 2015 - Fixed Line-In bug in TLV320_SPI.v
						- Changed version number to v3.6
	   4  May 2015 - Added external CW keying capability to iambic.v module via digital input IO4 
						  while iambic CW mode is selected (pin 9 on J16 Orion, pin 9 rear panel accy 
						  jack on ANAN-200D), key to ground, unkey is +3.3VDC via pull up resistor on Orion board,
						  IO4 input is debounced 
						- Changed version number to v3.7
	   23 May 2015 - Test version for 16bit DAC v9.0
		21 Jun 2015 - Changed temp_DACD to use the 15-bit C122_cordic_i_out for its high bits
						- Changed DACD to be the 16-bit offset binary format conversion of the signed 15-bit 
						  C122_cordic_i_out to drive the MAX5891 Tx DAC data inputs
						- Changed version number to v9.1
		23 Jun 2015	- Fixed power out bug by using a better conversion method to obtain the offset binary
						  format for DACD
						- Still have Tx a spur at +/- 424 KHz at approx -60dBc, working on removing that
						- Changed version number to v9.2
		25 Jun 2015 - Doubled power output but still have 424KHz spurs above and below f0
						- Changed version number to v9.3
		13 Nov 2016	- Added support for the 2nd BPF switching for the RX2 path
						- Replaced SPI.v with Orion Mk II version that supports 48-bit SPI data word
						- Added support for external 10MHz reference input
						- Added support for manual selection of RX2 filters/etc for C0=0010_010x C&C command
						- Changed version number to v9.4
		17 Nov 2016	- Fixed auto Alex data word instability that kept the SPI bus continuously running
						- Forced the LPF filter switching according to C122_frequency_HZ_Tx selection criteria in both Rx and Tx mode
						- Added code to SPI.v to send the Alex data word out twice whenever the dtat word changes
						- Changed version number to v9.5
		18 Nov 2016	- Fixed auto 6m preamp switching problem
						- Rearranged auto Alex max/min frequency sorting code for better readability
						- Changed hardware ID assignment in Tx_MAC.v line 1059 to "10" for Orion MkII ID
						- Changed version number to v9.6						
		28 Nov 2016	- Reduced the clock freq for the Alex SPI module from 3.072MHz to 1.536MHz in the PLL 
							megafunction for CBCLK to improve SPI bus stability						
						- Modified auto Alex filter select switch frequencies in HPF_select.v and BPF2_select.v to
							be appropriate for the MkII PA/filter board Rx BPFs
						- Modified sorting scheme in Orion.v for determining which freqs are passed to HPF_select.v and BPF2_select 
							in Alex auto mode
						- Modified SPI.v to send Alex data only once each time the Alex data word changes
						- Closed timing 
						- Changed version number to v9.7
		5 Dec 2016	- Fixed BPF2.v and Orion.v auto Alex freq switching bugs for BPF5 and LNA preamp selection
						- Fixed RX2_GROUND bug in Orion.v
						- Added CTRL_TRSW to Alex data word (bit 34) to support 8000DLE front panel LCD
						- Added firmware control of PA bias to enable only when in Tx mode, off in Rx mode
						- Changed version number to v9.8
		6 Dec 2016	- Changed auto Alex switch point freq for BPF4 to 21.000MHz
						- Fixed 12M BPF switching (manual and auto) in HFP_select.v, BPF2_select.v, and Orion.v
						- Fixed high baseline noise on RX1 by changing phase shift from 90 degrees to 60 degrees on C122_PLL/.c1 output
						- Closed timing
						- Changed version number to v9.9
						- Defined CTRL_TRSW as output 
- - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - -
		7 Dec 2016	- Constrained the new CTRL_TRSW output in Orion.sdc to close timing
						- Changed version number from v9.9 to v1.0, to form the initial ANAN-8000DLE firmware release
- - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - -

2017 	  	Jan 30	- port of Orion_MkII_v1.0 (Quartus II v13.1) to Quaruts Prime Lite v16.0
						- regenerated all megafunctions using v16.0 megafunctions
						- modified automatic-mode switch points in HPF_select.v and BPF2_select.v to:
							...
							if 		(frequency <  1500000) 	HPF <= 7'b0100000;   // bypass
							else if	(frequency <  2100000) 	HPF <= 7'b0010000;	// RX BPF1 160M	
							else if 	(frequency <  5500000)	HPF <= 7'b0001000;	// RX BPF2 80M/60M
							else if 	(frequency < 11000000)	HPF <= 7'b0000100;	// RX BPF3 40/30M
							else if 	(frequency < 22000000)	HPF <= 7'b0000001;	// RX BPF4 20M/17M
							else if 	(frequency < 35000000) 	HPF <= 7'b0000010; 	// RX BPF5 15/12M
							else 										HPF <= 7'b1000000;	// LNA, active above 27MHz
							...
						- removed C10_PLL
						- changed C122_PLL/.c0 output to 10.000MHz
						- changed 122.88 MHz module lock XOR feedback to operate at 10MHz vs 80KHz
						- added C122_PLL_SHIFT to obtain a phase shifted 122.88MHz clock for DACD (TxDAC) generation
						- replaced ASMI constraints in Angelia.sdc using the v16.0 AMSI path versions to constrain 
							the new I/O ports/paths
						- set the PLL_IF outputs to:
								PLL_IF/.c0 = 48 MHz
								PLL_IF/.c1 = 12.288 MHz
								PLL_IF/.c2 = 3.072 MHz 90 deg phase shift
								PLL_IF/.c3 = 48 KHz
						- set C122_SHIFT_PLL/.c0 = 122.88 MHz with 15 deg phase shift
						- removed all max/min delay timing constraints in Angelia.sdc, compiled
						- closed timing, re-compiled
						- removed clean_PTT_in as an input to FPGA_PTT to prevent PTT timing problems with software
						- changed Alex SPI.v to send ALex data once for each time the Alex data word changes
						- changed version number to v1.1
						- recompiled, closed timing
- - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - -
			Feb 22	- port to Quartus Prime Lite v16.1
						- upgraded all megafunctions to v16.1 megafunctions
						- changed ADC0 attenuator assignment logic to fix PureSignal auto-attenuate bug
						- removed ADC3 daughterboard references/outputs
						- changed version number to v1.2
						- retimed, compiled iteratively until timing closed
			Mar 14	- fixed XVTR mode bug as follows:
							- assigned TXRX_STATUS to FPGA_PTT and to bit 34 of the 48-bit Alex SPI data word
							- assigned CTRL_TRSW to depend on both FPGA_PTT and C&C C2[1] bit (XVTR_Enable) when C0=0010_010x is set
						- changed FW version number to v1.3
						- removed all max/min delay constraints from Orion.sdc
						- retimed/compiled iteratively until timing closed
			Mar 17	- modified SPI.v to send the Alex SPI data word out twice each time the data word changes, 
						- changed FW version number to v1.4
						- removed all max/min delay constraints from Orion.sdc
						- retimed/compiled iteratively until timing closed						
		   Mar 23	- changed user digital IO connections as follows:
								IO5 = TX INHIBIT, pin 16 of J16, rear 8000DLE panel DIG IN 1
								IO8 = External CW Key, pin 13 of J16, 8000DLE rear panel DIG IN 2
						- changed iambic.v to set IO8 as ext CW key input
						- added debounce to IO5 and IO8
						- changed C122_SHIFT/.c0 phase angle to 11.25 degrees
						- changed output_delay for DACD[*] paths to 1.2 nSec in Orion.sdc
						- changed FW version number to v1.5
						- removed all max/min delay constraints from Orion.sdc
						- retimed/compiled iteratively until timing closed	
			Apr 14	- added peak detection code for AIN1 (FWD_PWR) and AIN2 (REV_PWR):
								increased clock speed for the Orion_ADC.v module x10 to provide more FWD_PWR, REV_PWR readings per unit time:
								created PLL_30MHz/.c0 to generate a 30.72MHz clock for the Orion_ADC module, results in 7.68MHz SCLK for the ADC
								added peak detect for AIN1 and AIN2 in Orion_ADC.v
								added pk_detect_reset and pk_detect_ack to define peak detection interval based upon the C&C AIN1/2 byte update rate, 
									changes to Orion_Tx_fifo_ctl.v, Orion_ADC.v, and Orion.v
						- changed FW version to v1.6
						- closed timing
			Jul 15   - Added support for Tx Attenuator. If TX_ATTEN_SELECT (K22) is low then uses Tx attenuator, if high then
						  uses DAC current only to control Tx power. 
						  Note that I/O pin K22 is assigned a weak pull-up so high if external resistor is not fitted to pull low. 
						  The pull-up is added using the Assignment Editor, add a new line with: 
						 
							TO						Assignment Name 			Value 	Enabled 
						   TX_ATTEN_SELECT 	Weak Pull-Up Resistor 	 On		  Yes
						  
						- changed FW version to v1.7
						- Added Tx Attenuator I/O to sdc file. 
						- Added ROMs for Tx Attenuator (Tx_Atten) and PWM (Tx_DAC) values.
					     See 'Tx Power Control Using Attenuator.xlsx' for method of calculating values. 
						- BUILT WITH V16.0 ******
						
			Sep 11	- replaced tx_atten.mif and tx_dac.mif to fix drive level problem
						-changed FW version to v1.8
						- compiled using V16.1
			Oct 23	- added C122_Rx_1_in to control bit 11 of the Alex control word to support 7000DLE
						- set ADC2 attenuator (attenuator2) to max value of 31 (5'b1_1111) during transmit to support 7000DLE
						- added C122_6m_auto_preamp and C122_6m_auto_preamp2 to disable 6m preamps when in tx mode when Alex 
							settings are under automatic firmware control
						- changed FW version number to v1.9
						- re-timed and re-compiled
			Nov 10   - added bias_ctrl to control pin 10 (BUFF_OUT) of J16  via U20 and FPGA pin AH30 (BUFF_OUF_FPGA) and
							set it low to turn on PA bias on the 7000DLE
						- assigned FPGA pin AH30 to bias_ctrl
						- changed FW version number to v2.0
						- recompiled using Quartus Prime Lite v16.1
						
			Nov 17   - changed FW version number to v2.1
						- removed all set_max_delay constraints from Orion.sdc, recompiled, added
						  new set_max_delay constraints as needed						
						- recompiled using Quartus Prime lite v16.1
						
			Nov 28   - replaced Orion_Tx_fifo_ctrl.v with file from v1.7 to fix Rx6/Rx7 bug
						- modified Rx2 freq switching to implement common_Merc_freq option in C&C commands
						- modified Rx5 freq switching to allow software adjustment of freq when PureSignal is inactive
						- changed FW version to v2.2
						- retimed, recompiled
						
2018		Mar 21   - recompiled using Quartus Prime Lite V17.0
						- Moved HPF start point from 1.5MHz to 1.8MHz to cover more of MW band
						- Fixed bug in iambic.v that corrupted speed change whilst character being sent.
						- changed FW version to V2.3
						
2018		Mar 30	- modified C122_HPF switching by adding C122_HPF_PRE and 
						  switching C122_LPF to BYPASS on TX for PureSignal support
						- changed firmware version to v2.3
						- retimed, compiled
						
		Apr 15	- changed 6m LNA logic to make it active when rx frequency is greater than 21 MHz
						- changed firmware version to v2.4
						- compiled, retimed, compiled

2018		Apr 19   - (N1GP) Fixed a bug with SPI.v not sending twice correctly.
2018		May 19   - (N1GP) Updated ASMI.v and Orion.v for ECPQ128A using J14 to distinguish from ECPS128
2018		Dec 2    - (N1GP) Changed 6m LNA logic back to make it active when rx frequency is greater than 35 MHz as in v2.3
						- NOTE: comments in bpf2_select.v and HPF_select.v said 27 MHz but was really 35 MHz
						- changed firmware version to v2.5
						
2019		Jan 19 	- (VK6PH) change clock for Rx Attenuator from CMCLK (12.288MHz) to  CBCLK(3.072MHz)
						    since new version of DAT-31A-SP+ attenuator chip is not reliable at faster clock speed.
						- Changed FW version number to v2.6
			 
			 May 4	- (K5SO) replaced Orion.sdc timing constraint file with Orion.sdc from v2.3 and edited as follows:
						- removed all Set_Max_Delay and Set_Min_Delay constraints in the Orion.sdc file
						- removed previous ASMI I/O constraints and added constraints for the "unconstrained" ASMI I/O paths
						- applied a false_path constraint to MODE2 paths
						- changed firmware version number to v2.7
						- re-timed design
					 

						
*** change global clock name **** 
  

NOTES: 

	LEDs:  
	
	DEBUG_LED1  	- Lights when an Ethernet broadcast is detected
	DEBUG_LED2  	- Lights when traffic to the boards MAC address is detected
	DEBUG_LED3  	- Lights when detect a received sequence error or ASMI is busy
	DEBUG_LED4 		- Displays state of PHY negotiations - fast flash if no Ethernet connection, slow flash if 100T and on if 1000T
	DEBUG_LED5		- Lights when the PHY receives Ethernet traffic
	DEBUG_LED6  	- Lights when the PHY transmits Ethernet traffic
	DEBUG_LED7  	- Displays state of DHCP negotiations or static IP - on if ACK, slow flash if NAK, fast flash if time out 
					     and long then short flash if static IP
	DEBUG_LED8  	- Lights when sync (0x7F7F7F) received from PC
	DEBUG_LED9  	- Lights when a Metis discovery packet is received
	DEBUG_LED10 	- Lights when a Metis discovery packet reply is sent
	
	Status_LED	    - Flashes once per second
	

*/

//---------------------------------------------------------
//              Quartus V11.1sp2 Notes
//---------------------------------------------------------

/*
	In order to get this code to compile without timing errors under
	Quartus V11.1sp2 I needed to use the following settings:
	
	- Timing Analysis Settings - Use Classic 
	- Analysis and Synthesis Settings\Power Up Dont Care [not checked]
	- Analysis and Synthesis Settings\Restructure Multiplexers  [OFF]
	- Analysis and Synthesis Settings\State Machine Processing = User Encoding
	- Fitter Settings\Optimise fast-corner timing [ON]
	- Restructure Multiplexers = OFF
	- Perform Physical Synthesis for Combinational Logic for Performance = ON
	- Perform Register Duplication for Performance = ON
	- Perform Register Retiming for Performance =  ON
	
*/
	

module Orion(
	//clock afc
  input _122MHz,                 //122.88MHz from VCXO
  input  OSC_10MHZ,              //10MHz reference in 
  input  EXT_OSC_10MHZ, 
  output FPGA_PLL,               //122.88MHz VCXO contol voltage

  //attenuator (DAT-31-SP+)
  output ATTN_DATA,              //data for input attenuator
  output ATTN_DATA_2,
  output ATTN_CLK,               //clock for input attenuator
  output ATTN_CLK_2,
  output ATTN_LE,                //Latch enable for input attenuator
  output ATTN_LE_2,

  //rx adc (LTC2208)
  input  [15:0]INA,              //samples from LTC2208
  input  [15:0]INA_2,            //samples from LTC2208 #2
  input  LTC2208_122MHz,         //122.88MHz from LTC2208_122MHz pin 
  input  LTC2208_122MHz_2,       //122.88MHz from #2 LTC2208_122MHz pin 
  input  OVERFLOW,               //high indicates LTC2208 have overflow
  input  OVERFLOW_2,             //high indicates LTC2208 have overflow
  output RAND,            //high turns ramdom on
  output RAND_2,          //high turns ramdom on
  output PGA,            //high turns LTC2208 internal preamp on
  output PGA_2,          //high turns LTC2208 internal preamp on
  output DITH,            //high turns LTC2208 dither on 
  output DITH_2,          //high turns LTC2208 dither on
  output SHDN,            //x shuts LTC2208 off
  output SHDN_2,          //x shuts LTC2208 off

  //tx adc (AD9744ARU)
   output reg  DAC_ALC,           //sets Tx DAC output level
  output reg signed [15:0]DACD,   //16-bit offset binary format value for Tx DAC data bus 
  
  //audio codec (TLV320AIC23B)
  output CBCLK,               
  output CLRCIN, 
  output CLRCOUT,
  output CDIN,                   
  output CMCLK,                  //Master Clock to TLV320 
  output CMODE,                  //sets TLV320 mode - I2C or SPI
  output nCS,                    //chip select on TLV320
  output MOSI,                   //SPI data for TLV320
  output SSCK,                   //SPI clock for TLV320
  input  CDOUT,                  //Mic data from TLV320  
  
  //phy rgmii (KSZ9021RL)
  output [3:0]PHY_TX,
  output PHY_TX_EN,              //PHY Tx enable
  output PHY_TX_CLOCK,           //PHY Tx data clock
  input  [3:0]PHY_RX,     
  input  RX_DV,                  //PHY has data flag
  input  PHY_RX_CLOCK,           //PHY Rx data clock
  input  PHY_CLK125,             //125MHz clock from PHY PLL
  input  PHY_INT_N,              //interrupt (n.c.)
  output PHY_RESET_N,
  input  CLK_25MHZ,              //25MHz clock (n.c.)  
  
	//phy mdio (KSZ9021RL)
	inout  PHY_MDIO,               //data line to PHY MDIO
	output PHY_MDC,                //2.5MHz clock to PHY MDIO
  
	//eeprom (25AA02E48T-I/OT)
	output 	SCK, 							// clock on MAC EEPROM
	output 	SI,							// serial in on MAC EEPROM
	input   	SO, 							// SO on MAC EEPROM
	output  	CS,							// CS on MAC EEPROM
	
  //eeprom (M25P16VMW6G)  
  output NCONFIG,                //when high causes FPGA to reload from eeprom EPCS128	
  
  //12 bit adc's (ADC78H90CIMT)
  output ADCMOSI,                
  output ADCCLK,
  input  ADCMISO,
  output nADCCS, 
  
  //Tx Attenuator (F1912)
  output TX_ATTN_LE,					// High for parallel mode
  output TX_ATTN_CLK,				// not used in parallel mode  
  output TX_ATTN_DATA,				// not used in parallel mode
  output [5:0] TX_ATTEN,		   // [0] = bit 0, [1] = bit 1, [2] = bit 4, [3] = bit 8 etc.
  output TX_ATTN_MODE,				// Low for parallel mode 
  input	TX_ATTEN_SELECT,			// Low for Tx attenuator, high for DAC current
 
  //alex/apollo spi
  output SPI_SDO,                //SPI data to Alex or Apollo 
  input  SPI_SDI,                //SPI data from Apollo 
  output SPI_SCK,                //SPI clock to Alex or Apollo 
  output J15_5,                  //SPI Rx data load strobe to Alex / Apollo enable
  output J15_6,                  //SPI Tx data load strobe to Alex / Apollo ~reset 
  
  //mic and osc configuration 
  output DRIVER_PA_EN, 
  output MICBIAS_ENABLE, 
  output PTT_SELECT, 
  output MIC_SIG_SELECT, 
  output MICBIAS_SELECT,
  output CTRL_TRSW,
  
  //misc. i/o
  input  PTT,                    //PTT active low
  input  KEY_DOT,                //dot input from J11
  input  KEY_DASH,               //dash input from J11
  output FPGA_PTT,               //high turns Q4 on for PTTOUT
  input  MODE2,                  //jumper J14 on Orion: 1 if removed = ECPS128; 0 if jumpered = ECPQ128A
  input  ANT_TUNE,               //atu
  output IO1,                    //high to mute AF amp    
  input  IO2,                    //PTT, used by Apollo
  input  SW1,							//bootloader mode switch option
  input  SW2,							//bootloader mode switch option (located on daughterboard), OR'd with SW1
  output bias_ctrl,					//controls pin 10 of J16 (BUFF_OUT) via U20 and FPGA pin AH30 (BUFF_OUT_FPGA) for 7000DLE support
  
  //user digital inputs
  input  IO4,                    
  input  IO5,
  input  IO6,
  input  IO8,
  
  //user outputs
  output USEROUT0,               
  output USEROUT1,
  output USEROUT2,
  output USEROUT3,
  output USEROUT4,
  output USEROUT5,
  output USEROUT6,
  
    //debug led's
  output Status_LED,      
  output DEBUG_LED1,             
  output DEBUG_LED2,
  output DEBUG_LED3,
  output DEBUG_LED4,
  output DEBUG_LED5,
  output DEBUG_LED6,
  output DEBUG_LED7,
  output DEBUG_LED8,
  output DEBUG_LED9,
  output DEBUG_LED10,
   
	// RAM
  output wire RAM_A0,
  output wire RAM_A1,
  output wire RAM_A2,
  output wire RAM_A3,
  output wire RAM_A4,
  output wire RAM_A5,
  output wire RAM_A6,
  output wire RAM_A7,
  output wire RAM_A8,
  output wire RAM_A9,
  output wire RAM_A10,
  output wire RAM_A11,
  output wire RAM_A12,
  output wire RAM_A13  
);

assign USEROUT0 = IF_OC[0];					
assign USEROUT1 = IF_OC[1];  				
assign USEROUT2 = IF_OC[2]; 					
assign USEROUT3 = IF_OC[3]; 		
assign USEROUT4 = IF_OC[4];
assign USEROUT5 = IF_OC[5];
assign USEROUT6 = IF_OC[6];

assign RAND = IF_RAND;
assign RAND_2 = IF_RAND;
assign PGA = 0;								// 1 = gain of 3dB, 0 = gain of 0dB
assign PGA_2 = 0;
assign DITH = IF_DITHER;
assign DITH_2 = IF_DITHER;
assign SHDN = 1'b0;				   		// normal LTC2208 operation
assign SHDN_2 = 1'b0;

assign bias_ctrl = 1'b0;					// low turns on bias for PA on 7000DLE

assign NCONFIG = IP_write_done || reset_FPGA;

// enable AF Amp
assign  IO1 = 1'b0;  						// low to enable, high to mute


// initially disable RF AMP 
initial DRIVER_PA_EN = 1'b0;

parameter M_TPD   = 4;
parameter IF_TPD  = 2;

parameter  Orion_version = 8'd27;		// Serial number of this version
localparam Penny_serialno = 8'd00;		// Use same value as equ1valent Penny code 
localparam Merc_serialno = 8'd00;		// Use same value as equivalent Mercury code

localparam RX_FIFO_SZ  = 4096; 			// 16 by 4096 deep RX FIFO
localparam TX_FIFO_SZ  = 1024; 			// 16 by 1024 deep TX FIFO  
localparam SP_FIFO_SZ  = 16384; 			// 16 by 16,384 deep SP FIFO

localparam read_reg_address = 5'd31; 	// PHY register to read from - gives connect speed and fully duplex	


//--------------------------------------------------------------
// Reset Lines - C122_rst, IF_rst, SPI_Alex_reset
//--------------------------------------------------------------

wire  IF_rst;
wire SPI_Alex_rst;
	
assign IF_rst 	 = (!IF_locked || reset);		// hold code in reset until PLLs are locked & PHY operational

assign PHY_RESET_N = 1'b1;  						// Allow PYH to run for now

// transfer IF_rst to 122.88MHz clock domain to generate C122_rst
cdc_sync #(1)
	reset_C122 (.siga(IF_rst), .rstb(IF_rst), .clkb(C122_clk), .sigb(C122_rst)); // 122.88MHz clock domain reset
	
cdc_sync #(1)
	reset_Alex (.siga(IF_rst), .rstb(IF_rst), .clkb(CBCLK), .sigb(SPI_Alex_rst));  // SPI_clk domain reset
	
//---------------------------------------------------------
//		CLOCKS
//---------------------------------------------------------

wire C122_clk = LTC2208_122MHz;
wire C122_clk_2 = LTC2208_122MHz_2;
wire IF_clk;
wire CLRCLK;
assign CLRCIN  = CLRCLK;
assign CLRCOUT = CLRCLK;

wire	Apollo_clk;
wire 	IF_locked;
wire  C122_cbrise;

//wire userADC_clk;

// Generate IF_clk (48MHz), CMCLK (12.288MHz), CBCLK(3.072MHz) and CLRCLK (48kHz) from 122.88MHz using PLL
// NOTE: CBCLK is generated at 90 degress 
PLL_IF PLL_IF_inst (.inclk0(C122_clk), .c0(IF_clk), .c1(CMCLK), .c2(CBCLK),  .c3(CLRCLK), .locked(IF_locked));

PLL_30MHz PLL_30MHz_inst (.inclk0(_122MHz),.c0(DACD_clock)); // generates 30.720 MHz clock for Orion_ADC module

pulsegen pulse  (.sig(CBCLK), .rst(IF_rst), .clk(!CMCLK), .pulse(C122_cbrise));  // pulse on rising edge of BCLK for Rx/Tx frequency calculations

//----------------------------PHY Clocks-------------------

wire C125_clk; 	assign C125_clk = PHY_CLK125;	// use PHY 125MHz clock for system clock
wire Tx_clock;
wire Tx_clock_2;
wire C125_locked; 										// high when PLL locked
wire PHY_data_clock;
wire PHY_speed;											// 0 = 100T, 1 = 1000T
wire EEPROM_clock;										// 2.5MHz

// use PLL to generate 2.5MHz, 25MHz and 12.5MHz from 125MHz
// C0 = 2.5MHz, C1 = 25MHz, C2 = 12.5MHz

PLL_clocks PLL_clocks_inst(.areset(), .inclk0(C125_clk), .c0(EEPROM_clock), .c1(Tx_clock), .c2(Tx_clock_2), .locked(C125_locked));

assign PHY_TX_CLOCK = ~Tx_clock;

assign PHY_speed = 1'b0;		// high for 1000T, low for 100T; force 100T for now

// select data clock speed based on JP2 and speed that network is running at
// assign PHY_data_clock = (PHY_speed & speed_1000T) ? PHY_RX_CLOCK : PHY_RX_CLOCK_2;

// generate PHY_RX_CLOCK/2 for 100T 
reg PHY_RX_CLOCK_2;
always @ (posedge PHY_RX_CLOCK) PHY_RX_CLOCK_2 <= ~PHY_RX_CLOCK_2; 

// force 100T for now 
assign PHY_data_clock = PHY_RX_CLOCK_2;

//------------------------------------------------------------
//  Reset and initialisation
//------------------------------------------------------------

/* 
	Hold the code in reset whilst we do the following:
	
	Get the boards MAC address from the EEPROM.
	
	Then setup the PHY registers and read from the PHY until it indicates it has 
	negotiated a speed.  Read connection speed and that we are running full duplex.
	
	LED0 incates PHY status - fast flash if no Ethernet connection, slow flash if 100T and on if 1000T
	
	Then wait a second (for the network to stabilise) then  attempt to obtain an IP address using DHCP
	- supplied address is in YIADDR.  If the DHCP request either times out, or results in a NAK, retry four 
	additional times with a 2 second delay between each retry.
	
	If after the retries a DHCP assigned IP address is not available use an APIPA IP address or an assigned one
	from Flash.
	
	Inhibit replying to a Metis Discovery request until an IP address has been applied.
	
	LED6 indicates the result of DHCP - on if ACK, slow flash if NAK, fast flash if time out and 
	long then short flash if static IP
	
	Once an IP address has been assigned set IP_valid flag. When set enables a response to a Discovery request.
	
	Wait for a Metis discovery frame - once received enable HPSDR data to PC.
	
	Enable rest of code.
	
*/

reg reset;
reg [4:0]start_up;
reg [47:0]This_MAC; 			// holds the MAC address of this Metis board
reg read_MAC; 
wire MAC_ready;
reg DHCP_start;
reg [24:0]delay;
reg duplex;						// set when we are connected full duplex
reg speed_100T;				// set when we are connected at 100MHz
reg speed_1000T;				// set when we are connected at 1GHz
reg Tx_reset;					// when set prevents HPSDR UDP/IP Tx data being sent
reg [2:0]DHCP_retries;		// DHCP retry counter
reg IP_valid;					// set when Metis has a valid IP address assigned by DHCP or APIPA
reg Assigned_IP_valid;		// set if IP address assigned by PC is not 0.0.0.0. or 255.255.255.255
reg use_IPIPA;					// set when no DHCP or assigned IP available so use APIAP
reg read_IP_address;			// set when we wish to read IP address from EEPROM


always @ (posedge Tx_clock_2)
begin
	case (start_up)
	// get the MAC address for this board
0:	begin 
		IP_valid <= 1'b0;							// clear IP valid flag
		Assigned_IP_valid <= 1'b0;				// clear IP in flash memory valid
		reset <= 1'b1;
		Tx_reset <= 1'b1;							// prevent I&Q data Tx until all initialised 
		read_MAC <= 1'b1;
		use_IPIPA <= 0;							// clear IPIPA flag
		start_up <= start_up + 1'b1;
	end
	// wait until we have read the EEPROM then the IP address
1:  begin
		if (MAC_ready) begin 					// MAC_ready goes high when EEPROM read
			read_MAC <= 0;
			read_IP_address <= 1'b1;						// set read IP flag
			start_up <= start_up + 1'b1;
		end
		else start_up <= 1'b1;
	end
	// read the IP address from EEPROM then set up the PHY
2:	begin
		if (IP_ready) begin
			read_IP_address <= 0;
    		write_PHY <= 1'b1;					// set write to PHY flag
			start_up <= start_up + 1'b1;
		end
		else start_up <= 2;    
    end			
	// check the IP address read from the flash memory is valid. Set up the PHY MDIO registers
3: begin
	   if (AssignIP != 0 && AssignIP != 32'hFF_FF_FF_FF)
			Assigned_IP_valid <= 1'b1;	
	   if (write_done) begin
			write_PHY <= 0;						// clear write PHY flag so it does not run again
			duplex <= 0;							// clear duplex and speed flags
			speed_100T <= 0;
			speed_1000T <= 0; 
			read_PHY <= 1'b1;						// set read from PHY flag
			start_up <= start_up + 1'b1;
		end 
		else start_up <= 3;						// loop here till write is done
	end 
	
	// loop reading PHY Register 31 bits [3],[5] & [6] to determine if final connection is full duplex at 100T or 1000T.
	// Set speed and duplex bits.
	// If an IP address has been assigned (i.e. != 0) then continue else	
	// once connected delay 1 second before trying DHCP to give network time to stabilise.
4: begin
		if (read_done  && (register_data[5] || register_data[6])) begin
			duplex <= register_data[3];			// get connection status and speed
			speed_100T  <= register_data[5];
			speed_1000T <= register_data[6];
			read_PHY <= 0;								// clear read PHY flag so it does not run again	
			reset <= 0;	
			if (duplex) begin							// loop here is not fully duplex network connection
				// if an IP address has been assigned then skip DHCP etc
				if (Assigned_IP_valid) start_up <= 6;
				// allow rest of code to run now so we can get IP address. If 						
				else if (delay == 12500000) begin	// delay 1 second so that PHY is ready for DHCP transaction
					DHCP_start <= 1'b1;					// start DHCP process
					if (time_out)							// loop until the DHCP module has cleared its time_out flag
						start_up <= 4;
					else begin
						delay <= 0;							// reset delay for DHCP retries
						start_up <= start_up + 1'b1;
					end 
				end 
				else delay <= delay + 1'b1;
			end 
		end 
		else start_up <= 4;								// keep reading Register 1 until we have a valid speed and full duplex		
   end 

	// get an IP address from the DHCP server, move to next state if successful, retry 3 times if NAK or time out.		
5:  begin 
		DHCP_start <= 0;
		if (DHCP_ACK) 										// have DHCP assigned IP address so continue
			start_up <= start_up + 1'b1;
		else if (DHCP_NAK || time_out) begin		// try again 3 more times with 1 second delay between attempts
			if (DHCP_retries == 3) begin				// no more DHCP retries so use IPIPA address and  continue
				use_IPIPA <= 1'b1;
				start_up <= start_up + 1'b1;
			end
			else begin
				DHCP_retries <= DHCP_retries + 1'b1;	// try DHCP again
				start_up <= 4;
			end	
		end		
		else start_up <= 5;
	end
	
	// Have a valid IP address and a full duplex PHY connection so enable Tx code 
6:  begin
	IP_valid <= 1'b1;					// we now have a valid IP address so can respond to Discovery requests etc
	Tx_reset <= 0;						// release reset so UDP/IP Tx code can run
	start_up <= start_up + 1'b1;						
	read_PHY <= 1'b1;					// set read from PHY flag
	end
	// loop checking we still have a Network connection by reading speed from PHY registers - restart if network connection lost
7:	begin
		if (read_done) begin 
			read_PHY <= 0;
			if (register_data[5] || register_data[6])
				start_up <= 6;								// network connection OK
			else start_up <= 0;							// lost network connection so re-start
		end 
	end
	default: start_up <= 0;
    endcase
end 

//----------------------------------------------------------------------------------
// read and write to the EEPROM	(NOTE: Max clock frequency is 20MHz)
//----------------------------------------------------------------------------------
wire IP_ready;
wire write_IP;
				
EEPROM EEPROM_inst(.clock(EEPROM_clock), .read_MAC(read_MAC), .read_IP(read_IP_address), .write_IP(write_IP), 
				   .IP_to_write(IP_to_write), .CS(CS), .SCK(SCK), .SI(SI), .SO(SO), .This_MAC(This_MAC),
				   .This_IP(AssignIP), .MAC_ready(MAC_ready), .IP_ready(IP_ready), .IP_write_done(IP_write_done));				
					
					
//------------------------------------------------------------------------------------
//  If DHCP provides an IP address for Metis use that else use a random APIPA address
//------------------------------------------------------------------------------------

// Use an APIPA address of 169.254.(last two bytes of the MAC address)

wire [31:0] This_IP;
wire [31:0]AssignIP;			// IP address read from EEPROM

assign This_IP =  Assigned_IP_valid ? AssignIP : 
				              use_IPIPA ? {8'd169, 8'd254, This_MAC[15:0]} : YIADDR;

//----------------------------------------------------------------------------------
// Read/Write the  PHY MDIO registers (NOTE: Max clock frequency is 2.5MHz)
//----------------------------------------------------------------------------------
wire write_done; 
reg write_PHY;
reg read_PHY;
wire PHY_clock;
wire read_done;
wire [15:0]register_data; 
wire PHY_MDIO_clk;
assign PHY_MDIO_clk = EEPROM_clock;

MDIO MDIO_inst(.clk(PHY_MDIO_clk), .write_PHY(write_PHY), .write_done(write_done), .read_PHY(read_PHY),
	  .clock(PHY_MDC), .MDIO_inout(PHY_MDIO), .read_done(read_done),
	  .read_reg_address(read_reg_address), .register_data(register_data),.speed(PHY_speed));

//----------------------------------------------------------------------------------
//  Renew the DHCP supplied IP address at half the lease period
//----------------------------------------------------------------------------------

/*
	Request a DHCP IP address at IP_lease/2 seconds if we have a valid DHCP assigned IP address.
	The IP_lease is obtained from the DHCP server and returned during the DHCP ACK.
	This is the number of seconds that the IP lease is valid. 
	
	Divide this value by 2 then multiply by the clock rate to give the delay time.
	
	If an IP_lease time of zero is received then the lease time is set to 24 days.
*/

wire [51:0]lease_time;
assign lease_time = (IP_lease == 0) ?  52'h7735_8C8C_A6C0 : (IP_lease >> 1) * 12500000; // 24 days if no lease time given
// assign lease_time = (IP_lease == 0) ? 52'h7735_8C8C_A6C0  : (52'd4 * 52'd12500000);  // every 4 seconds for testing


reg [24:0]IP_delay;
reg DHCP_renew;
reg [3:0]renew_DHCP_retries;
reg [51:0]renew_counter;
reg [24:0]renew_timer; 
reg [2:0]renew;
reg printf;
reg DHCP_request_renew;
reg second_time;						// set if can't get a DHCP IP address after two tries.
reg DHCP_discover_broadcast;    // last ditch attempt so do a discovery broadcast

always @(posedge Tx_clock_2)
begin 
case (renew)

0:	begin 
	renew_timer <= 0;
		if (DHCP_ACK) begin							 // only run if we have a  valid DHCP supplied IP address
			if (renew_counter == lease_time )begin
				renew_counter <= 0;
				renew <= renew + 1'b1;
			end
			else renew_counter <= renew_counter + 1'b1;
		end 
		else renew <= 0;
	end 
// Renew DHCP IP address
1:	begin
		if (second_time) 
			renew <= 4;
		else begin 
			DHCP_request_renew <= 1'b1;
			renew <= renew + 1'b1;
		end 
	end

// delay so the request is seen then return
2:	renew <= renew + 1'b1;

 
// get an IP address from the DHCP server, move to next state if successful, if not reset lease timer to 1/4 previous value
3: begin
	DHCP_request_renew <= 0;
		if (renew_timer != 2 * 12500000) begin  // delay for 2 seconds before we look for ACK, NAK or time_out
			renew_timer <= renew_timer + 1'b1;
			renew <= 3;
		end 		
		else begin
			if (DHCP_NAK || time_out) begin		// did not renew so set timer to lease_time/4
				second_time <= 1'b1;
				renew_counter = (lease_time - lease_time >> 4);  // i.e. 0.75 * lease_time
				renew <= 0;
			end
			else begin	
				renew_counter <= 0; 					// did renew so reset counter and continue.
				renew <= 0;
			end 
		end
	end 

// have not got an IP address the second time we tryed so use a broadcast and loop here
4:	begin 
	DHCP_discover_broadcast <= 1'b1;				// do a DHCP discovery
	renew <= renew + 1'b1;
	end 
	
// if we get a DHCP_ACK then continue else give up 
5:	begin
	DHCP_discover_broadcast <= 0;
		if (renew_timer != 2 * 12500000) begin  // delay for 2 seconds before we look for ACK, NAK or time_out
			renew_timer <= renew_timer + 1'b1;
			renew <= 5;
		end 
		else if (DHCP_NAK || time_out) 			// did not renew so give up
				renew <= 5;
		else begin 										// did renew so continue
			second_time <= 0;
			renew <= 0;
		end 
	end 	
default: renew <= 0;
endcase
end 

//----------------------------------------------------------------------------------
//  See if we can get an IP address using DHCP
//----------------------------------------------------------------------------------

wire time_out;
wire DHCP_request;

DHCP DHCP_inst(Tx_clock_2, (DHCP_start || DHCP_discover_broadcast), DHCP_renew, DHCP_discover , DHCP_offer, time_out, DHCP_request, DHCP_ACK);

//---------------------------------------------------------
// 		Set up TLV320 using SPI 
//---------------------------------------------------------
	
TLV320_SPI TLV (.clk(CMCLK), .CMODE(CMODE), .nCS(nCS), .MOSI(MOSI), .SSCK(SSCK), .boost(IF_Mic_boost), .line(IF_Line_In), .line_in_gain(IF_Line_In_Gain));

//-----------------------------------------------------
//   Rx_MAC - PHY Receive Interface  
//-----------------------------------------------------

wire [7:0]ping_data[0:59];
wire [15:0]Port;
wire [15:0]Discovery_Port;		// PC port doing a Discovery
wire broadcast;
wire ARP_request;
wire ping_request;
wire Rx_enable;
wire this_MAC;  					// set when packet addressed to this MAC
wire DHCP_offer; 					// set when we get a valid DHCP_offer
wire [31:0]YIADDR;				// DHCP supplied IP address for this board
wire [31:0]DHCP_IP;  			// IP address of DHCP server offering IP address 
wire DHCP_ACK, DHCP_NAK;
wire [31:0]PC_IP;					// IP address of the PC we are connecting to
wire [31:0]Discovery_IP;		// IP address of the PC doing a Discovery
wire [47:0]PC_MAC;				// MAC address of the PC we are connecting to
wire [47:0]Discovery_MAC;		// MAC address of the PC doing a Discovery
wire [31:0]Use_IP;				// Assigned IP address, if zero then use DHCP
wire METIS_discovery;			// pulse high when Metis_discovery received
wire [47:0]ARP_PC_MAC; 			// MAC address of PC requesting ARP
wire [31:0]ARP_PC_IP;			// IP address of PC requesting ARP
wire [47:0]Ping_PC_MAC; 		// MAC address of PC requesting ping
wire [31:0]Ping_PC_IP;			// IP address of PC requesting ping
wire [15:0]Length;				// Lenght of frame - used by ping
wire data_match;					// for debug use 
wire PHY_100T_state;				// used as system clock at 100T
wire [7:0] Rx_fifo_data;		// byte from PHY to send to Rx_fifo
wire rs232_write_strobe;
wire seq_error;					// set when we receive a sequence error
wire run;							// set to send data to PC
wire wide_spectrum;				// set to send wide spectrum data
wire [31:0]IP_lease;				// holds IP lease in seconds from DHCP ACK packet
wire [47:0]DHCP_MAC;				// MAC address of DHCP server 
wire erase;							// set when we receive an erase EPCS16 command
wire erase_ACK;					// set when ASMI interface acks the erase command
wire [31:0]num_blocks;			// number of 256 byte blocks to save in EPCS16
wire EPCS_FIFO_enable;			// EPCS fifo write enable
wire IP_write_done;
wire [31:0] IP_to_write;



Rx_MAC Rx_MAC_inst (.PHY_RX_CLOCK(PHY_RX_CLOCK), .PHY_data_clock(PHY_data_clock),.RX_DV(RX_DV), .PHY_RX(PHY_RX),
			        .broadcast(broadcast), .ARP_request(ARP_request), .ping_request(ping_request),  
			        .Rx_enable(Rx_enable), .this_MAC(this_MAC), .Rx_fifo_data(Rx_fifo_data), .ping_data(ping_data),
			        .DHCP_offer(DHCP_offer),
			        .This_MAC(This_MAC), .YIADDR(YIADDR), .DHCP_ACK(DHCP_ACK), .DHCP_NAK(DHCP_NAK),
			        .METIS_discovery(METIS_discovery), .METIS_discover_sent(METIS_discover_sent), .PC_IP(PC_IP), .PC_MAC(PC_MAC),
			        .This_IP(This_IP), .Length(Length), .PHY_100T_state(PHY_100T_state),
			        .ARP_PC_MAC(ARP_PC_MAC), .ARP_PC_IP(ARP_PC_IP), .Ping_PC_MAC(Ping_PC_MAC), 
			        .Ping_PC_IP(Ping_PC_IP), .Port(Port), .seq_error(seq_error), .data_match(data_match),
			        .run(run), .IP_lease(IP_lease), .DHCP_IP(DHCP_IP), .DHCP_MAC(DHCP_MAC),
			        .erase(erase), .erase_ACK(erase_ACK), .num_blocks(num_blocks), .EPCS_FIFO_enable(EPCS_FIFO_enable),
			        .wide_spectrum(wide_spectrum), .IP_write_done(IP_write_done), .write_IP(write_IP),
					  .IP_to_write(IP_to_write) 
			        );
			        


//-----------------------------------------------------
//   Tx_MAC - PHY Transmit Interface  
//-----------------------------------------------------

wire [10:0] PHY_Tx_rdused;  
wire LED;
wire Tx_fifo_rdreq;
wire ARP_sent;
wire  DHCP_discover;
reg  [7:0] RS232_data;
reg  RS232_Tx;
wire DHCP_request_sent;
wire DHCP_discover_sent;
wire METIS_discover_sent;
wire Tx_CTL;
wire [3:0]TD;


Tx_MAC Tx_MAC_inst (.Tx_clock(Tx_clock), .Tx_clock_2(Tx_clock_2), .IF_rst(IF_rst),
					.Send_ARP(Send_ARP),.ping_reply(ping_reply),.PHY_Tx_data(PHY_Tx_data),
					.PHY_Tx_rdused(PHY_Tx_rdused), .ping_data(ping_data), .LED(LED),
					.Tx_fifo_rdreq(Tx_fifo_rdreq),.Tx_CTL(PHY_TX_EN), .ARP_sent(ARP_sent),
					.ping_sent(ping_sent), .TD(PHY_TX),.DHCP_request(DHCP_request),
					.DHCP_discover_sent(DHCP_discover_sent), .This_MAC(This_MAC),
					.DHCP_discover(DHCP_discover), .DHCP_IP(DHCP_IP), .DHCP_request_sent(DHCP_request_sent),
					.METIS_discovery(METIS_discovery), .PC_IP(PC_IP), .PC_MAC(PC_MAC), .Length(Length),
			        .Port(Port), .This_IP(This_IP), .METIS_discover_sent(METIS_discover_sent),
			        .ARP_PC_MAC(ARP_PC_MAC), .ARP_PC_IP(ARP_PC_IP), .Ping_PC_IP(Ping_PC_IP),
			        .Ping_PC_MAC(Ping_PC_MAC), .speed_100T(1'b1), .Tx_reset(Tx_reset),
			        .run(run), .IP_valid(IP_valid), .printf(printf), .IP_lease(IP_lease),
			        .DHCP_MAC(DHCP_MAC), .DHCP_request_renew(DHCP_request_renew),
			        .erase_done(erase_done), .erase_done_ACK(erase_done_ACK), .send_more(send_more),
			        .send_more_ACK(send_more_ACK), .Orion_version(Orion_version),
			        .sp_fifo_rddata(sp_fifo_rddata), .sp_fifo_rdreq(sp_fifo_rdreq), 
			        .sp_fifo_rdused(), .wide_spectrum(wide_spectrum), .have_sp_data(sp_data_ready),
					  .AssignIP(AssignIP)
			        ); 

//------------------------ sequence ARP and Ping requests -----------------------------------

reg Send_ARP;
reg ping_reply;
reg ping_sent;
reg [16:0]times_up;			// time out counter so code wont hang here
reg [1:0] state;

parameter IDLE = 2'd0,
			  ARP = 2'd1,
			 PING = 2'd2;

//always @ (posedge PHY_RX_CLOCK)
always @ (posedge Tx_clock)
begin
	case (state)
	IDLE: begin
				times_up   <= 0;
				Send_ARP   <= 0;
				ping_reply <= 0;
				if (ARP_request) state <= ARP;
				else if (ping_request) state <= PING;
			end
	
	ARP:	begin	
				Send_ARP <= 1'b1;
				if (ARP_sent || times_up > 100000) state <= IDLE;
				times_up <= times_up + 17'd1;
			end
			
	PING:	begin
				ping_reply <= 1'b1;	
				if (ping_sent || times_up > 100000) state <= IDLE;
				times_up <= times_up + 17'd1;
			end 

	default: state = IDLE;
	endcase
end



//----------------------------------------------------
//   Receive PHY FIFO 
//----------------------------------------------------

/*
					    PHY_Rx_fifo (16k bytes) 
					
						---------------------
	  Rx_fifo_data |data[7:0]	  wrfull | PHY_wrfull ----> Flash LED!
						|				         |
		Rx_enable	|wrreq				   |
						|					      |									    
	PHY_data_clock	|>wrclk	 			   |
						---------------------								
  IF_PHY_drdy     |rdreq		  q[15:0]| IF_PHY_data [swap Endian] 
					   |					      |					  			
			       	|   		     rdempty| IF_PHY_rdempty 
			         |                    | 							
			 IF_clk	|>rdclk rdusedw[12:0]| 		    
					   ---------------------								
					   |                    |
			 IF_rst  |aclr                |								
					   ---------------------								
 
 NOTE: the rdempty stays asserted until enough words have been written to the input port to fill an entire word on the 
 output port. Hence 4 writes must take place for this to happen. 
 Also, rdusedw indicates how many 16 bit samples are available to be read. 
 
*/

wire PHY_wrfull;
wire IF_PHY_rdempty;
wire IF_PHY_drdy;


PHY_Rx_fifo PHY_Rx_fifo_inst(.wrclk (PHY_data_clock),.rdreq (IF_PHY_drdy),.rdclk (IF_clk),.wrreq(Rx_enable),
                .data (Rx_fifo_data),.q ({IF_PHY_data[7:0],IF_PHY_data[15:8]}), .rdempty(IF_PHY_rdempty),
                .wrfull(PHY_wrfull),.aclr(IF_rst | PHY_wrfull));


					 
					 
//------------------------------------------------
//   SP_fifo  (16384 words) dual clock FIFO
//------------------------------------------------

/*
        The spectrum data FIFO is 16 by 16384 words long on the input.
        Output is in Bytes for easy interface to the PHY code
        NB: The output flags are only valid after a read/write clock has taken place

       
							   SP_fifo
						---------------------
	      temp_ADC |data[15:0]	   wrfull| sp_fifo_wrfull
						|				         |
	sp_fifo_wrreq	|wrreq	     wrempty| sp_fifo_wrempty
						|				         |
			C122_clk	|>wrclk              | 
						---------------------
	sp_fifo_rdreq	|rdreq		   q[7:0]| sp_fifo_rddata
						|                    | 
						|				         |
		Tx_clock_2	|>rdclk		         | 
						|		               | 
						---------------------
						|                    |
	 C122_rst OR   |aclr                |
		!run   	   |                    |
	    				---------------------
		
*/

wire  sp_fifo_rdreq;
wire [7:0]sp_fifo_rddata;
wire sp_fifo_wrempty;
wire sp_fifo_wrfull;
wire sp_fifo_wrreq;


//--------------------------------------------------
//   Wideband Spectrum Data 
//--------------------------------------------------

//	When wide_spectrum is set and sp_fifo_wrempty then fill fifo with 16k words 
// of consecutive ADC samples.  Pass have_sp_data to Tx_MAC to indicate that 
// data is available.
// Reset fifo when !run so the data always starts at a known state.


wire have_sp_data;


SP_fifo  SPF (.aclr(C122_rst | !run), .wrclk (C122_clk), .rdclk(Tx_clock_2), 
             .wrreq (sp_fifo_wrreq), .data (temp_ADC[0]), .rdreq (sp_fifo_rdreq),
             .q(sp_fifo_rddata), .wrfull(sp_fifo_wrfull), .wrempty(sp_fifo_wrempty)); 					 
					 
					 
sp_rcv_ctrl SPC (.clk(C122_clk), .reset(C122_rst), .sp_fifo_wrempty(sp_fifo_wrempty),
                 .sp_fifo_wrfull(sp_fifo_wrfull), .write(sp_fifo_wrreq), .have_sp_data(have_sp_data));	
				 
// the wideband data is presented too fast for the PC to swallow so slow down to 12500/4096 = 3kHz
// use a counter and when zero enable the wide spectrum data

reg [15:0]sp_delay;   
wire sp_data_ready;

always @ (posedge Tx_clock_2)
		sp_delay <= sp_delay + 15'd1;
		
assign sp_data_ready = (sp_delay == 0 && have_sp_data); 


	
//--------------------------------------------------------------------------
//			EPCS16 Erase and Program code 
//--------------------------------------------------------------------------

/*
					    EPCS_fifo (1k bytes) 
					
					    ---------------------
	  Rx_fifo_data  |data[7:0]	         | 
					    |				         |
 EPCS_FIFO_enable  |wrreq		         | 
					    |					      |									    
	PHY_data_clock  |>wrclk	 			   |
					    ---------------------								
	   EPCS_rdreq   |rdreq		  q[7:0] | EPCS_data
					    |					      |					  			
			     	    |   		            |  
			          |                   | 							
         Tx_clock  |>rdclk rdusedw[9:0]| EPCS_Rx_used	    
					    ---------------------								
					    |                    |
			  IF_rst  |aclr                |								
					    ---------------------						
*/

wire [7:0]EPCS_data;
wire [9:0]EPCS_Rx_used;
wire  EPCS_rdreq;

EPCS_fifo EPCS_fifo_inst(.wrclk (PHY_data_clock),.rdreq (EPCS_rdreq),.rdclk (Tx_clock),.wrreq(EPCS_FIFO_enable), 
                .data (Rx_fifo_data),.q (EPCS_data), .rdusedw(EPCS_Rx_used), .aclr(IF_rst));

//----------------------------
// 			ASMI Interface
//----------------------------
wire busy;
wire erase_done;
wire send_more;
wire erase_done_ACK;
wire send_more_ACK;
wire reset_FPGA;

ASMI_interface  ASMI_int_inst(.clock(Tx_clock), .busy(busy), .erase(erase), .erase_ACK(erase_ACK), .IF_PHY_data(EPCS_data), .EPCS_flash(MODE2),
							 .IF_Rx_used(EPCS_Rx_used), .rdreq(EPCS_rdreq), .erase_done(erase_done), .num_blocks(num_blocks),
							 .erase_done_ACK(erase_done_ACK), .send_more(send_more), .send_more_ACK(send_more_ACK), .NCONFIG(reset_FPGA)); 
							 
//--------------------------------------------------------------------------------------------
//  	Iambic CW Keyer
//--------------------------------------------------------------------------------------------

wire keyout;
wire dot, dash, CWX;
reg iambic;					// 0 = straight key/bug mode, 1 = iambic CW keyer mode
reg keyer_mode;			// 0 = iambic CW keyer mode A, 1 = iamic CW keyer mode B

assign dot  = (IF_I_PWM[2] & internal_CW);
assign dash = (IF_I_PWM[1] & internal_CW);
assign  CWX = (IF_I_PWM[0] & internal_CW);
// parameter is clock speed in kHz.

iambic #(48) iambic_inst (.clock(CLRCLK), .cw_speed(keyer_speed), .iambic(iambic), .keyer_mode(keyer_mode), .weight(keyer_weight), 
                          .letter_space(keyer_spacing), .dot_key(!KEY_DOT | dot), .dash_key(!KEY_DASH | dash),
								  .CWX(CWX), .paddle_swap(key_reverse), .keyer_out(keyout), .IO8(clean_IO8));
						  
//--------------------------------------------------------------------------------------------
//  	Calculate  Raised Cosine profile for sidetone and CW envelope when internal CW selected 
//--------------------------------------------------------------------------------------------

wire CW_char;
assign CW_char = (keyout & internal_CW & run);		// set if running, internal_CW is enabled and either CW key is active
wire [15:0] CW_RF;
wire [15:0] profile;
wire CW_PTT;
profile profile_sidetone (.clock(CLRCLK), .CW_char(CW_char), .profile(profile),  .delay(8'd0));
profile profile_CW       (.clock(CLRCLK), .CW_char(CW_char), .profile(CW_RF),    .delay(RF_delay), .hang(hang), .PTT(CW_PTT));

//--------------------------------------------------------
//			Generate CW sidetone with raised cosine profile
//--------------------------------------------------------	
wire signed [15:0] prof_sidetone;
sidetone sidetone_inst( .clock(CLRCLK), .enable(internal_CW), .tone_freq(tone_freq), .sidetone_level(sidetone_level), .CW_PTT(CW_PTT),
                        .prof_sidetone(prof_sidetone),  .profile(profile));
// select sidetone  when CW key active and sidetone_level is not zero else Rx audio.
wire [31:0] Rx_audio;
assign Rx_audio = CW_PTT && (sidetone_level != 0) ? {prof_sidetone, prof_sidetone}  : {IF_Left_Data,IF_Right_Data}; 

//---------------------------------------------------------
//		Send L/R audio to TLV320 in I2S format
//---------------------------------------------------------
             
// send receiver audio to TLV320 in I2S format
audio_I2S audio_I2S_inst (.BCLK(CBCLK), .empty(), .LRCLK(CLRCLK), .data_in(Rx_audio), .data_out(CDIN), .get_data()); 	

      
//----------------------------------------------------------------------------
//		Get mic data from  TLV320 in I2S format and transfer to IF_clk domain
//---------------------------------------------------------------------------- 

wire [15:0] mic_data;
reg IF_CDOUT;
cdc_sync #(1)
	cdc_CDOUT (.siga(CDOUT), .rstb(IF_rst), .clkb(IF_clk), .sigb(IF_CDOUT)); 
      
mic_I2S mic_I2S_inst (.clock(CBCLK), .CLRCLK(CLRCLK), .in(IF_CDOUT), .mic_data(mic_data), .ready());
        
// transfer mic data into the IF_clk domain
cdc_sync #(16)
	cdc_mic (.siga(mic_data), .rstb(IF_rst), .clkb(IF_clk), .sigb(IF_mic_Data)); 

//---------------------------------------------------------
//		De-ramdomizer
//--------------------------------------------------------- 

/*

 A Digital Output Randomizer is fitted to the LTC2208. This complements bits 15 to 1 if 
 bit 0 is 1. This helps to reduce any pickup by the A/D input of the digital outputs. 
 We need to de-ramdomize the LTC2208 data if this is turned on. 
 
*/

reg [15:0]temp_ADC[0:1];
reg [15:0] temp_DACD; // for pre-distortion Tx tests

always @ (posedge C122_clk) 
begin 
	 temp_DACD <= { C122_cordic_i_out[13:0], 2'b0 }; // use high bits of temp_DACD
   if (RAND) begin	// RAND set so de-ramdomize
		if (INA[0]) temp_ADC[0] <= {~INA[15:1],INA[0]};
		else temp_ADC[0] <= INA;
	end
   else temp_ADC[0] <= INA;  // not set so just copy data	 
		
	if (RAND_2) begin
		if (INA_2[0]) temp_ADC[1] <= {~INA_2[15:1], INA_2[0]};
		else temp_ADC[1] <= INA_2;	
	end
	else temp_ADC[1] <= INA_2;
	
end 


//------------------------------------------------------------------------------
//                 Transfer  Data from IF clock to 122.88MHz clock domain
//------------------------------------------------------------------------------

// cdc_sync is used to transfer from a slow to a fast clock domain
wire  C122_DFS0, C122_DFS1;
wire  C122_rst;
wire  signed [15:0] C122_I_PWM;
wire  signed [15:0] C122_Q_PWM;

cdc_sync #(32)
	freq0 (.siga(IF_frequency[0]), .rstb(C122_rst), .clkb(C122_clk), .sigb(C122_frequency_HZ_Tx)); // transfer Tx frequency
	
cdc_sync #(32)
	freq1 (.siga(IF_frequency[1]), .rstb(C122_rst), .clkb(C122_clk), .sigb(C122_frequency_HZ[0])); // transfer Rx1 frequency

cdc_sync #(32)
	freq2 (.siga(IF_frequency[2]), .rstb(C122_rst), .clkb(C122_clk_2), .sigb(C122_frequency_HZ[1])); // transfer Rx2 frequency

cdc_sync #(32)
	freq3 (.siga(IF_frequency[3]), .rstb(C122_rst), .clkb(C122_clk), .sigb(C122_frequency_HZ[2])); // transfer Rx3 frequency

cdc_sync #(32)
	freq4 (.siga(IF_frequency[4]), .rstb(C122_rst), .clkb(C122_clk), .sigb(C122_frequency_HZ[3])); // transfer Rx4 frequency

cdc_sync #(32)
	freq5 (.siga(IF_frequency[5]), .rstb(C122_rst), .clkb(C122_clk), .sigb(C122_frequency_HZ[4])); // transfer Rx5 frequency

cdc_sync #(32)
	freq6 (.siga(IF_frequency[6]), .rstb(C122_rst), .clkb(C122_clk_2), .sigb(C122_frequency_HZ[5])); // transfer Rx6 frequency

cdc_sync #(32)
	freq7 (.siga(IF_frequency[7]), .rstb(C122_rst), .clkb(C122_clk_2), .sigb(C122_frequency_HZ[6])); // transfer Rx7 frequency

cdc_sync #(2)
	rates (.siga({IF_DFS1,IF_DFS0}), .rstb(C122_rst), .clkb(C122_clk), .sigb({C122_DFS1, C122_DFS0})); // sample rate
		
cdc_sync #(16)
    Tx_I  (.siga(IF_I_PWM), .rstb(C122_rst), .clkb(_122MHz), .sigb(C122_I_PWM )); // Tx I data
    
cdc_sync #(16)
    Tx_Q  (.siga(IF_Q_PWM), .rstb(C122_rst), .clkb(_122MHz), .sigb(C122_Q_PWM)); // Tx Q data
    

//------------------------------------------------------------------------------
//                 Pulse generators
//------------------------------------------------------------------------------

wire IF_CLRCLK;

//  Create short pulse from posedge of CLRCLK synced to IF_clk for RXF read timing
//  First transfer CLRCLK into IF clock domain
cdc_sync cdc_CRLCLK (.siga(CLRCLK), .rstb(IF_rst), .clkb(IF_clk), .sigb(IF_CLRCLK)); 
//  Now generate the pulse
pulsegen cdc_m   (.sig(IF_CLRCLK), .rst(IF_rst), .clk(IF_clk), .pulse(IF_get_samples));


//---------------------------------------------------------
//		Convert frequency to phase word 
//---------------------------------------------------------

/*	
     Calculates  ratio = fo/fs = frequency/122.88Mhz where frequency is in MHz
	 Each calculation should take no more than 1 CBCLK

	 B scalar multiplication will be used to do the F/122.88Mhz function
	 where: F * C = R
	 0 <= F <= 65,000,000 hz
	 C = 1/122,880,000 hz
	 0 <= R < 1

	 This method will use a 32 bit by 32 bit multiply to obtain the answer as follows:
	 1. F will never be larger than 65,000,000 and it takes 26 bits to hold this value. This will
		be a B0 number since we dont need more resolution than 1 Hz - i.e. fractions of a hertz.
	 2. C is a constant.  Notice that the largest value we could multiply this constant by is B26
		and have a signed value less than 1.  Multiplying again by B31 would give us the biggest
		signed value we could hold in a 32 bit number.  Therefore we multiply by B57 (26+31).
		This gives a value of M2 = 1,172,812,403 (B57/122880000)
	 3. Now if we multiply the B0 number by the B57 number (M2) we get a result that is a B57 number.
		This is the result of the desire single 32 bit by 32 bit multiply.  Now if we want a scaled
		32 bit signed number that has a range -1 <= R < 1, then we want a B31 number.  Thus we shift
		the 64 bit result right 32 bits (B57 -> B31) or merely select the appropriate bits of the
		64 bit result. Sweet!  However since R is always >= 0 we will use an unsigned B32 result
*/

//------------------------------------------------------------------------------
//                 All DSP code is in the Receiver module
//------------------------------------------------------------------------------

localparam NR = 7; // number of receivers to implement

reg       [31:0] C122_frequency_HZ [0:NR-1];   // frequency control bits for CORDIC
reg       [31:0] C122_frequency_HZ_Tx;
reg       [31:0] C122_last_freq [0:NR-1];
reg       [31:0] C122_last_freq_Tx;
reg       [31:0] C122_sync_phase_word [0:NR-1];
reg       [31:0] C122_sync_phase_word_Tx;
wire      [63:0] C122_ratio [0:NR-1];
wire      [63:0] C122_ratio_Tx;
wire      [23:0] rx_I [0:NR-1];
wire      [23:0] rx_Q [0:NR-1];
wire             strobe [0:NR-1];
wire		 [31:0] Rx2_phase_word;
wire  			  IF_IQ_Data_rdy;
wire 		 [47:0] IF_IQ_Data;
wire             test_strobe3;

// set the decimation rate 40 = 48k.....2 = 960k
	
	reg [5:0] rate;
	
	always @ ({C122_DFS1, C122_DFS0})
	begin 
		case ({C122_DFS1, C122_DFS0})		
		0: rate <= 6'd40; 		//  48ksps 
		1: rate <= 6'd20;			//  96ksps
		2: rate <= 6'd10;			//  192ksps
		3: rate <= 6'd5;			//  384ksps
		
		default: rate <= 6'd40;
		endcase
	end 

localparam M2 = 32'd1172812403;  // B57 = 2^57.   M2 = B57/122880000
localparam M3 = 32'd16777216; // used in the phase word calc to properly round the result


generate
  genvar c;
  for (c = 0; c < NR; c = c + 1) // calc freq phase word for 7 freqs (Rx1, Rx2, Rx3, Rx4, Rx5, Rx6, Rx7)
   begin: MDC 
    //  assign C122_ratio[c] = C122_frequency_HZ[c] * M2; // B0 * B57 number = B57 number

   // Note: We add 1/2 M2 (M3) so that we end up with a rounded 32 bit integer below.
    assign C122_ratio[c] = C122_frequency_HZ[c] * M2 + M3; // B0 * B57 number = B57 number 

    always @ (posedge C122_clk)
    begin
      if (C122_cbrise) // time between C122_cbrise is enough for ratio calculation to settle
      begin
        C122_last_freq[c] <= C122_frequency_HZ[c];
        if (C122_last_freq[c] != C122_frequency_HZ[c]) // frequency changed)
          C122_sync_phase_word[c] <= C122_ratio[c][56:25]; // B57 -> B32 number since R is always >= 0  
      end	
    end

//assign phase word for Rx2 depending upon whether common_Merc_freq is asserted
//assign Rx2_phase_word = common_Merc_freq ? C122_sync_phase_word[0] : C122_sync_phase_word[1];
	 
	cdc_mcp #(48)			// Transfer the receiver data and strobe from C122_clk to IF_clk
		IQ_sync (.a_data ({rx_I[c], rx_Q[c]}), .a_clk(C122_clk),.b_clk(IF_clk), .a_data_rdy(strobe[c]),
				.a_rst(C122_rst), .b_rst(IF_rst), .b_data(IF_M_IQ_Data[c]), .b_data_ack(IF_M_IQ_Data_rdy[c]));

  end
endgenerate
				
				// set receiver module input sources
wire [15:0] select_input_special;
wire [15:0] select_input_RX[0 : NR-1];
reg	[1:0] ADC_RX1 = 2'b00;	//default to ADC0 for input
reg	[1:0] ADC_RX2 = 2'b00;
reg	[1:0] ADC_RX3 = 2'b00;
reg	[1:0] ADC_RX4 = 2'b00;
reg	[1:0] ADC_RX5 = 2'b00;
reg	[1:0] ADC_RX6 = 2'b00;
reg	[1:0] ADC_RX7 = 2'b00;

assign select_input_RX[0] = (ADC_RX1[0] == 1'b1) ? temp_ADC[1] : temp_ADC[0];
assign select_input_RX[1] = (ADC_RX2[0] == 1'b1) ? temp_ADC[1] : temp_ADC[0];
assign select_input_RX[2] = (ADC_RX3[0] == 1'b1) ? temp_ADC[1] : temp_ADC[0];
assign select_input_RX[3] = (ADC_RX4[0] == 1'b1) ? temp_ADC[1] : temp_ADC[0];
assign select_input_RX[4] = (ADC_RX5[0] == 1'b1) ? temp_ADC[1] : temp_ADC[0];
assign select_input_RX[5] = (ADC_RX6[0] == 1'b1) ? temp_ADC[1] : temp_ADC[0];
assign select_input_RX[6] = (ADC_RX7[0] == 1'b1) ? temp_ADC[1] : temp_ADC[0];

assign select_input_special = FPGA_PTT ?  temp_DACD : select_input_RX[4]; //for support of PureSignal


receiver receiver_inst0(   // Rx1
	//control
	.clock(C122_clk),
	.rate(rate),
	.frequency(C122_sync_phase_word[0]),
	.out_strobe(strobe[0]),
	//input
	.in_data(select_input_RX[0]),
	//output
	.out_data_I(rx_I[0]),
	.out_data_Q(rx_Q[0]),
	.test_strobe3()
	);

receiver receiver_inst1(	// Rx2
	//control
	.clock(C122_clk_2),
	.rate(rate),
	.frequency(common_Merc_freq ? C122_sync_phase_word[0] : C122_sync_phase_word[1]),
	.out_strobe(strobe[1]),
	//input
	.in_data(select_input_RX[1]),
	//output
	.out_data_I(rx_I[1]),
	.out_data_Q(rx_Q[1]),
	.test_strobe3()
	);

receiver receiver_inst2(	// Rx3
	//control
	.clock(C122_clk),
	.rate(rate),
	.frequency(C122_sync_phase_word[2]),
	.out_strobe(strobe[2]),
	//input
	.in_data(select_input_RX[2]),
	//output
	.out_data_I(rx_I[2]),
	.out_data_Q(rx_Q[2]),
	.test_strobe3()
	);

receiver2 receiver_inst3(	// Rx4
	//control
	.clock(C122_clk),
	.rate(rate),
	.frequency(C122_sync_phase_word[3]),
	.out_strobe(strobe[3]),
	//input
	.in_data(select_input_RX[3]),
	//output
	.out_data_I(rx_I[3]),
	.out_data_Q(rx_Q[3]),
	.test_strobe3()
	);

	receiver2 receiver_inst4(	// Rx5 - has DAC data on TX
	//control
	.clock(C122_clk),
	.rate(rate),
	.frequency(PS_enabled ? C122_sync_phase_word_Tx : C122_sync_phase_word[4]),
	.out_strobe(strobe[4]),
	//input
	.in_data(select_input_special),
	//output
	.out_data_I(rx_I[4]),
	.out_data_Q(rx_Q[4]),
	.test_strobe3()
	);

receiver receiver_inst5(   // Rx6
	//control
	.clock(C122_clk),
	.rate(rate),
	.frequency(C122_sync_phase_word[5]),
	.out_strobe(strobe[5]),
	//input
	.in_data(select_input_RX[5]),
	//output
	.out_data_I(rx_I[5]),
	.out_data_Q(rx_Q[5]),
	.test_strobe3()
	);

receiver receiver_inst6(   // Rx7
	//control
	.clock(C122_clk),
	.rate(rate),
	.frequency(C122_sync_phase_word[6]),
	.out_strobe(strobe[6]),
	//input
	.in_data(select_input_RX[6]),
	//output
	.out_data_I(rx_I[6]),
	.out_data_Q(rx_Q[6]),
	.test_strobe3()
	);

// calc frequency phase word for Tx
//assign C122_ratio_Tx = C122_frequency_HZ_Tx * M2;
// Note: We add 1/2 M2 (M3) so that we end up with a rounded 32 bit integer below.
assign C122_ratio_Tx = C122_frequency_HZ_Tx * M2 + M3; 

always @ (posedge C122_clk)
begin
  if (C122_cbrise)
  begin
    C122_last_freq_Tx <= C122_frequency_HZ_Tx;
	 if (C122_last_freq_Tx != C122_frequency_HZ_Tx)
	  C122_sync_phase_word_Tx <= C122_ratio_Tx[56:25];
  end
end



//---------------------------------------------------------
//    ADC SPI interface 
//---------------------------------------------------------

wire [11:0] AIN1;
wire [11:0] AIN2;
wire [11:0] AIN3;
wire [11:0] AIN4;
wire [11:0] AIN5;  // holds 12 bit ADC value of Forward Power detector.
wire [11:0] AIN6;  // holds 12 bit ADC of 13.8v measurement
wire pk_detect_reset;	// from Orion_Tx_fifo_ctl.v to Orion_ADC.v
wire pk_detect_ack;		// from Orion_ADC.v to Orion_Tx_fifo_ctl.v

Orion_ADC ADC_SPI(.clock(CBCLK), .SCLK(ADCCLK), .nCS(nADCCS), .MISO(ADCMISO), .MOSI(ADCMOSI),
				   .AIN1(AIN1), .AIN2(AIN2), .AIN3(AIN3), .AIN4(AIN4), .AIN5(AIN5), .AIN6(AIN6), .pk_detect_reset(pk_detect_reset), .pk_detect_ack(pk_detect_ack));	
				   
				   
//---------------------------------------------------------
//	Apollo interface
//	Whenever frequency changes, send new value to Apollo.
//	Whenever Filter, Tuner, or PTT status changes, send enable or disable message to Apollo.
//	If PTT high continues beyond timeout, send additional PTT enable messages, so Apollo doesn't turn off the PA
//---------------------------------------------------------

localparam timeout = 16'd1000;	// default timeout (ms) for Apollo PA bias
// Maybe this value will eventually be sent from the PC in the C&C bytes.

wire Alex_SPI_SDO;
wire Alex_SPI_SCK;
wire SPI_TX_LOAD;
wire SPI_RX_LOAD;

wire Apollo_SPI_SDO;
wire Apollo_SPI_SCK;
wire ApolloReset;
wire ApolloEnable;
wire ApolloStatus;

wire FilterSelect;

assign FilterSelect = IF_Apollo;  // 0 = Alex, 1 = Apollo.

assign SPI_SDO = FilterSelect ? Apollo_SPI_SDO : Alex_SPI_SDO;		// select which module has control of data
assign SPI_SCK = FilterSelect ? Apollo_SPI_SCK : Alex_SPI_SCK;		// and clock for serial data transfer
assign J15_5   = FilterSelect ? ApolloReset : SPI_RX_LOAD;			// Alex Rx_load or Apollo Reset
assign J15_6   = FilterSelect ? ApolloEnable : SPI_TX_LOAD;      // Alex Tx_load or Apollo Enable 


reg IF_Filter;
reg IF_Tuner;
reg IF_autoTune;

//Apollo Apollo_inst(
//	.reset(IF_rst),
//	.clock(Apollo_clk),
//	.frequency(IF_frequency[0]),	
//	.timeout(timeout),
//	.PTT(FPGA_PTT),
//	.Filter(1'b1),  // Filter(IF_Filter),
//	.Tuner(IF_Tuner),
//	.ANT_TUNE(IF_autoTune),
//	.SPI_SDI(SPI_SDI),							// serial data from Apollo - currently not used 
//	.SPI_SDO(Apollo_SPI_SDO),					// serial data to Apollo
//	.SPI_SCK(Apollo_SPI_SCK),					// clock for Apollo serial data transfer
//	//.ApolloStatusLine(ApolloStatus),			// Apollo sets this low when it wants to send data
//	.ApolloStatusLine(0),			// Apollo sets this low when it wants to send data
//	.ApolloReset(ApolloReset),
//	//.ApolloEnable(ApolloEnable),
//	.statusAvailable(ApolloStatusAvailable),	// set true when ApolloStatusBytes have been updated
//	.status(ApolloStatusBytes),					// Status bytes from Apollo.  currently unused.  Some day send some to PC via C&C bytes.
//	.FilterSelect(FilterSelect)
//	);
	
				   
//---------------------------------------------------------
//                 Transmitter code 
//---------------------------------------------------------	

/* 
	The gain distribution of the transmitter code is as follows.
	Since the CIC interpolating filters do not interpolate by 2^n they have an overall loss.
	
	The overall gain in the interpolating filter is ((RM)^N)/R.  So in this case its 2560^4.
	This is normalised by dividing by ceil(log2(2560^4)).
	
	In which case the normalized gain would be (2560^4)/(2^46) = .6103515625
	
	The CORDIC has an overall gain of 1.647.
	
	Since the CORDIC takes 16 bit I & Q inputs but output needs to be truncated to 14 bits, in order to
	interface to the DAC, the gain is reduced by 1/4 to 0.41175
	
	We need to be able to drive to DAC to its full range in order to maximise the S/N ratio and 
	minimise the amount of PA gain.  We can increase the output of the CORDIC by multiplying it by 4.
	This is simply achieved by setting the CORDIC output width to 16 bits and assigning bits [13:0] to the DAC.
	
	The gain distripution is now:
	
	0.61 * 0.41174 * 4 = 1.00467 
	
	This means that the DAC output will wrap if a full range 16 bit I/Q signal is received. 
	This can be prevented by reducing the output of the CIC filter.
	
	If we subtract 1/128 of the CIC output from itself the level becomes
	
	1 - 1/128 = 0.9921875
	
	Hence the overall gain is now 
	
	0.61 * 0.9921875 * 0.41174 * 4 = 0.996798
	

*/	

reg signed [15:0]C122_fir_i;
reg signed [15:0]C122_fir_q;

// latch I&Q data on strobe from FIR
always @ (posedge _122MHz)
begin 
	if (req1) begin 
		C122_fir_i = C122_I_PWM;
		C122_fir_q = C122_Q_PWM;	
	end 
end 


//---------------------------------------------------------
//  Interpolate by 8 FIR and interpolate by 320 CIC filters
//---------------------------------------------------------

wire req1, req2;
wire [19:0] y1_r, y1_i; 
wire [15:0] y2_r, y2_i;

FirInterp8_1024 fi (_122MHz, req2, req1, C122_fir_i, C122_fir_q, y1_r, y1_i);  // req2 enables an output sample, req1 requests next input sample.

CicInterpM5 #(.RRRR(320), .IBITS(20), .OBITS(16), .GBITS(34)) in2 ( _122MHz, 1'd1, req2, y1_r, y1_i, y2_r, y2_i);


			   

//------------------------------------------------------

//    CORDIC NCO 
//---------------------------------------------------------

// Code rotates input at set frequency and produces I & Q 

wire signed [14:0] C122_cordic_i_out;
wire signed [31:0] C122_phase_word_Tx;

wire signed [15:0] I;
wire signed [15:0] Q;

// if in VNA mode use the Rx[0] phase word for the Tx
assign C122_phase_word_Tx = VNA ? C122_sync_phase_word[0] : C122_sync_phase_word_Tx;
assign                  I =  VNA ? 16'd19274 : (CW_PTT ? CW_RF : y2_i);   	// select VNA or CW mode if active. Set CORDIC for max DAC output
assign                  Q = (VNA | CW_PTT)  ? 16'd0 : y2_r; 					// taking into account CORDICs gain i.e. 0x7FFF/1.7


// NOTE:  I and Q inputs reversed to give correct sideband out 

cpl_cordic #(.OUT_WIDTH(16))
 		cordic_inst (.clock(_122MHz), .frequency(C122_phase_word_Tx), .in_data_I(I),			
		.in_data_Q(Q), .out_data_I(C122_cordic_i_out), .out_data_Q());		
			 	 
/* 
  We can use either the I or Q output from the CORDIC directly to drive the DAC.

    exp(jw) = cos(w) + j sin(w)

  When multplying two complex sinusoids f1 and f2, you get only f1 + f2, no
  difference frequency.

      Z = exp(j*f1) * exp(j*f2) = exp(j*(f1+f2))
        = cos(f1 + f2) + j sin(f1 + f2)
*/

// clock the DAC data at 15 degrees to the clock to ensure it is stable.
reg C122_clk_90;
reg [15:0] temp_cordic_out;

// the Tx DAC (MAX5891) uses offset binary format inputs so conversion of the signed cordic output is necessary
//always @ (posedge C122_clk_15)
always @ (posedge DACD_clock)
begin
	temp_cordic_out <= {C122_cordic_i_out[13:0], 2'b00};	// use C122_cordic_i_out in the high bits of temp_cordic_out
	DACD <= clean_IO5 ? (16'd32768 + temp_cordic_out) : 16'b0; 				// convert to 16-bit offset binary format and assign to DACD
end

//------------------------------------------------------------
//  Set Power Output 
//------------------------------------------------------------

/*
	Code used to set power output depends on hardware. If TX_ATTEN_SELECT is high then PWM DAC drive current is used.
	If low then a digital attenuator (0-31dB in 0.5dB steps) is used to control power output.
	Since 0.5dB steps are too course, the PWM DAC is used for fine control. The selection of attenuator step and DAC PWM 
	settings, based on IF_Drive_Level,is done via ROMs (Tx_Atten and Tx_DAC).

*/

// select Tx attenuator parallel load mode

assign TX_ATTN_LE = 1'b1;
assign TX_ATTN_MODE = 0;

wire [7:0] Drive_PWM;

Tx_Atten Tx_Attn_inst (.clock(IF_clk), .address(IF_Drive_Level), .q( TX_ATTEN));  // ROM for Tx attenuator settings
Tx_DAC   Tx_DAC_inst  (.clock(IF_clk), .address(IF_Drive_Level), .q(Drive_PWM)); // ROM for Tx DAC settings

// select DAC PWM source based on Tx_ATTEN_SELECT 
wire [7:0] PWM_source =  TX_ATTEN_SELECT ? IF_Drive_Level : Drive_PWM;


reg [7:0] PWM_count;

//	PWM DAC to set drive current to DAC. PWM_count increments using 122.88MHz clock. If the count is less than the drive 
//	level set by the PC then DAC_ALC will be high, otherwise low.

always @ (posedge _122MHz)
begin 
	PWM_count <= PWM_count + 1'b1;
	if (PWM_source >= PWM_count)
		DAC_ALC <= 1'b1;
	else 
		DAC_ALC <= 1'b0;
end 


//---------------------------------------------------------
//  Receive DOUT and CDOUT data to put in TX FIFO
//---------------------------------------------------------

wire   [15:0] IF_P_mic_Data;
wire          IF_P_mic_Data_rdy;
wire   [47:0] IF_M_IQ_Data [0:NR-1];
wire [NR-1:0] IF_M_IQ_Data_rdy;
wire   [63:0] IF_tx_IQ_mic_data;
reg           IF_tx_IQ_mic_rdy;
wire   [15:0] IF_mic_Data;
wire    [2:0] IF_chan;
reg    [2:0] IF_last_chan;
wire     [47:0] IF_chan_test;

always @*
begin
  if (IF_rst)
    IF_tx_IQ_mic_rdy = 1'b0;
  else 
      IF_tx_IQ_mic_rdy = IF_M_IQ_Data_rdy[0]; 	// this the strobe signal from the ADC now in IF clock domain
end

assign IF_IQ_Data = IF_M_IQ_Data[IF_chan];

// concatenate the IQ and Mic data to form a 64 bit data word
assign IF_tx_IQ_mic_data = {IF_IQ_Data, IF_mic_Data};  

//----------------------------------------------------------------------------
//     Tx_fifo Control - creates IF_tx_fifo_wdata and IF_tx_fifo_wreq signals
//----------------------------------------------------------------------------

localparam RFSZ = clogb2(RX_FIFO_SZ-1);  // number of bits needed to hold 0 - (RX_FIFO_SZ-1)
localparam TFSZ = clogb2(TX_FIFO_SZ-1);  // number of bits needed to hold 0 - (TX_FIFO_SZ-1)
localparam SFSZ = clogb2(SP_FIFO_SZ-1);  // number of bits needed to hold 0 - (SP_FIFO_SZ-1)

wire     [15:0] IF_tx_fifo_wdata;   		// LTC2208 ADC uses this to send its data to Tx FIFO
wire            IF_tx_fifo_wreq;    		// set when we want to send data to the Tx FIFO
wire            IF_tx_fifo_full;
wire [TFSZ-1:0] IF_tx_fifo_used;
wire            IF_tx_fifo_rreq;
wire            IF_tx_fifo_empty;

wire [RFSZ-1:0] IF_Rx_fifo_used;    		// read side count
wire            IF_Rx_fifo_full;

wire            clean_dash;      			// debounced dash key
wire            clean_dot;       			// debounced dot key
wire            clean_PTT_in;    			// debounced PTT button
wire     [11:0] Penny_ALC;

wire   [RFSZ:0] RX_USED;
wire            IF_tx_fifo_clr;

assign RX_USED = {IF_Rx_fifo_full,IF_Rx_fifo_used};


assign Penny_ALC = AIN5; 

wire VNA_start = VNA && IF_Rx_save && (IF_Rx_ctrl_0[7:1] == 7'b0000_001);  // indicates a frequency change for the VNA.

Orion_Tx_fifo_ctrl #(RX_FIFO_SZ, TX_FIFO_SZ) TXFC 
           (IF_rst, IF_clk, IF_tx_fifo_wdata, IF_tx_fifo_wreq, IF_tx_fifo_full,
            IF_tx_fifo_used, IF_tx_fifo_clr, IF_tx_IQ_mic_rdy,
            IF_tx_IQ_mic_data, IF_chan, IF_last_chan, clean_dash, clean_dot, (clean_PTT_in | CW_PTT), OVERFLOW,
            OVERFLOW_2, Penny_serialno, Merc_serialno, Orion_version, Penny_ALC, AIN1, AIN2,
            AIN3, AIN4, AIN6, IO4, clean_IO5, IO6, clean_IO8, VNA_start, VNA, pk_detect_reset, pk_detect_ack);

//------------------------------------------------------------------------
//   Tx_fifo  (1024 words) Dual clock FIFO - Altera Megafunction (dcfifo)
//------------------------------------------------------------------------

/*
        Data from the Tx FIFO Controller  is written to the FIFO using IF_tx_fifo_wreq. 
        FIFO is 1024 WORDS long.
        NB: The output flags are only valid after a read/write clock has taken place
        
        
							--------------------
	IF_tx_fifo_wdata 	|data[15:0]		 wrful| IF_tx_fifo_full
						   |				         |
	IF_tx_fifo_wreq	|wreq		     wrempty| IF_tx_fifo_empty
						   |				   	   |
		IF_clk			|>wrclk	 wrused[9:0]| IF_tx_fifo_used
						   ---------------------
    Tx_fifo_rdreq		|rdreq		   q[7:0]| PHY_Tx_data
						   |					      |
	   Tx_clock_2		|>rdclk		  rdempty| 
						   |		  rdusedw[10:0]| PHY_Tx_rdused  (0 to 2047 bytes)
						   ---------------------
						   |                    |
 IF_tx_fifo_clr OR  	|aclr                |
	IF_rst				---------------------
				
        

*/

Tx_fifo Tx_fifo_inst(.wrclk (IF_clk),.rdreq (Tx_fifo_rdreq),.rdclk (Tx_clock_2),.wrreq (IF_tx_fifo_wreq), 
                .data ({IF_tx_fifo_wdata[7:0], IF_tx_fifo_wdata[15:8]}),.q (PHY_Tx_data),.wrusedw(IF_tx_fifo_used), .wrfull(IF_tx_fifo_full),
                .rdempty(),.rdusedw(PHY_Tx_rdused),.wrempty(IF_tx_fifo_empty),.aclr(IF_rst || IF_tx_fifo_clr ));

wire [7:0] PHY_Tx_data;
reg [3:0]sync_TD;
wire PHY_Tx_rdempty;             
             


//---------------------------------------------------------
//   Rx_fifo  (2048 words) single clock FIFO
//---------------------------------------------------------

wire [15:0] IF_Rx_fifo_rdata;
reg         IF_Rx_fifo_rreq;    // controls reading of fifo
wire [15:0] IF_PHY_data;

wire [15:0] IF_Rx_fifo_wdata;
reg         IF_Rx_fifo_wreq;

FIFO #(RX_FIFO_SZ) RXF (.rst(IF_rst), .clk (IF_clk), .full(IF_Rx_fifo_full), .usedw(IF_Rx_fifo_used), 
          .wrreq (IF_Rx_fifo_wreq), .data (IF_PHY_data), 
          .rdreq (IF_Rx_fifo_rreq), .q (IF_Rx_fifo_rdata) );


//------------------------------------------------------------
//   Sync and  C&C  Detector
//------------------------------------------------------------

/*

  Read the value of IF_PHY_data whenever IF_PHY_drdy is set.
  Look for sync and if found decode the C&C data.
  Then send subsequent data to Rx FIF0 until end of frame.
	
*/

reg   [2:0] IF_SYNC_state;
reg   [2:0] IF_SYNC_state_next;
reg   [7:0] IF_SYNC_frame_cnt; 	// 256-4 words = 252 words
reg   [7:0] IF_Rx_ctrl_0;   		// control C0 from PC
reg   [7:0] IF_Rx_ctrl_1;   		// control C1 from PC
reg   [7:0] IF_Rx_ctrl_2;   		// control C2 from PC
reg   [7:0] IF_Rx_ctrl_3;   		// control C3 from PC
reg   [7:0] IF_Rx_ctrl_4;   		// control C4 from PC
reg         IF_Rx_save;


localparam SYNC_IDLE   = 1'd0,
           SYNC_START  = 1'd1,
           SYNC_RX_1_2 = 2'd2,
           SYNC_RX_3_4 = 2'd3,
           SYNC_FINISH = 3'd4;

always @ (posedge IF_clk)
begin
  if (IF_rst)
    IF_SYNC_state <= #IF_TPD SYNC_IDLE;
  else
    IF_SYNC_state <= #IF_TPD IF_SYNC_state_next;

  if (IF_rst)
    IF_Rx_save <= #IF_TPD 1'b0;
  else
    IF_Rx_save <= #IF_TPD IF_PHY_drdy && (IF_SYNC_state == SYNC_RX_3_4);

  if (IF_PHY_drdy && (IF_SYNC_state == SYNC_START) && (IF_PHY_data[15:8] == 8'h7F))
    IF_Rx_ctrl_0  <= #IF_TPD IF_PHY_data[7:0];

  if (IF_PHY_drdy && (IF_SYNC_state == SYNC_RX_1_2))
  begin
    IF_Rx_ctrl_1  <= #IF_TPD IF_PHY_data[15:8];
    IF_Rx_ctrl_2  <= #IF_TPD IF_PHY_data[7:0];
  end

  if (IF_PHY_drdy && (IF_SYNC_state == SYNC_RX_3_4))
  begin
    IF_Rx_ctrl_3  <= #IF_TPD IF_PHY_data[15:8];
    IF_Rx_ctrl_4  <= #IF_TPD IF_PHY_data[7:0];
  end

  if (IF_SYNC_state == SYNC_START)
    IF_SYNC_frame_cnt <= 0;					    					// reset sync counter
  else if (IF_PHY_drdy && (IF_SYNC_state == SYNC_FINISH))
    IF_SYNC_frame_cnt <= IF_SYNC_frame_cnt + 1'b1;		    // increment if we have data to store
end

always @*
begin
  case (IF_SYNC_state)
    // state SYNC_IDLE  - loop until we find start of sync sequence
    SYNC_IDLE:
    begin
      IF_Rx_fifo_wreq  = 1'b0;             // Note: Sync bytes not saved in Rx_fifo

      if (IF_rst || !IF_PHY_drdy) 
        IF_SYNC_state_next = SYNC_IDLE;    // wait till we get data from PC
      else if (IF_PHY_data == 16'h7F7F)
        IF_SYNC_state_next = SYNC_START;   // possible start of sync
      else
        IF_SYNC_state_next = SYNC_IDLE;
    end	

    // check for 0x7F  sync character & get Rx control_0 
    SYNC_START:
    begin
      IF_Rx_fifo_wreq  = 1'b0;             // Note: Sync bytes not saved in Rx_fifo

      if (!IF_PHY_drdy)              
        IF_SYNC_state_next = SYNC_START;   // wait till we get data from PC
      else if (IF_PHY_data[15:8] == 8'h7F)
        IF_SYNC_state_next = SYNC_RX_1_2;  // have sync so continue
      else
        IF_SYNC_state_next = SYNC_IDLE;    // start searching for sync sequence again
    end

    
    SYNC_RX_1_2:                        	 // save Rx control 1 & 2
    begin
      IF_Rx_fifo_wreq  = 1'b0;             // Note: Rx control 1 & 2 not saved in Rx_fifo

      if (!IF_PHY_drdy)              
        IF_SYNC_state_next = SYNC_RX_1_2;  // wait till we get data from PC
      else
        IF_SYNC_state_next = SYNC_RX_3_4;
    end

    SYNC_RX_3_4:                        	 // save Rx control 3 & 4
    begin
      IF_Rx_fifo_wreq  = 1'b0;             // Note: Rx control 3 & 4 not saved in Rx_fifo

      if (!IF_PHY_drdy)              
        IF_SYNC_state_next = SYNC_RX_3_4;  // wait till we get data from PC
      else
        IF_SYNC_state_next = SYNC_FINISH;
    end

    // Remainder of data goes to Rx_fifo, re-start looking
    // for a new SYNC at end of this frame. 
    // Note: due to the use of IF_PHY_drdy data will only be written to the 
    // Rx fifo if there is room. Also the frame_count will only be incremented if IF_PHY_drdy is true.
    SYNC_FINISH:
    begin    
	  IF_Rx_fifo_wreq  = IF_PHY_drdy;
	  if (IF_SYNC_frame_cnt == ((512-8)/2)) begin  // frame ended, go get sync again
		IF_SYNC_state_next = SYNC_IDLE;
	  end 
	  else IF_SYNC_state_next = SYNC_FINISH;
    end

    default:
    begin
      IF_Rx_fifo_wreq  = 1'b0;
      IF_SYNC_state_next = SYNC_IDLE;
    end
	endcase
end

wire have_room;
assign have_room = (IF_Rx_fifo_used < RX_FIFO_SZ - ((512-8)/2)) ? 1'b1 : 1'b0;  // the /2 is because we send 16 bit values

// prevent read from PHY fifo if empty and writing to Rx fifo if not enough room 
assign  IF_PHY_drdy = have_room & ~IF_PHY_rdempty;

//---------------------------------------------------------
//              Decode Command & Control data
//---------------------------------------------------------

/*
	Decode IF_Rx_ctrl_0....IF_Rx_ctrl_4.

	Decode frequency (both Tx and Rx if full duplex selected), PTT, Speed etc

	The current frequency is set by the PC by decoding 
	IF_Rx_ctrl_1... IF_Rx_ctrl_4 when IF_Rx_ctrl_0[7:1] = 7'b0000_001
		
      The Rx Sampling Rate, either 192k, 96k or 48k is set by
      the PC by decoding IF_Rx_ctrl_1 when IF_Rx_ctrl_0[7:1] are all zero. IF_Rx_ctrl_1
      decodes as follows:

      IF_Rx_ctrl_1 = 8'bxxxx_xx00  - 48kHz
      IF_Rx_ctrl_1 = 8'bxxxx_xx01  - 96kHz
      IF_Rx_ctrl_1 = 8'bxxxx_xx10  - 192kHz

	Decode PTT from PC. Held in IF_Rx_ctrl_0[0] as follows
	
	0 = PTT inactive
	1 = PTT active
	
	Decode Attenuator settings on Alex, when IF_Rx_ctrl_0[7:1] = 0, IF_Rx_ctrl_3[1:0] indicates the following 
	
	00 = 0dB
	01 = 10dB
	10 = 20dB
	11 = 30dB
	
	Decode ADC & Attenuator settings on Orion, when IF_Rx_ctrl_0[7:1] = 0, IF_Rx_ctrl_3[4:2] indicates the following
	
	000 = Random, Dither, Preamp OFF
	1xx = Random ON
	x1x = Dither ON
	xx1 = Preamp ON **** replace with attenuator
	
	Decode Rx relay settings on Alex, when IF_Rx_ctrl_0[7:1] = 0, IF_Rx_ctrl_3[6:5] indicates the following
	
	00 = None
	01 = Rx 1
	10 = Rx 2
	11 = Transverter
	
	Decode Tx relay settigs on Alex, when IF_Rx_ctrl_0[7:1] = 0, IF_Rx_ctrl_4[1:0] indicates the following
	
	00 = Tx 1
	01 = Tx 2
	10 = Tx 3
	
	Decode Rx_1_out relay settigs on Alex, when IF_Rx_ctrl_0[7:1] = 0, IF_Rx_ctrl_3[7] indicates the following

	1 = Rx_1_out on 

	When IF_Rx_ctrl_0[7:1] == 7'b0001_010 decodes as follows:
	
	IF_Line_In_Gain		<= IF_Rx_ctrl2[4:0]	// decode 5-bit line gain setting
	
*/

reg   [6:0] IF_OC;       			// open collectors on Orion
reg         IF_mode;     			// normal or Class E PA operation 
reg         IF_RAND;     			// when set randomizer in ADCon
reg         IF_DITHER;   			// when set dither in ADC on
reg   [1:0] IF_ATTEN;    			// decode attenuator setting on Alex
reg         Preamp;					// selects input attenuator setting, 0 = 20dB, 1 = 0dB (preamp ON)
reg   [1:0] IF_TX_relay; 			// Tx relay setting on Alex
reg         IF_Rout;     			// Rx1 out on Alex
reg   [1:0] IF_RX_relay; 			// Rx relay setting on Alex 
reg  [31:0] IF_frequency[0:7]; 	// Tx, Rx1, Rx2, Rx3, Rx4, Rx5, Rx6, Rx7
reg         IF_duplex;
reg         IF_DFS1;
reg			IF_DFS0;
reg   [7:0] IF_Drive_Level; 		// Tx drive level
reg         IF_Mic_boost;			// Mic boost 0 = 0dB, 1 = 20dB
reg         IF_Line_In;				// Selects input, mic = 0, line = 1
reg			common_Merc_freq;		// when set forces Rx2 freq to Rx1 freq
reg   [4:0] IF_Line_In_Gain;		// Sets Line-In Gain value (00000=-32.4 dB to 11111=+12 dB in 1.5 dB steps)
reg         IF_Apollo;				// Selects Alex (0) or Apollo (1)
reg 			VNA;						// Selects VNA mode when set. 
reg		   Alex_manual; 	  		// set if manual selection of Alex relays active
reg         Alex_6m_preamp; 		// set if manual selection and 6m preamp selected
reg			Alex_6m_preamp_2;		//
reg   [6:0] Alex_manual_LPF;		// Alex LPF relay selection in manual mode
reg   [5:0] Alex_manual_HPF;		// Alex HPF relay selection in manual mode
reg	[5:0] Alex_manual_BPF2;		// Alex BPF2 relay selection in manual mode
reg			RX2_GROUND;				// Alex RX2 GROUND state
reg   [4:0] Orion_atten;			// 0-31 dB Orion attenuator value
reg			Orion_atten_enable; // enable/disable bit for Orion attenuator
reg			TR_relay_disable;		// Alex T/R relay disable option
reg	[4:0] Orion_atten2;			// attenuation setting for input attenuator 2 (input atten for ADC2), 0-31 dB
reg			atten2_enable; 		//enable/disable control for input attenuator 2 (0=disabled, 1= enabled)
reg			Orion_tip_ring_select;
reg         internal_CW;			// set when internal CW generation selected
reg   [7:0] sidetone_level;		// 0 - 100, sets internal sidetone level
reg   [7:0] RF_delay;				// 0 - 255, sets delay in mS from CW Key activation to RF out
reg   [9:0] hang;						// 0 - 1000, sets delay in mS from release of CW Key to dropping of PTT
reg  [11:0] tone_freq;				// 200 to 2250 Hz, sets sidetone frequency.
reg			Orion_micPTT_disable; // 0 =  Orion mic PTT enabled, 1 = Orion mic PTT disabled
reg         key_reverse;		   // reverse CW keyes if set
reg   [5:0] keyer_speed; 			// CW keyer speed 0-60 WPM
reg   [1:0] keyer_mode_in;			// 00 = straight/external/bug, 01 = Mode A, 10 = Mode B
reg   [7:0] keyer_weight;			// keyer weight 33-66
reg         keyer_spacing;			// 0 = off, 1 = on
reg   [4:0] atten_on_Tx;			// Rx attenuation value to use when Tx is active
reg			XVTR_Enable;			// 8000DLE XVTR mode enable option
reg			PS_enabled;				// PureSignal state (0=disabled, 1=enabled)

always @ (posedge IF_clk)
begin 
  if (IF_rst)
  begin // set up default values - 0 for now
    // RX_CONTROL_1
    {IF_DFS1, IF_DFS0} <= 2'b00;   	// decode speed 
	 Orion_tip_ring_select <= 1'b0;	// Orion mic tip/ring config
	 MICBIAS_ENABLE	  <= 1'b0;     // Orion mic bias enable
	 Orion_micPTT_disable <= 1'b0;   // Orion mic PTT disable
    // RX_CONTROL_2
    IF_mode            <= 1'b0;    	// decode mode, normal or Class E PA
    IF_OC              <= 7'b0;    	// decode open collectors on Orion
    // RX_CONTROL_3
    IF_ATTEN           <= 2'b0;    	// decode Alex attenuator setting 
    Preamp             <= 1'b1;    	// decode Preamp (Attenuator), default on
    IF_DITHER          <= 1'b0;    	// decode dither on or off
    IF_RAND            <= 1'b0;    	// decode randomizer on or off
    IF_RX_relay        <= 2'b0;    	// decode Alex Rx relays
    IF_Rout            <= 1'b0;    	// decode Alex Rx_1_out relay
	 TR_relay_disable   <= 1'b0;     // decode Alex T/R relay disable
    // RX_CONTROL_4
    IF_TX_relay        <= 2'b0;    	// decode Alex Tx Relays
    IF_duplex          <= 1'b0;    	// not in duplex mode
	 IF_last_chan       <= 3'b000;  	// default single receiver
    IF_Mic_boost       <= 1'b0;    	// mic boost off 
    IF_Drive_Level     <= 8'b0;	   // drive at minimum
	 IF_Line_In			  <= 1'b0;		// select Mic input, not Line in
	 IF_Filter			  <= 1'b0;		// Apollo filter disabled (bypassed)
	 IF_Tuner			  <= 1'b0;		// Apollo tuner disabled (bypassed)
	 IF_autoTune	     <= 1'b0;		// Apollo auto-tune disabled
	 IF_Apollo			  <= 1'b0;     //	Alex selected		
	 VNA					  <= 1'b0;		// VNA disabled
	 Alex_manual		  <= 1'b0; 	  	// default manual Alex filter selection (0 = auto selection, 1 = manual selection)
	 Alex_manual_HPF	  <= 6'b0;		// default manual settings, no Alex HPF filters selected
	 Alex_6m_preamp	  <= 1'b0;		// default not set
	 Alex_6m_preamp_2	  <= 1'b0;		// default not set
	 Alex_manual_LPF	  <= 7'b0;		// default manual settings, no Alex LPF filters selected
	 IF_Line_In_Gain	  <= 5'b0;		// default line-in gain at min
	 Orion_atten		  <= 5'b0;		// default zero input attenuation
	 Orion_atten_enable <= 1'b0;    // default disable Orion attenuator
	 Orion_atten2		  <= 5'b0;		// default attenuation setting for input attenuator 2 (input atten for ADC2)
	 atten2_enable 		<= 1'b0;		// default disable input attenuator 2 
    internal_CW        <= 1'b0;		// default internal CW generation is off
    sidetone_level     <= 8'b0;		// default sidetone level is 0
    RF_delay           <= 8'b0;	   // default CW Key activation to RF out
    hang               <= 10'b0;		// default hang time 
	 tone_freq  		  <= 12'b0;		// default sidetone frequency
    key_reverse		  <= 1'b0;     // reverse CW keyes if set
    keyer_speed        <= 6'b0; 		// CW keyer speed 0-60 WPM
    keyer_mode_in      <= 2'b0;	   // 00 = straight/external/bug, 01 = Mode A, 10 = Mode B
    keyer_weight       <= 8'b0;		// keyer weight 33-66
    keyer_spacing      <= 1'b0;	   // 0 = off, 1 = on
	 atten_on_Tx		  <= 5'b11111; // default Rx attenuation value to use when Tx is active	
	 XVTR_Enable		  <= 1'b0;		// default 8000DLE XVTR mode disabled
	 PS_enabled			  <= 1'b0;		// default PureSignal disabled (0=disabled, 1=enabled)
  end
  else if (IF_Rx_save) 					// all Rx_control bytes are ready to be saved
  begin 										// Need to ensure that C&C data is stable 
    if (IF_Rx_ctrl_0[7:1] == 7'b0000_000)
    begin
      // RX_CONTROL_1
      {IF_DFS1, IF_DFS0}  <= IF_Rx_ctrl_1[1:0]; // decode speed 
      // RX_CONTROL_2
      IF_mode             <= IF_Rx_ctrl_2[0];   // decode mode, normal or Class E PA
      IF_OC               <= IF_Rx_ctrl_2[7:1]; // decode open collectors on Penelope
      // RX_CONTROL_3
      IF_ATTEN            <= IF_Rx_ctrl_3[1:0]; // decode Alex attenuator setting 
      Preamp              <= IF_Rx_ctrl_3[2];  // decode Preamp (Attenuator)  1 = On (0dB atten), 0 = Off (20dB atten)
      IF_DITHER           <= IF_Rx_ctrl_3[3];   // decode dither on or off
      IF_RAND             <= IF_Rx_ctrl_3[4];   // decode randomizer on or off
      IF_RX_relay         <= IF_Rx_ctrl_3[6:5]; // decode Alex Rx relays
      IF_Rout             <= IF_Rx_ctrl_3[7];   // decode Alex Rx_1_out relay
      // RX_CONTROL_4
      IF_TX_relay         <= IF_Rx_ctrl_4[1:0]; // decode Alex Tx Relays
      IF_duplex           <= IF_Rx_ctrl_4[2];   // save duplex mode
      IF_last_chan	     <= IF_Rx_ctrl_4[5:3]; // number of IQ streams to send to PC
		common_Merc_freq	  <= IF_Rx_ctrl_4[7];   // diversity mode, Rx1/Rx2 freq forced equal if set
    end
    if (IF_Rx_ctrl_0[7:1] == 7'b0001_001)
    begin
	  IF_Drive_Level	  <= IF_Rx_ctrl_1;	    	// decode drive level
	  IF_Mic_boost		  <= IF_Rx_ctrl_2[0];   	// decode mic boost 0 = 0dB, 1 = 20dB  
	  IF_Line_In		  <= IF_Rx_ctrl_2[1];		// 0 = Mic input, 1 = Line In
	  IF_Filter			  <= IF_Rx_ctrl_2[2];		// 1 = enable Apollo filter
	  IF_Tuner			  <= IF_Rx_ctrl_2[3];		// 1 = enable Apollo tuner
	  IF_autoTune		  <= IF_Rx_ctrl_2[4];		// 1 = begin Apollo auto-tune
	  IF_Apollo         <= IF_Rx_ctrl_2[5];      // 1 = Apollo enabled, 0 = Alex enabled 
	  Alex_manual		  <= IF_Rx_ctrl_2[6]; 	  	// manual Alex HPF/LPF filter selection (0 = disable, 1 = enable)
	  VNA					  <= IF_Rx_ctrl_2[7];		// 1 = enable VNA mode
	  Alex_manual_HPF	  <= IF_Rx_ctrl_3[5:0];		// Alex HPF filters select
	  Alex_6m_preamp	  <= IF_Rx_ctrl_3[6];		// 6M low noise amplifier (0 = disable, 1 = enable)
	  TR_relay_disable  <= IF_Rx_ctrl_3[7];		// Alex T/R relay disable option (0=TR relay enabled, 1=TR relay disabled)
	  Alex_manual_LPF	  <= IF_Rx_ctrl_4[6:0];		// Alex LPF filters select	  
	end
	if (IF_Rx_ctrl_0[7:1] == 7'b0001_010)
	begin
	  Orion_tip_ring_select <= IF_Rx_ctrl_1[4];	 	// 0 = Orion mic ptt to ring and mic/mic bias to tip, 1 = Orion mic ptt to tip and mic/mic bias to ring
	  MICBIAS_ENABLE			<= IF_Rx_ctrl_1[5];   	// 0 = disables Orion mic bias, 1 = enables Orion microphone bias
	  Orion_micPTT_disable 	<= IF_Rx_ctrl_1[6];   	// 0 = Orion mic PTT enabled, 1 = Orion mic PTT disabled
	  IF_Line_In_Gain    	<= IF_Rx_ctrl_2[4:0];	// decode line-in gain setting
	  Orion_atten      		<= IF_Rx_ctrl_4[4:0];   // decode input attenuation setting
	  Orion_atten_enable 	<= IF_Rx_ctrl_4[5];    	// decode Orion attenuator 1 enable/disable
	end
 	if (IF_Rx_ctrl_0[7:1] == 7'b0001_011)
	begin
	  Orion_atten2   	<= IF_Rx_ctrl_1[4:0];	// attenuation setting for input attenuator 2 (input atten for ADC2)
	  atten2_enable 	   <= IF_Rx_ctrl_1[5];		// input attenuator 2 enable/disable (0=disabled, 1= enabled)
	 key_reverse		  <= IF_Rx_ctrl_2[6];     	// reverse CW keyes if set
    keyer_speed        <= IF_Rx_ctrl_3[5:0];  	// CW keyer speed 0-60 WPM
    keyer_mode_in         <= IF_Rx_ctrl_3[7:6];	   // 00 = straight/external/bug, 01 = Mode A, 10 = Mode B
    if (keyer_mode_in == 2'b00) iambic <= 1'b0; // straight key/bug CW mode
	 else iambic <= 1'b1;								// iambic CW keyer mode
	 if (keyer_mode_in == 2'b01) keyer_mode <= 1'b0; // iambic CW keyer mode A
	 if (keyer_mode_in == 2'b10) keyer_mode <= 1'b1; // iambic CW keyer mode B
	 keyer_weight       <= IF_Rx_ctrl_4[6:0];		// keyer weight 33-66
    keyer_spacing      <= IF_Rx_ctrl_4[7];	   // 0 = off, 1 = on
	end

 	if (IF_Rx_ctrl_0[7:1] == 7'b0001_110)
	begin
	  ADC_RX1   			<= IF_Rx_ctrl_1[1:0];	// ADC to use for RX1: 00=ADC0, 01=ADC1, 10=ADC2
	  ADC_RX2   			<= IF_Rx_ctrl_1[3:2];	// ADC to use for RX2: 00=ADC0, 01=ADC1, 10=ADC2
	  ADC_RX3   			<= IF_Rx_ctrl_1[5:4];	// ADC to use for RX3: 00=ADC0, 01=ADC1, 10=ADC2
	  ADC_RX4   			<= IF_Rx_ctrl_1[7:6];	// ADC to use for RX4: 00=ADC0, 01=ADC1, 10=ADC2
	  ADC_RX5   			<= IF_Rx_ctrl_2[1:0];	// ADC to use for RX5: 00=ADC0, 01=ADC1, 10=ADC2
	  ADC_RX6   			<= IF_Rx_ctrl_2[3:2];	// ADC to use for RX6: 00=ADC0, 01=ADC1, 10=ADC2
	  ADC_RX7   			<= IF_Rx_ctrl_2[5:4];	// ADC to use for RX7: 00=ADC0, 01=ADC1, 10=ADC2
	  atten_on_Tx			<= IF_Rx_ctrl_3[4:0];	// get Rx attenuation value to use when Tx is active
	  end

	  if (IF_Rx_ctrl_0[7:1] == 7'b0001_111)
	begin
	  internal_CW       <= IF_Rx_ctrl_1[0];		// decode internal CW 0 = off, 1 = on
	  sidetone_level    <= IF_Rx_ctrl_2;			// decode CW sidetone volume
	  RF_delay			  <= IF_Rx_ctrl_3;			// decode delay from pressing CW Key to RF out	
	end
	if (IF_Rx_ctrl_0[7:1] == 7'b0010_000)
	begin
		hang[9:2]			<= IF_Rx_ctrl_1;			// decode CW hang time, 10 bits
		hang[1:0]	 		<= IF_Rx_ctrl_2[1:0];
		tone_freq [11:4]  <= IF_Rx_ctrl_3;			// decode sidetone frequency, 12 bits
		tone_freq [3:0]   <= IF_Rx_ctrl_4[3:0];	
	end
	if (IF_Rx_ctrl_0[7:1] == 7'b0010_010)			// decode manual control of RX2 filters/etc on Orion Mk II board s
	begin
		Alex_manual_BPF2 	<= IF_Rx_ctrl_1[5:0];		// decode states for RX2 filters
		Alex_6m_preamp_2	<= IF_Rx_ctrl_1[6];			// decode Alex_6m_preamp_2 state (0 = disable, 1 = enable)
		RX2_GROUND			<=	IF_Rx_ctrl_1[7];			// decode RX2_GROUND state (0 = disable, 1 = enable ground)
		XVTR_Enable			<= IF_Rx_ctrl_2[1];			// decode 8000DLE XVTR Enable (0=disabled, 1=enabled)
		PS_enabled			<= IF_Rx_ctrl_2[6];			// decode PureSignal state (0=disabled, 1=enabled)
	end
  end
end	

// Orion mic tip/ring configuration; mic bias enabled/disabled via C&C command above
//
always @ (posedge IF_clk)
begin
	  if (Orion_tip_ring_select == 1'b1)
		begin	
			PTT_SELECT 		<= 1'b1;						 // set Orion mic ptt to tip
			MIC_SIG_SELECT <= 1'b1;						 // set Orion mic signal to ring
			MICBIAS_SELECT <= 1'b0; 					 // set Orion mic bias to ring
		end
		else begin
			PTT_SELECT 		<= 1'b0;						 // set Orion mic ptt to ring
			MIC_SIG_SELECT <= 1'b0;						 // set Orion mic signal to tip
			MICBIAS_SELECT <= 1'b1;						 // set Orion mic bias to tip
		end			
end	

always @ (posedge IF_clk)
begin 
  if (IF_rst)
  begin // set up default values - 0 for now
    IF_frequency[0]    <= 32'd0;
    IF_frequency[1]    <= 32'd0;
    IF_frequency[2]    <= 32'd0;
    IF_frequency[3]    <= 32'd0;
    IF_frequency[4]    <= 32'd0;
    IF_frequency[5]    <= 32'd0;
    IF_frequency[6]    <= 32'd0;
    IF_frequency[7]    <= 32'd0;
  end
  else if (IF_Rx_save)
  begin
      if (IF_Rx_ctrl_0[7:1] == 7'b0000_001)   // decode IF_frequency[0]
      begin
		  IF_frequency[0]   <= {IF_Rx_ctrl_1, IF_Rx_ctrl_2, IF_Rx_ctrl_3, IF_Rx_ctrl_4}; // Tx frequency
			if (!IF_duplex && (IF_last_chan == 3'b000))
				IF_frequency[1] <= IF_frequency[0]; //				  
		end
		if (IF_Rx_ctrl_0[7:1] == 7'b0000_010) // decode Rx1 frequency
      begin
			if (!IF_duplex && (IF_last_chan == 3'b000)) // Rx1 frequency
				IF_frequency[1] <= IF_frequency[0];				  
         else
				IF_frequency[1] <= {IF_Rx_ctrl_1, IF_Rx_ctrl_2, IF_Rx_ctrl_3, IF_Rx_ctrl_4}; 
		end

		if (IF_Rx_ctrl_0[7:1] == 7'b0000_011) begin // decode Rx2 frequency
			if (IF_last_chan >= 3'b001) IF_frequency[2] <= {IF_Rx_ctrl_1, IF_Rx_ctrl_2, IF_Rx_ctrl_3, IF_Rx_ctrl_4};  // Rx2 frequency
			else IF_frequency[2] <= IF_frequency[0];  
		end 

		if (IF_Rx_ctrl_0[7:1] == 7'b0000_100) begin // decode Rx3 frequency
			if (IF_last_chan >= 3'b010) IF_frequency[3] <= {IF_Rx_ctrl_1, IF_Rx_ctrl_2, IF_Rx_ctrl_3, IF_Rx_ctrl_4};  // Rx3 frequency
			else IF_frequency[3] <= IF_frequency[0];  
		end 

		 if (IF_Rx_ctrl_0[7:1] == 7'b0000_101) begin // decode Rx4 frequency
			if (IF_last_chan >= 3'b011) IF_frequency[4] <= {IF_Rx_ctrl_1, IF_Rx_ctrl_2, IF_Rx_ctrl_3, IF_Rx_ctrl_4};  // Rx4 frequency
			else IF_frequency[4] <= IF_frequency[0];  
		end 

		 if (IF_Rx_ctrl_0[7:1] == 7'b0000_110) begin // decode Rx5 frequency
			if (IF_last_chan >= 3'b100) IF_frequency[5] <= {IF_Rx_ctrl_1, IF_Rx_ctrl_2, IF_Rx_ctrl_3, IF_Rx_ctrl_4};  // Rx5 frequency
			else IF_frequency[5] <= IF_frequency[0];  
		end 

		 if (IF_Rx_ctrl_0[7:1] == 7'b0000_111) begin // decode Rx6 frequency
			if (IF_last_chan >= 3'b101) IF_frequency[6] <= {IF_Rx_ctrl_1, IF_Rx_ctrl_2, IF_Rx_ctrl_3, IF_Rx_ctrl_4};  // Rx6 frequency
			else IF_frequency[6] <= IF_frequency[0];  
		end
	
		 if (IF_Rx_ctrl_0[7:1] == 7'b0001_000) begin // decode Rx7 frequency
			if (IF_last_chan >= 3'b110) IF_frequency[7] <= {IF_Rx_ctrl_1, IF_Rx_ctrl_2, IF_Rx_ctrl_3, IF_Rx_ctrl_4};  // Rx7 frequency
			else IF_frequency[7] <= IF_frequency[0];  
		end 
		
		 
//--------------------------------------------------------------------------------------------------------
 end
end

assign FPGA_PTT = clean_IO5 && (IF_Rx_ctrl_0[0] | CW_PTT); // IF_Rx_ctrl_0 only updated when we get correct sync sequence. CW_PTT is used when internal CW is selected

// 8000DLE PA 
assign DRIVER_PA_EN = FPGA_PTT;  // enable bias on PA only when in Tx mode
assign CTRL_TRSW = (FPGA_PTT && XVTR_Enable);		// to support 8000DLE XVTR operations
wire TXRX_STATUS = FPGA_PTT;

//------------------------------------------------------------
//  Orion on-board attenuators 
//------------------------------------------------------------

// set the input attenuators
wire [4:0] atten_data_in;
wire [4:0] atten2_data_in;
wire [4:0] attenuator1;
wire [4:0] attenuator2;

assign atten_data_in = Orion_atten_enable ? Orion_atten : 5'b0_0000; //(Preamp ? 5'b0_0000 : 5'b1_0100);
assign atten2_data_in = atten2_enable ? Orion_atten2 : 5'b0_0000;
assign attenuator1 = FPGA_PTT ? atten_on_Tx : atten_data_in;
assign attenuator2 = FPGA_PTT ? 5'b1_1111/*atten_on_Tx*/ : atten2_data_in; // temporarily, set ADC2 atten to 31 during xmit
	
Attenuator Attenuator_ADC1 (.clk(CBCLK), .data(attenuator1), .ATTN_CLK(ATTN_CLK), .ATTN_DATA(ATTN_DATA), .ATTN_LE(ATTN_LE));
Attenuator Attenuator_ADC2 (.clk(CBCLK), .data(attenuator2), .ATTN_CLK(ATTN_CLK_2), .ATTN_DATA(ATTN_DATA_2), .ATTN_LE(ATTN_LE_2));


//////////////////////////////////////////////////////////////
//
//		Alex Filter selection
//
//	The frequency sent by PowerSDR is the indicated frequency
//  less the 9kHz IF. In order to select filters at the correct
//  frequency we need to add the IF offset to the current frequency.
//
//////////////////////////////////////////////////////////////

reg	[31:0] C122_LPF_freq;
reg   [31:0] C122_freq;
reg 	[31:0] C122_freq_temp;
reg	[31:0] C122_BPF2_freq;
reg 	[31:0] C122_BPF2_freq_temp;

wire  auto_6m_preamp;
wire  auto_6m_preamp_2;

// The following always block finds the receivers with the lowest RXn numbers that are assigned to ADC0/ADC1 and uses 
// the associated frequencies to determine the BPFs used in auto Alex switching mode for the ADC0 and ADC1 signal paths, respectively.
//
// Note that with hardware platforms that use BPFs on the Rx paths (such as the MkII and 8000DLE PA/filter boards for which
// this firmware is written), it is possible for the user to specify frequencies for the 7 receivers that may be 
// impossible for the hardware to implement.  For example, keeping in mind that only one BPF can be selected at a time, 
// if any two receivers assigned to the same ADC happen to be assigned frequencies that require different BPFs the 
// filter that is chosen by the auto Alex switching code will be the BPF that is appropriate for the frequency that is 
// assigned to the receiver with the lowest RXn number of those receivers that are assigned to that ADC. 
// 
always @ (posedge C122_clk) begin
	if (C122_cbrise) begin
		C122_freq_temp <= C122_frequency_HZ[0];		// initial assignments for sorting
		C122_BPF2_freq_temp <= C122_frequency_HZ[0];
		
		if ((ADC_RX7 == 2'b00) && (C122_frequency_HZ[6] > 32'd0)) C122_freq_temp <= C122_frequency_HZ[6];
		if ((ADC_RX6 == 2'b00) && (C122_frequency_HZ[5] > 32'd0)) C122_freq_temp <= C122_frequency_HZ[5];
		if ((ADC_RX5 == 2'b00) && (C122_frequency_HZ[4] > 32'd0)) C122_freq_temp <= C122_frequency_HZ[4];
		if ((ADC_RX4 == 2'b00) && (C122_frequency_HZ[3] > 32'd0)) C122_freq_temp <= C122_frequency_HZ[3];
		if ((ADC_RX3 == 2'b00) && (C122_frequency_HZ[2] > 32'd0)) C122_freq_temp <= C122_frequency_HZ[2];
		if ((ADC_RX2 == 2'b00) && (C122_frequency_HZ[1] > 32'd0)) C122_freq_temp <= C122_frequency_HZ[1];
		if ((ADC_RX1 == 2'b00) && (C122_frequency_HZ[0] > 32'd0)) C122_freq_temp <= C122_frequency_HZ[0];
		
		if ((ADC_RX7 == 2'b01) && (C122_frequency_HZ[6] > 32'd0)) C122_BPF2_freq_temp <= C122_frequency_HZ[6];
		if ((ADC_RX6 == 2'b01) && (C122_frequency_HZ[5] > 32'd0)) C122_BPF2_freq_temp <= C122_frequency_HZ[5];
		if ((ADC_RX5 == 2'b01) && (C122_frequency_HZ[4] > 32'd0)) C122_BPF2_freq_temp <= C122_frequency_HZ[4];
		if ((ADC_RX4 == 2'b01) && (C122_frequency_HZ[3] > 32'd0)) C122_BPF2_freq_temp <= C122_frequency_HZ[3];
		if ((ADC_RX3 == 2'b01) && (C122_frequency_HZ[2] > 32'd0)) C122_BPF2_freq_temp <= C122_frequency_HZ[2];
		if ((ADC_RX2 == 2'b01) && (C122_frequency_HZ[1] > 32'd0)) C122_BPF2_freq_temp <= C122_frequency_HZ[1];
		if ((ADC_RX1 == 2'b01) && (C122_frequency_HZ[0] > 32'd0)) C122_BPF2_freq_temp <= C122_frequency_HZ[0];

		C122_freq <= C122_freq_temp;
		C122_BPF2_freq <= C122_BPF2_freq_temp;
		
	end //if (C122_cbrise)	
end	// always block

wire [6:0] C122_LPF;
wire [6:0] C122_HPF;
wire [6:0] C122_HPF_PRESET;
wire [6:0] C122_BPF2;
wire [6:0] C122_LPF_auto;
wire [6:0] C122_HPF_auto;
wire [6:0] C122_BPF2_auto;
wire C122_6m_auto_preamp;
wire C122_6m_auto_preamp2;

// turn off 6m rx preamps during tx when Alex filters are under firmware control
assign C122_6m_auto_preamp = FPGA_PTT ? 1'b0 : C122_HPF_auto[6];
assign C122_6m_auto_preamp2 = FPGA_PTT ? 1'b0 : C122_BPF2_auto[6];

// turn 6m preamp on if any rx frequency > 27MHz and in automatic Alex filter selection mode or if selected by manual Alex mode
assign C122_6m_preamp = Alex_manual ? Alex_6m_preamp : C122_6m_auto_preamp /*C122_HPF_auto[6]*/;
assign C122_6m_preamp_2 = Alex_manual ? Alex_6m_preamp_2 : C122_6m_auto_preamp2 /*C122_BPF2_auto[6]*/;

LPF_select Alex_LPF_select(.clock(C122_clk), .frequency(C122_frequency_HZ_Tx), .LPF(C122_LPF_auto));
HPF_select Alex_HPF_select(.clock(C122_clk), .frequency(C122_freq), .HPF(C122_HPF_auto));
BPF2_select Alex_BPF2_select(.clock(C122_clk), .frequency(C122_BPF2_freq), .BPF2(C122_BPF2_auto));

// if Alex_manual mode selected then use HPF, BPF2, & LPF settings provided by user
assign C122_LPF  = Alex_manual ? {1'b0, Alex_manual_LPF} : C122_LPF_auto;
assign C122_HPF_PRESET  = Alex_manual ? {1'b0, Alex_manual_HPF} : C122_HPF_auto;
assign C122_BPF2 = Alex_manual ? {1'b0, Alex_manual_BPF2} : C122_BPF2_auto;
assign C122_HPF = FPGA_PTT ? 7'b0100000 : C122_HPF_PRESET; // BYPASS on TX for PureSignal support

//////////////////////////////////////////////////////////////
//
//		Alex Antenna relay selection
//
//		Antenna relays decode as follows
//
//		TX_relay[1:0]	Antenna selected
//			00			Tx 1
//			01			Tx 2
//			10			Tx 3
//
//		RX_relay[1:0]	Antenna selected
//			00			None
//			01			Rx 1
//			10			Rx 2
//			11			Transverter
//
//		Rout			Rx_1_out
//			0			Not selected
//			1			Selected
//
//////////////////////////////////////////////////////////////

wire C122_ANT1;			
wire C122_ANT2;
wire C122_ANT3;
wire C122_Rx_1_out;
wire C122_Transverter;
wire C122_Rx_2_in;
wire C122_Rx_1_in;

assign C122_Rx_1_out = IF_Rout;

assign C122_ANT1 = (IF_TX_relay == 2'b00) ? 1'b1 : 1'b0; 		// select Tx antenna 1
assign C122_ANT2 = (IF_TX_relay == 2'b01) ? 1'b1 : 1'b0; 		// select Tx antenna 2
assign C122_ANT3 = (IF_TX_relay == 2'b10) ? 1'b1 : 1'b0; 		// select Tx antenna 3

assign C122_Rx_1_in     = (IF_RX_relay == 2'b01) ? 1'b1 : 1'b0; // select Rx antenna 1
assign C122_Rx_2_in     = (IF_RX_relay == 2'b10) ? 1'b1 : 1'b0; // select Rx antenna 2
assign C122_Transverter = (IF_RX_relay == 2'b11) ? 1'b1 : 1'b0; // select Transverter input 


//////////////////////////////////////////////////////////////
//
//		Alex SPI interface
//
//////////////////////////////////////////////////////////////

wire        C122_6m_preamp;
wire			C122_6m_preamp_2;
wire        C122_Tx_red_led;
wire        C122_Rx_red_led;
wire			C122_Rx_red_led2;
wire			C122_Rx_yellow_led;
wire			C122_Rx_yellow_led2;
wire			C122_Tx_yellow_led;
wire			C122_TXRX_STATUS;
wire        C122_TR_relay;
wire			C122_RX_MASTER_IN_SEL;

// activate C122_RX_MASTER_IN_SEL if XVTR mode or EXT1 mode active
assign C122_RX_MASTER_IN_SEL = (C122_Transverter | C122_Rx_2_in) ? 1'b1 : 1'b0;

// define and concatenate the Tx data to send to Alex via SPI
assign C122_TR_relay   = (TR_relay_disable) ? 1'b0 : FPGA_PTT; // turn on TR relay when PTT active unless disabled
assign C122_Tx_red_led = C122_TR_relay; // turn red led on when we Tx                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                           

reg LED_D6;  // test
assign LED_D6 = RX2_GROUND;	// test

wire [47:0] C122_Alex_data;
reg  [47:0] SPI_Alex_data;

// 48-bit Alex data word sent to Orion Mk II hardware via SPI bus...configured to send bit 47 first, 
// IC and pin # refers to Mk II PA/filter board component labels
//assign C122_Alex_data = {
always @  (posedge C122_clk)	
begin
	C122_Alex_data <= {
	// Tx filters/relays
	C122_LPF[6],					// 17/15M (BPF4: K12/K13, U5-pin 17) 		bit 47
	C122_LPF[5],					// 12/10M (BPF5: K15/K16, U5-pin 16)		bit 46
	C122_LPF[4],				   // BYPASS  (BYPASS: K17/K18, U5-pin 15)	bit 45
	C122_TR_relay, 				// LED D7, lit on Tx,(U5-pin 14)				bit 44
	C122_TR_relay,					// TXRX_Relay	(K5, U5-pin 7)					bit 43
	C122_ANT3,						// ANT3		(K14, U5-pin 6)					bit 42
	C122_ANT2,						// ANT2		(K11, U5-pin 5)					bit 41
	C122_ANT1,						// ANT1		(K8, U5-pin 4)						bit 40	
	C122_LPF[3],					// 160M (BPF0: K2/K1, U3-pin 17)				bit 39			
	C122_LPF[2],					// 80M (BPF1: K3/K4, U3-pin 16)				bit 38				
	C122_LPF[1],					// 60/40M (BPF2: K6/K7, U3-pin 15)			bit 37
	C122_LPF[0],					// 30/20M (BPF3: K9/K10, U3-pin 14)			bit 36
	LED_D6, 							//LED D6	(U3-pin 7)								bit 35
	TXRX_STATUS,					// 8000DLE TXRX status							bit 34
	1'b0,								// N.C. 		(U3-pin 5)							bit 33
	1'b0,								// N.C.		(U3-pin 4)							bit 32

	// RX2 filters/relays
	1'b0, 							// REDLED_2		(D21, U13-pin 17)				bit 31
	1'b0,								// N.C.			(U13-pin 16)					bit 30
	1'b0,								// N.C.     	(U13-pin 15)					bit 29
	C122_BPF2[5],					// HF_BYPASS_2 (RL35/RL34, U13-pin 14)		bit 28
	1'b0,								// N.C.			(U13-pin 7)						bit 27
	1'b0,								// N.C.			(U13-pin 6)						bit 26
	1'b0,								// N.C.			(U13-pin 5)						bit 25
	RX2_GROUND,					// RX2_GROUND 	(RX2_GROUND, U13-pin4)		bit 24
	1'b0,								// N.C. 			(U7-pin 17)						bit 23
	C122_BPF2[4],					// BPF1 160M (1.5MHz-2.5MHz)(1.5HPF_2, RL21/RL23, U7-pin 16)		bit 22
	C122_BPF2[3],					// BPF2 80M/60M (2MHz-6MHz) (6.5HPF_2, RL24/RL25, U7-pin 15)		bit 21
	C122_BPF2[2],					// BPF3 40M/30M (5MHz-10MHz)(9.5HPF_2, RL26/RL27, U7-pin 14)		bit 20
	C122_6m_preamp_2,				// 12M/10M/6M LNA2 (6MLNA_2, RL33/RL32, U7-pin 7)	bit 19
	C122_BPF2[1],					// BPF5 (20MHz-35MHz)(20HPF_2, RL30/RL31, U7-pin 6)		bit 18
	C122_BPF2[0],					// BPF4 20M/17M (12MHz-24MHz) (13HPF_2, RL28/RL29, U7-pin 5)		bit 17
	1'b0,								// YELLOWLED_2 (D19, U7-pin 4)							bit 16

	//	RX1 filters/relays	
	1'b0,								// REDLED (D16, U10-pin 17)								bit 15
	C122_RX_MASTER_IN_SEL,		// RX_MASTER_IN_SEL	(RL22, U10-pin 16)				bit 14
	1'b0,								// N.C.					(U10-pin 15)						bit 13	
	C122_HPF[5],					// HPF_BYPASS	(RL14/RL13, U10-pin 14)					bit 12
	C122_Rx_1_in, 					// C122_Rx_1_out, RX_BYPASS_OUT (RL17, U10-pin 7)	bit 11
	1'b0,								// N.C.			(U10-pin 6)									bit 10	
	C122_Rx_2_in,					// EXT1		(RL20, U10-pin 5)								bit 9
	C122_Transverter,				// XVTR IN	(RL10, U10-pin 4)								bit 8	
	1'b0,								// N.C.			(U6-pin 17)									bit 7	
	C122_HPF[4],					// BPF1 160M (1.5MHz-2.5MHz)	(1.5HPF, RL1/RL2, U6-pin 16)			bit 6
	C122_HPF[3],					// BPF2 80M/60M (2MHz-6MHz) 6.5MHz BPF	(6.5HPF, RL3/RL4, U6-pin 15)			bit 5
	C122_HPF[2],					// BPF3 40M/30M (5MHz-10MHz)(9.5HPF, RL5/RL6, U6-pin 14)			bit 4
	C122_6m_preamp,				// 12M/10M/6M LNA	(6MLNA, RL12/RL11, U6-pin 7) 		bit 3
	C122_HPF[1],					// BPF5 (20MHz-35MHz)(20HPF, RL9/RL10, U6-pin 6)			bit 2
	C122_HPF[0],					// BPF4 20M/17M (12MHz-24MHz) (earlier 13MHz BPF)	(13HPF, RL7/RL8, U6-pin 5)				bit 1
	1'b0	 							// RX YELLOW LED (YELLOWLED, D15, U6-pin 4)			bit 0
	};
end		
	
// move Alex data into SPI_clk domain 
cdc_sync #(48)
	SPI_Alex (.siga(C122_Alex_data), .rstb(SPI_Alex_rst), .clkb(CBCLK), .sigb(SPI_Alex_data));
	
SPI Alex_SPI_Tx (.reset(SPI_Alex_rst), .Alex_data(SPI_Alex_data), .SPI_data(Alex_SPI_SDO),
                 .SPI_clock(Alex_SPI_SCK), .Tx_load_strobe(SPI_TX_LOAD),
                 .Rx_load_strobe(SPI_RX_LOAD), .spi_clock(CBCLK));	

//---------------------------------------------------------
//   State Machine to manage PWM interface
//---------------------------------------------------------
/*

    The code loops until there are at least 4 words in the Rx_FIFO.

    The first word is the Left audio followed by the Right audio
    which is followed by I data and finally the Q data.
    	
    The words sent to the D/A converters must be sent at the sample rate
    of the A/D converters (48kHz) so is synced to the negative edge of the CLRCLK (via IF_get_rx_data).
*/

reg   [2:0] IF_PWM_state;      // state for PWM
reg   [2:0] IF_PWM_state_next; // next state for PWM
reg  [15:0] IF_Left_Data;      // Left 16 bit PWM data for D/A converter
reg  [15:0] IF_Right_Data;     // Right 16 bit PWM data for D/A converter
reg  [15:0] IF_I_PWM;          // I 16 bit PWM data for D/A conveter
reg  [15:0] IF_Q_PWM;          // Q 16 bit PWM data for D/A conveter
wire        IF_get_samples;
wire        IF_get_rx_data;

assign IF_get_rx_data = IF_get_samples;

localparam PWM_IDLE     = 0,
           PWM_START    = 1,
           PWM_LEFT     = 2,
           PWM_RIGHT    = 3,
           PWM_I_AUDIO  = 4,
           PWM_Q_AUDIO  = 5;

always @ (posedge IF_clk) 
begin
  if (IF_rst)
    IF_PWM_state   <= #IF_TPD PWM_IDLE;
  else
    IF_PWM_state   <= #IF_TPD IF_PWM_state_next;

  // get Left audio
  if (IF_PWM_state == PWM_LEFT)
    IF_Left_Data   <= #IF_TPD IF_Rx_fifo_rdata;

  // get Right audio
  if (IF_PWM_state == PWM_RIGHT)
    IF_Right_Data  <= #IF_TPD IF_Rx_fifo_rdata;

  // get I audio
  if (IF_PWM_state == PWM_I_AUDIO)
    IF_I_PWM       <= #IF_TPD IF_Rx_fifo_rdata;

  // get Q audio
  if (IF_PWM_state == PWM_Q_AUDIO)
    IF_Q_PWM       <= #IF_TPD IF_Rx_fifo_rdata;

end

always @*
begin
  case (IF_PWM_state)
    PWM_IDLE:
    begin
      IF_Rx_fifo_rreq = 1'b0;

      if (!IF_get_rx_data  || RX_USED[RFSZ:2] == 1'b0 ) // RX_USED < 4
        IF_PWM_state_next = PWM_IDLE;    // wait until time to get the donuts every 48kHz from oven (RX_FIFO)
      else
        IF_PWM_state_next = PWM_START;   // ah! now it's time to get the donuts
    end

    // Start packaging the donuts
    PWM_START:
    begin
      IF_Rx_fifo_rreq    = 1'b1;
      IF_PWM_state_next  = PWM_LEFT;
    end

    // get Left audio
    PWM_LEFT:
    begin
      IF_Rx_fifo_rreq    = 1'b1;
      IF_PWM_state_next  = PWM_RIGHT;
    end

    // get Right audio
    PWM_RIGHT:
    begin
      IF_Rx_fifo_rreq    = 1'b1;
      IF_PWM_state_next  = PWM_I_AUDIO;
    end

    // get I audio
    PWM_I_AUDIO:
    begin
      IF_Rx_fifo_rreq    = 1'b1;
      IF_PWM_state_next  = PWM_Q_AUDIO;
    end

    // get Q audio
    PWM_Q_AUDIO:
    begin
      IF_Rx_fifo_rreq    = 1'b0;
      IF_PWM_state_next  = PWM_IDLE; // truck has left the shipping dock
    end

    default:
    begin
      IF_Rx_fifo_rreq    = 1'b0;
      IF_PWM_state_next  = PWM_IDLE;
    end
  endcase
end

//---------------------------------------------------------
//  Debounce PTT input - active low
//---------------------------------------------------------

wire Orion_micPTT;
assign Orion_micPTT = Orion_micPTT_disable ? 1'b1 : PTT;

debounce de_PTT(.clean_pb(clean_PTT_in), .pb(~Orion_micPTT), .clk(IF_clk));


//---------------------------------------------------------
//  Debounce dot key - active low
//---------------------------------------------------------

debounce de_dot(.clean_pb(clean_dot), .pb(~KEY_DOT), .clk(IF_clk));


//---------------------------------------------------------
//  Debounce dash key - active low
//---------------------------------------------------------

debounce de_dash(.clean_pb(clean_dash), .pb(~KEY_DASH), .clk(IF_clk));

//
// Debounce IO8 external CW digital input 
//
wire				 clean_IO8;						// debounced IO4 CW input

debounce de_IO8(.clean_pb(clean_IO8), .pb(~IO8), .clk(IF_clk));


// debounce IO5 TX INHIBIT digital input
wire clean_IO5;
debounce de_IO5(.clean_pb(clean_IO5), .pb(IO5), .clk(IF_clk));

//---------------------------------------------------------
//    PLLs 
//---------------------------------------------------------


/* 
	Divide the 10MHz reference and 122.88MHz clock to give 80kHz signals.
	Apply these to an EXOR phase detector. If the 10MHz reference is not
	present the EXOR output will be a 80kHz square wave. When passed through 
	the loop filter this will provide a dc level of (3.3/2)v which will
	set the 122.88MHz VCXO to its nominal frequency.
	The selection of the internal or external 10MHz reference for the PLL
	is made using a PCB jumper.

*/

//wire ref_80khz; 
//wire int_ref_80khz;  
//wire ext_ref_80khz;
//wire osc_80khz;
//wire ext_locked;
wire osc_10MHz;
wire DACD_clock;
 
// Use a PLL to divide 10MHz clock to 80kHz
//EXT_C10_PLL PLL3_inst (.inclk0(EXT_OSC_10MHZ), .c0(ext_ref_80khz), .locked(ext_locked));

// Use a PLL to divide 10MHz clock to 80kHz
//C10_PLL PLL2_inst (.inclk0(OSC_10MHZ), .c0(int_ref_80khz), .locked());

// Use a PLL to divide 122.88MHz clock to 80kHz	as backup in case 10MHz source is not present
// Generate 122.88MHz clock at 90 degrees for DAC clock							
//C122_PLL PLL_inst (.inclk0(_122MHz), .c0(osc_80khz), .c1(C122_clk_90), .locked());	
C122_PLL PLL_inst (.inclk0(_122MHz), .c0(osc_10MHz), .locked());	

// generate a phase shifted 122.88MHz clock for the TxDAC (DACD) data
//C122_SHIFT_PLL SHIFT_inst(.inclk0(_122MHz),.c0(DACD_clock), .locked());

//////////////////////////////////////////////////////////////////////

// select which 10MHz-derived 80 KHz source to use to lock the 122.88MHz osc
//assign ref_80khz = ext_locked ? ext_ref_80khz : int_ref_80khz;

// if external 10 MHz signal is present disable the internal 10 MHz oscillator module
//assign OSC_ENABLE = ext_locked;  // OSC_ENABLE low to enable local 10 MHz osc module
	
//Apply to EXOR phase detector 
//assign FPGA_PLL = ref_80khz ^ osc_80khz; 
assign FPGA_PLL = OSC_10MHZ ^ osc_10MHz; 

//-----------------------------------------------------------
//  LED Control  
//-----------------------------------------------------------

/*
	LEDs:  
	
	DEBUG_LED1  	- Lights when an Ethernet broadcast is detected
	DEBUG_LED2  	- Lights when traffic to the boards MAC address is detected
	DEBUG_LED3  	- Lights when detect a received sequence error or ASMI is busy
	DEBUG_LED4 		- Displays state of PHY negotiations - fast flash if no Ethernet connection, slow flash if 100T and on if 1000T
	DEBUG_LED5		- Lights when the PHY receives Ethernet traffic
	DEBUG_LED6  	- Lights when the PHY transmits Ethernet traffic
	DEBUG_LED7  	- Displays state of DHCP negotiations or static IP - on if ACK, slow flash if NAK, fast flash if time out 
					     and long then short flash if static IP
	DEBUG_LED8  	- Lights when sync (0x7F7F7F) received from PC
	DEBUG_LED9  	- Lights when a Metis discovery packet is received
	DEBUG_LED10 	- Lights when a Metis discovery packet reply is sent	
	
	Status_LED	    - Flashes once per second
	
	A LED is flashed for the selected period on the positive edge of the signal.
	If the signal period is greater than the LED period the LED will remain on.


*/

parameter half_second = 10000000; // at 48MHz clock rate

// flash LED1 for ~0.2 seconds whenever we detect a broadcast
Led_flash Flash_LED1(.clock(IF_clk), .signal(broadcast), .LED(DEBUG_LED1), .period(half_second));

// flash LED2 for ~0.2 seconds whenever we detect a packet addressed to this MAC address
Led_flash Flash_LED2(.clock(IF_clk), .signal(this_MAC), .LED(DEBUG_LED2), .period(half_second));

// flash LED3 for ~0.2 seconds when we have detected a received sequence error or ASMI is busy
Led_flash Flash_LED3(.clock(IF_clk), .signal(seq_error || busy), .LED(DEBUG_LED3), .period(half_second)); 

// flash LED5 for ~ 0.2 second whenever the PHY gets data
Led_flash Flash_LED5(.clock(IF_clk), .signal(RX_DV), .LED(DEBUG_LED5), .period(half_second)); 	

// flash LED6 for ~ 0.2 second whenever the PHY sends data
Led_flash Flash_LED6(.clock(IF_clk), .signal(PHY_TX_EN), .LED(DEBUG_LED6), .period(half_second)); 	

// flash LED8 for ~0.2 seconds when we have detected sync 
Led_flash Flash_LED8(.clock(IF_clk), .signal(IF_SYNC_state == SYNC_RX_1_2), .LED(DEBUG_LED8), .period(half_second));

// flash LED9 for ~0.2 seconds whenever we detect a Metis discovery request
Led_flash Flash_LED9(.clock(IF_clk), .signal(METIS_discovery), .LED(DEBUG_LED9), .period(half_second));

// flash LED10 for ~0.2 seconds whenever we detect a Metis discovery reply
Led_flash Flash_LED10(.clock(IF_clk), .signal(METIS_discover_sent), .LED(DEBUG_LED10), .period(half_second));

//Flash Heart beat LED
reg [26:0]HB_counter;
always @(posedge PHY_CLK125) HB_counter = HB_counter + 1'b1;
assign Status_LED = HB_counter[25];  // Blink


//------------------------------------------------------------
//   Multi-state LED Control   - code in Led_control is for active LOW LEDs
//------------------------------------------------------------

parameter clock_speed = 25000000; // 25MHz clock 

// display state of PHY negotiations  - fast flash if no Ethernet connection, slow flash if 100T, on if 1000T
// and swap between fast and slow flash if not full duplex
Led_control #(clock_speed) Control_LED0(.clock(Tx_clock), .on(speed_1000T), .fast_flash(~speed_100T || ~speed_1000T),
										.slow_flash(speed_100T), .vary(!duplex), .LED(DEBUG_LED4));  
										
// display state of DHCP negotiations - on if ACK, slow flash if NAK, fast flash if time out and swap between fast and slow 
// if using a static IP address
Led_control # (clock_speed) Control_LED1(.clock(Tx_clock), .on(DHCP_ACK), .slow_flash(DHCP_NAK),
										.fast_flash(time_out), .vary(Assigned_IP_valid), .LED(DEBUG_LED7));	

function integer clogb2;
input [31:0] depth;
begin
  for(clogb2=0; depth>0; clogb2=clogb2+1)
  depth = depth >> 1;
end
endfunction


endmodule 
