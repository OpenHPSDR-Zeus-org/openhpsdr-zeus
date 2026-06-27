# Angelia.sdc
# 10 April 2014, Joe Martin K5SO
# 17 July 2017, Constrained I/O for Tx attenuator, Phil Harman VK6PH
# 
# modified 11 March 2022 for the Orion=>Angelia port, Joe Martin K5SO

#**************************************************************
# Time Information
#**************************************************************

set_time_format -unit ns -decimal_places 3


#**************************************************************************************
# Create Clock
#**************************************************************************************
# externally generated clocks (with respect to the FPGA)
#
create_clock -period 122.880MHz	-name LTC2208_122MHz    [get_ports LTC2208_122MHz]
create_clock -period 122.880MHz	-name LTC2208_122MHz_2  [get_ports LTC2208_122MHz_2]
create_clock -period 122.880MHz	-name _122MHz		[get_ports _122MHz]
create_clock -period 10.000MHz	-name EXT_OSC_10MHZ 	[get_ports {EXT_OSC_10MHZ}]
create_clock -period  10.000MHz	-name OSC_10MHZ		[get_ports OSC_10MHZ]
create_clock -period 125.000MHz	-name PHY_CLK125	[get_ports PHY_CLK125]
create_clock -period  25.000MHz	-name PHY_RX_CLOCK	[get_ports PHY_RX_CLOCK]

#virtual base clocks on required inputs
create_clock -name virt_PHY_RX_CLOCK 	-period 8.000 
create_clock -name virt_122MHz 	-period 8.138
create_clock -name virt_122MHz_2 	-period 8.138
create_clock -name virt_CBCLK		-period 325.52


derive_pll_clocks

derive_clock_uncertainty

#assign more familiar names!
set IFCLK		PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]
set CMCLK  		PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]
set CBCLK  		PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]
set CLRCLK 		PLL_IF_inst|altpll_component|auto_generated|pll1|clk[3]
set EEPROM_clock 	PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]
set clock_12_5MHz	PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]
#set DAC_clock		SHIFT_inst|altpll_component|auto_generated|pll1|clk[0]
set DAC_clock		PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[0]
# for rev4
set userADC_clk	PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[1]	
set ADCCLK		Orion_ADC:ADC_SPI|SCLK


#*************************************************************************************
# Create Generated Clock
#*************************************************************************************
# NOTE: Whilst derive_pll_clocks constrains PLL clocks if these are connected to an FPGA output pin then a generated
# clock needs to be attached to the pin (and a false path set to it...false path not necessarily...see ADCCLK)

# internally generated clocks
create_generated_clock -name PHY_RX_CLOCK_2 -source PHY_RX_CLOCK 	-divide_by 2 	PHY_RX_CLOCK_2

# PLL generated clocks feeding output pins 
#create_generated_clock -name IFCLK   -source $IFCLK  [get_ports IFCLK]
create_generated_clock -name CBCLK   -source $CBCLK  [get_ports CBCLK]
create_generated_clock -name CMCLK   -source $CMCLK  [get_ports CMCLK]
create_generated_clock -name CLRCIN  -source $CLRCLK [get_ports CLRCIN]
create_generated_clock -name CLRCOUT -source $CLRCLK [get_ports CLRCOUT]
create_generated_clock -name Orion_ADC:ADC_SPI|SCLK -source $userADC_clk -divide_by 4 [get_ports Orion_ADC:ADC_SPI|SCLK]


#**************************************************************
# Set Clock Groups
#**************************************************************

set_clock_groups -asynchronous -group {PHY_CLK125 \
					PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0] \
					PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1] \
					PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2] \
					PHY_RX_CLOCK \
					PHY_RX_CLOCK_2 \
				       } \
				-group {_122MHz \
					LTC2208_122MHz \
					LTC2208_122MHz_2 \
					PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1] \
					PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2] \
					PLL_IF_inst|altpll_component|auto_generated|pll1|clk[3] \
					PLL_inst|altpll_component|auto_generated|pll1|clk[0] \
					PLL_inst|altpll_component|auto_generated|pll1|clk[1] \
					SHIFT_inst|altpll_component|auto_generated|pll1|clk[0] \
					PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[0] \
					PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[1] \
					Orion_ADC:ADC_SPI|SCLK \
				       } \
				-group {OSC_10MHZ}\
				-group {EXT_OSC_10MHZ} \
				-group {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0] }
					




#*************************************************************************************************************
# Set Input Delay
#*************************************************************************************************************
# If setup and hold delays are equal then only need to specify once without max or min

#TLV320 Data in +/- 20nS setup and hold
set_input_delay  20  -clock virt_CBCLK  {CDOUT}

#EEPROM Data in +/- 40nS setup and hold
set_input_delay  40  -clock $EEPROM_clock {SO}

#PHY PHY_MDIO Data in +/- 10nS setup and hold
set_input_delay  10  -clock $EEPROM_clock {PHY_MDIO PHY_INT_N}

#12.5MHz clock for Config EEPROM  +/- 10nS setup and hold
#set_input_delay 10  -clock  $clock_12_5MHz {ASMI_interface:ASMI_int_inst|ASMI:ASMI_inst|#ASMI_altasmi_parallel_smm2:ASMI_altasmi_parallel_smm2_component|cycloneii_asmiblock2~ALTERA_DATA0 }
set_input_delay 10  -clock  $clock_12_5MHz {ASMI_interface:ASMI_int_inst|ASMI:ASMI_inst|ASMI_altasmi_parallel_smm2:ASMI_altasmi_parallel_smm2_component|sd2~ALTERA_DATA0 }

#ADC78H90 Data in +/- 10nS setup and hold
set_input_delay  10  -clock virt_CBCLK {ADCMISO}

# was 1.500 and -0.500
set_input_delay -add_delay -max -clock PHY_CLK125 2.000  {PHY_MDIO PHY_RX[*] RX_DV PHY_INT_N }
set_input_delay -add_delay -min -clock PHY_CLK125 -0.500 {PHY_MDIO PHY_RX[*] RX_DV PHY_INT_N }

set_input_delay -clock LTC2208_122MHz 1.000    { INA[*] SPI_SDI}
set_input_delay -clock LTC2208_122MHz_2 1.000  { INA_2[*]}



#*************************************************************************************************************
# Set Output Delay
#*************************************************************************************************************
# If setup and hold delays are equal then only need to specify once without max or min

# was 1.5000 and -0.500
set_output_delay -add_delay -max -clock PHY_CLK125 2.000  { PHY_TX[*] PHY_TX_EN PHY_TX_CLOCK }
set_output_delay -add_delay -min -clock PHY_CLK125 -0.50 { PHY_TX[*] PHY_TX_EN PHY_TX_CLOCK } 

#48MHz clock
#set_output_delay 1.000 -clock $IFCLK { MICBIAS_ENABLE MICBIAS_SELECT MIC_SIG_SELECT PTT_SELECT }
set_output_delay 1.000 -clock _122MHz { MICBIAS_ENABLE MICBIAS_SELECT MIC_SIG_SELECT PTT_SELECT }
 
# phase shifted 122.88MHz to clock DACD data out to the Tx DAC
set_output_delay 1.20 -clock $DAC_clock { DACD[*] }

#122.88MHz clock  

#set_output_delay 1.000 -clock _122MHz { DACD[*] }
#set_output_delay  1.000 -clock _122MHz   { DACD[*] FPGA_PLL DAC_ALC}
set_output_delay  1.000 -clock _122MHz   { FPGA_PLL DAC_ALC}

# Attenuator - min is referenced to falling edge of clock 
set_output_delay  10  -clock $CMCLK { ATTN_DATA* ATTN_LE* }
set_output_delay  10  -clock $CMCLK { ATTN_DATA* ATTN_LE* } -clock_fall -add_delay

#TLV320 SPI  
set_output_delay  20 -clock $CMCLK { MOSI nCS CMODE}

#TLV320 Data out 
set_output_delay  10 -clock $CBCLK {CDIN}

#Alex  uses CBCLK
set_output_delay  10 -clock $CBCLK { SPI_SDO J15_5 J15_6}

#EEPROM (2.5MHz)
set_output_delay  40 -clock $EEPROM_clock {SCK SI CS}

#ADC78H90 
#set_output_delay  10 -clock $CBCLK {ADCMOSI nADCCS}
# for rev4
set_output_delay  10 -clock $userADC_clk { ADCMOSI nADCCS ADCCLK}

#PHY (2.5MHz)
set_output_delay  10 -clock $EEPROM_clock {PHY_MDIO}

#12.5MHz clock for Config EEPROM  +/- 10nS
#set_output_delay  10 -clock $clock_12_5MHz {ASMI_interface:ASMI_int_inst|ASMI:ASMI_inst|#ASMI_altasmi_parallel_smm2:ASMI_altasmi_parallel_smm2_component|cycloneii_asmiblock2~ALTERA_DCLK ASMI_interface:ASMI_int_inst|ASMI:ASMI_inst|#ASMI_altasmi_parallel_smm2:ASMI_altasmi_parallel_smm2_component|cycloneii_asmiblock2~ALTERA_SCE ASMI_interface:ASMI_int_inst|ASMI:ASMI_inst|#ASMI_altasmi_parallel_smm2:ASMI_altasmi_parallel_smm2_component|cycloneii_asmiblock2~ALTERA_SDO }
set_output_delay  10 -clock $clock_12_5MHz {ASMI_interface:ASMI_int_inst|ASMI:ASMI_inst|ASMI_altasmi_parallel_smm2:ASMI_altasmi_parallel_smm2_component|sd2~ALTERA_DCLK ASMI_interface:ASMI_int_inst|ASMI:ASMI_inst|ASMI_altasmi_parallel_smm2:ASMI_altasmi_parallel_smm2_component|sd2~ALTERA_SCE ASMI_interface:ASMI_int_inst|ASMI:ASMI_inst|ASMI_altasmi_parallel_smm2:ASMI_altasmi_parallel_smm2_component|sd2~ALTERA_SDO }



#**************************************************************************************
# Set Maximum Delay (for setup or recovery; low-level, over-riding timing adjustments)
#**************************************************************************************

set_max_delay -from _122MHz -to _122MHz 14
#set_max_delay -from _122MHz -to SHIFT_inst|altpll_component|auto_generated|pll1|clk[0] 6
set_max_delay -from _122MHz -to PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[0] 9

set_max_delay -from LTC2208_122MHz -to LTC2208_122MHz 16

set_max_delay -from PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1] -to PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1] 26
set_max_delay -from PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1] -to PHY_CLK125 9

set_max_delay -from PLL_inst|altpll_component|auto_generated|pll1|clk[0] -to _122MHz 6

#set_max_delay -from SHIFT_inst|altpll_component|auto_generated|pll1|clk[0] -to PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2] 20
set_max_delay -from PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[0] -to PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2] 20

#set_max_delay -from SHIFT_inst|altpll_component|auto_generated|pll1|clk[0] -to SHIFT_inst|altpll_component|auto_generated|pll1|clk[0] 10

set_max_delay -from virt_CBCLK -to PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0] 25


# rev1 additional delays for Orion=>Angelia port...to achieve timing closure
set_max_delay -from PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[1] -to PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0] 7
set_max_delay -from PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[1] -to PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2] 22
set_max_delay -from PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[0] -to PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[0] 12
set_max_delay -from PLL_inst|altpll_component|auto_generated|pll1|clk[0] -to _122MHz 10

# rev2 additional delays...to achieve at least 1nSec positive slack for all I/O and core paths in the FPGA
set_max_delay -from PLL_IF_inst|altpll_component|auto_generated|pll1|clk[3] -to LTC2208_122MHz 10
set_max_delay -from PLL_IF_inst|altpll_component|auto_generated|pll1|clk[3] -to _122MHz 11
set_max_delay -from LTC2208_122MHz_2 -to LTC2208_122MHz_2 10
set_max_delay -from PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1] -to LTC2208_122MHz 10


#************************************************************************************
# Set Minimum Delay (for hold or removal; low-level, over-riding timing adjustments)
#************************************************************************************
# for rev2
set_min_delay -from LTC2208_122MHz_2 -to LTC2208_122MHz_2	-1
set_min_delay -from PHY_RX_CLOCK_2 -to PHY_RX_CLOCK_2  -1
set_min_delay -from PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0] -to PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0] -1
set_min_delay -from _122MHz -to _122MHz -1
set_min_delay -from LTC2208_122MHz -to LTC2208_122MHz -1
set_min_delay -from PHY_RX_CLOCK -to PHY_RX_CLOCK -1
set_min_delay -from PLL_IF_inst|altpll_component|auto_generated|pll1|clk[3] -to PLL_IF_inst|altpll_component|auto_generated|pll1|clk[3] -1
set_min_delay -from PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1] -to PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1] -1
set_min_delay -from PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[1] -to PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[1] -1
set_min_delay -from PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1] -to PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1] -1
set_min_delay -from PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2] -to PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2] -1
set_min_delay -from PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2] -to PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2] -1
set_min_delay -from PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0] -to PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0] -1
set_min_delay -from PHY_CLK125 -to PHY_CLK125 -1
set_min_delay -from PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2] -to PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0] -1
set_min_delay -from PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[0] -to PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[0] -1
set_min_delay -from PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[1] -to PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0] -1
set_min_delay -from PHY_RX_CLOCK_2 -to PHY_RX_CLOCK -1
set_min_delay -from PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2] -to PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1] -1
set_min_delay -from LTC2208_122MHz -to LTC2208_122MHz_2 -1

#**************************************************************
# Set False Paths
#**************************************************************

set_false_path -from [get_clocks {LTC2208_122MHz_2}] -to [get_clocks {LTC2208_122MHz}]

# Set false paths to remove irrelevant setup and hold analysis 
set_false_path -fall_from  virt_PHY_RX_CLOCK -rise_to PHY_RX_CLOCK -setup
set_false_path -rise_from  virt_PHY_RX_CLOCK -fall_to PHY_RX_CLOCK -setup
set_false_path -fall_from  virt_PHY_RX_CLOCK -fall_to PHY_RX_CLOCK -hold
set_false_path -rise_from  virt_PHY_RX_CLOCK -rise_to PHY_RX_CLOCK -hold

# Set false path to generated clocks that feed output pins
#set_false_path -to [get_ports {CMCLK CBCLK CLRCIN CLRCOUT ATTN_CLK* SSCK ADCCLK SPI_SCK PHY_MDC}]
# for rev4
set_false_path -to [get_ports {CMCLK CBCLK CLRCIN CLRCOUT ATTN_CLK* SSCK SPI_SCK PHY_MDC}]

# don't need fast paths to the LEDs and adhoc outputs so set false paths so Timing will be ignored
set_false_path -to {Status_LED DEBUG_LED* DITH* FPGA_PTT  NCONFIG  RAND*  USEROUT*  DRIVER_PA_EN CTRL_TRSW TX_ATTEN*}

#don't need fast paths from the following inputs
set_false_path -from  {ANT_TUNE IO*  KEY_DASH KEY_DOT OVERFLOW* PTT TX_ATTEN_SELECT MODE2}


