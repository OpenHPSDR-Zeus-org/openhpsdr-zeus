# Hermes.sdc
#
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
create_clock -period 122.880MHz	-name _122MHz				[get_ports _122MHz]
create_clock -period  10.000MHz	-name OSC_10MHZ			[get_ports OSC_10MHZ]
create_clock -period 125.000MHz	-name PHY_CLK125			[get_ports PHY_CLK125]
create_clock -period  25.000MHz	-name PHY_RX_CLOCK		[get_ports PHY_RX_CLOCK]

#virtual base clocks on required inputs
create_clock -name virt_PHY_RX_CLOCK 	-period 8.000 
create_clock -name virt_122MHz 			-period 8.138
create_clock -name virt_CBCLK				-period 325.52


derive_pll_clocks

derive_clock_uncertainty

#assign more familiar names!
set CMCLK  			PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]
set CBCLK  			PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]
set CLRCLK 			PLL_IF_inst|altpll_component|auto_generated|pll1|clk[3]
set EEPROM_clock 	PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]




#*************************************************************************************
# Create Generated Clock
#*************************************************************************************
# NOTE: Whilst derive_pll_clocks constrains PLL clocks if these are connected to an FPGA output pin then a generated
# clock needs to be attached to the pin and a false path set to it

# internally generated clocks
create_generated_clock -name PHY_RX_CLOCK_2 -source PHY_RX_CLOCK 	-divide_by 2 	PHY_RX_CLOCK_2

# PLL generated clocks feeding output pins 
create_generated_clock -name CBCLK   -source $CBCLK  [get_ports CBCLK]
create_generated_clock -name CMCLK   -source $CMCLK  [get_ports CMCLK]
create_generated_clock -name CLRCIN  -source $CLRCLK [get_ports CLRCIN]
create_generated_clock -name CLRCOUT -source $CLRCLK [get_ports CLRCOUT]



#*************************************************************************************
# Set Clock Groups
#*************************************************************************************
# Note: output clock c0 (48.034909 MHz) of PLL_IF_inst is asynchronous with input source clock inclk0 (122.88MHz)

set_clock_groups -asynchronous -group {PHY_CLK125 \
					PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0] \
					PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1] \
					PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2] \
					PHY_RX_CLOCK \
					PHY_RX_CLOCK_2 \
					} \
				-group {_122MHz \
					LTC2208_122MHz \
					PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1] \
					PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2] \
					PLL_IF_inst|altpll_component|auto_generated|pll1|clk[3] \
					PLL_inst|altpll_component|auto_generated|pll1|clk[0] \
					} \
				-group {PLL2_inst|altpll_component|auto_generated|pll1|clk[0]}\
				-group {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0] }
										 

#*************************************************************************************************************
# set input delay
#*************************************************************************************************************
# If setup and hold delays are equal then only need to specify once without max or min

#TLV320 Data in +/- 20nS setup and hold
set_input_delay  20  -clock virt_CBCLK  {CDOUT}

#EEPROM Data in +/- 40nS setup and hold
set_input_delay  40  -clock $EEPROM_clock {SO}

#PHY PHY_MDIO Data in +/- 10nS setup and hold
set_input_delay  10  -clock $EEPROM_clock {PHY_MDIO PHY_INT_N}

#ADC78H90 Data in +/- 10nS setup and hold
set_input_delay  10  -clock virt_CBCLK {ADCMISO}

set_input_delay -add_delay -max -clock PHY_CLK125 1.500  {PHY_MDIO PHY_RX[*] RX_DV PHY_INT_N }
set_input_delay -add_delay -min -clock PHY_CLK125 -0.500 {PHY_MDIO PHY_RX[*] RX_DV PHY_INT_N }

set_input_delay -clock LTC2208_122MHz 1.000  { INA[*] SPI_SDI}



#*************************************************************************************************************
# set output delay
#*************************************************************************************************************
# If setup and hold delays are equal then only need to specify once without max or min

set_output_delay -add_delay -max -clock PHY_CLK125 1.500  { PHY_TX[*] PHY_TX_EN PHY_TX_CLOCK }
set_output_delay -add_delay -min -clock PHY_CLK125 -0.500 { PHY_TX[*] PHY_TX_EN PHY_TX_CLOCK } 

#122.88MHz clock for Tx DAC 
set_output_delay  1.000 -clock _122MHz   { DACD[*] FPGA_PLL DAC_ALC}

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
set_output_delay  10 -clock $CBCLK {ADCMOSI nADCCS}

#PHY (2.5MHz)
set_output_delay  10 -clock $EEPROM_clock {PHY_MDIO}



#*************************************************************************************************************
# Set Maximum Delay
#*************************************************************************************************************
#

set_max_delay -from _122MHz -to _122MHz 15

set_max_delay -from LTC2208_122MHz -to LTC2208_122MHz 15
set_max_delay -from LTC2208_122MHz -to _122MHz 13
set_max_delay -from LTC2208_122MHz -to PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2] 10

#set_max_delay -from PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0] -to PHY_CLK125 16

set_max_delay -from PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1] -to PHY_CLK125 12

#set_max_delay -from PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2] -to _122MHz 14

set_max_delay -from PLL_IF_inst|altpll_component|auto_generated|pll1|clk[3] -to _122MHz 10


#*************************************************************************************************************
# Set Minimum Delay
#*************************************************************************************************************

set_min_delay -from _122MHz -to _122MHz -1

#set_min_delay -from LTC2208_122MHz -to LTC2208_122MHz -2

#**************************************************************
# Set False Paths
#**************************************************************

# Set false paths to remove irrelevant setup and hold analysis 
set_false_path -fall_from  virt_PHY_RX_CLOCK -rise_to PHY_RX_CLOCK -setup
set_false_path -rise_from  virt_PHY_RX_CLOCK -fall_to PHY_RX_CLOCK -setup
set_false_path -fall_from  virt_PHY_RX_CLOCK -fall_to PHY_RX_CLOCK -hold
set_false_path -rise_from  virt_PHY_RX_CLOCK -rise_to PHY_RX_CLOCK -hold

# Set false path to generated clocks that feed output pins
set_false_path -to [get_ports {CMCLK CBCLK CLRCIN CLRCOUT ATTN_CLK SSCK ADCCLK SPI_SCK PHY_MDC}]

# don't need fast paths to the LEDs and adhoc outputs so set false paths so Timing will be ignored
set_false_path -to {Status_LED DEBUG_LED* DITH FPGA_PTT  NCONFIG  RAND  USEROUT*}

#don't need fast paths from the following inputs
set_false_path -from  {ANT_TUNE IO*  KEY_DASH KEY_DOT OVERFLOW PTT}




