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
create_clock -period 122.880MHz	-name _122MHz		[get_ports _122MHz]
create_clock -period  10.000MHz	-name OSC_10MHZ		[get_ports OSC_10MHZ]
create_clock -period 125.000MHz	-name PHY_CLK125	[get_ports PHY_CLK125]
create_clock -period  25.000MHz	-name PHY_RX_CLOCK	[get_ports PHY_RX_CLOCK]

#virtual base clocks on all inputs
create_clock -name virt_PHY_RX_CLOCK 	-period 8.000 
create_clock -name virt_122MHz 			-period 8.138
create_clock -name virt_CBCLK				-period 325.52
create_clock -name virt_2_5_MHz        -period 400.000


derive_pll_clocks

derive_clock_uncertainty

#assign more familiar names!
set clock_12_5MHz PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]
set clock_2_5MHz  PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]
set IF_clk PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]
set CBCLK Hermes_clk_lrclk_gen:clrgen|BCLK



#*************************************************************************************
# Create Generated Clock
#*************************************************************************************
# internally generated clocks

create_generated_clock -name Hermes_clk_lrclk_gen:clrgen|BCLK 	-source [get_ports LTC2208_122MHz] 	-divide_by 40 	Hermes_clk_lrclk_gen:clrgen|BCLK 
create_generated_clock -name spc[2] -source Hermes_clk_lrclk_gen:clrgen|BCLK 	-divide_by 8 spc[2]	
create_generated_clock -name data_clk 	-source $IF_clk -divide_by 4 
create_generated_clock -name sidetone:sidetone_inst|sidetone_clock 	-source PHY_CLK125 -divide_by 690 	sidetone:sidetone_inst|sidetone_clock
create_generated_clock -name pro_count[2] -source [get_pins {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[3]}] 	-divide_by 8 	pro_count[2]
create_generated_clock -name tx_output_clock -source PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2] [get_ports {PHY_TX_CLOCK}]
create_generated_clock -name PHY_RX_CLOCK_2 -source PHY_RX_CLOCK -divide_by 2 PHY_RX_CLOCK_2
create_generated_clock -name Attenuator:Attenuator_inst|clk_2 -source  $IF_clk -divide_by 2 Attenuator:Attenuator_inst|clk_2

#*************************************************************************************
# Set Clock Groups
#*************************************************************************************
# Note: output clock c0 (48.034909 MHz) of PLL_IF_inst is asynchronous with input source clock inclk0 (122.88MHz)

set_clock_groups -asynchronous -group {PHY_CLK125 \
					PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1] \
					PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2] \
					} \
					-group {_122MHz \
   				LTC2208_122MHz \
					Hermes_clk_lrclk_gen:clrgen|BCLK \
					spc[2] \
					PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1] \
					PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2] \
					pro_count[2] \
					sidetone:sidetone_inst|sidetone_clock \
					} \
				-group {PLL_inst|altpll_component|auto_generated|pll1|clk[0]}\
				-group {PLL2_inst|altpll_component|auto_generated|pll1|clk[0]} \
				-group {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]} \
				-group {PHY_RX_CLOCK}
										 

#*************************************************************************************************************
# set input delay
#*************************************************************************************************************
# If setup and hold delays are equal then only need to specify once without max or min 

#12.5MHz clock for Config EEPROM  +/- 10nS setup and hold
set_input_delay -add_delay  10  -clock  virt_2_5_MHz { ASMI_interface:ASMI_int_inst|ASMI:ASMI_inst|ASMI_altasmi_parallel_cv82:ASMI_altasmi_parallel_cv82_component|cycloneii_asmiblock2~ALTERA_DATA0}

# data from LTC2208 +/- 2nS setup and hold 
set_input_delay -add_delay  2.000 -clock virt_122MHz  { INA[*]}

#PHY Data in 
set_input_delay 0.8  -clock virt_PHY_RX_CLOCK {PHY_RX[*] RX_DV} 

#TLV320 Data in +/- 20nS setup and hold
set_input_delay -add_delay 20  -clock virt_CBCLK  {CDOUT}

#EEPROM Data in +/- 40nS setup and hold
set_input_delay -add_delay 40  -clock virt_2_5_MHz {SO}

#PHY PHY_MDIO Data in +/- 10nS setup and hold
set_input_delay -add_delay 10  -clock virt_2_5_MHz {PHY_MDIO PHY_INT_N}

#ADC78H90 Data in +/- 10nS setup and hold
set_input_delay -add_delay 10  -clock virt_CBCLK {ADCMISO}


#*************************************************************************************************************
# set output delay
#*************************************************************************************************************

# If setup and hold delays are equal then only need to specify once without max or min 

#12.5MHz clock for Config EEPROM  +/- 10nS
set_output_delay -add_delay 10 -clock $clock_12_5MHz {ASMI_interface:ASMI_int_inst|ASMI:ASMI_inst|ASMI_altasmi_parallel_cv82:ASMI_altasmi_parallel_cv82_component|cycloneii_asmiblock2~ALTERA_SCE ASMI_interface:ASMI_int_inst|ASMI:ASMI_inst|ASMI_altasmi_parallel_cv82:ASMI_altasmi_parallel_cv82_component|cycloneii_asmiblock2~ALTERA_SDO ASMI_interface:ASMI_int_inst|ASMI:ASMI_inst|ASMI_altasmi_parallel_cv82:ASMI_altasmi_parallel_cv82_component|cycloneii_asmiblock2~ALTERA_DCLK}

#122.88MHz clock for Tx DAC 
set_output_delay -add_delay 1.000 -clock _122MHz   { DACD[*] FPGA_PLL DAC_ALC}

# Generated clocks should be constrained with 1nS setup and hold and added to Groups

set_output_delay -add_delay 1.0  -clock $CBCLK { CMCLK CBCLK }

set_output_delay -add_delay 1.0  -clock data_clk {ATTN_CLK SSCK}

set_output_delay -add_delay 1.0  -clock $CBCLK { ADCCLK SPI_SCK}

set_output_delay -add_delay 1.0  -clock $clock_2_5MHz {PHY_MDC}

#PHY
set_output_delay -add_delay 1.0  -clock tx_output_clock [get_ports {PHY_TX* PHY_TX_EN}]

# Attenuators - min is referenced to falling edge of clock 
set_output_delay -add_delay 10  -clock data_clk { ATTN_DATA ATTN_LE }
set_output_delay -add_delay 10  -clock data_clk { ATTN_DATA ATTN_LE } -clock_fall

#TLV320 SPI  
set_output_delay -add_delay 20 -clock data_clk { MOSI nCS}

#TLV320 Data out 
set_output_delay -add_delay 10 -clock $CBCLK {CLRCIN CLRCOUT CDIN CMODE}

#Alex  uses CBCLK/4
set_output_delay -add_delay 10 -clock spc[2] { SPI_SDO J15_5 J15_6}

#EEPROM (2.5MHz)
set_output_delay -add_delay 40 -clock $clock_2_5MHz {SCK SI CS}

#ADC78H90 
set_output_delay -add_delay 10 -clock $CBCLK {ADCMOSI nADCCS}

#PHY (2.5MHz)
set_output_delay -add_delay 10 -clock $clock_2_5MHz {PHY_MDIO}



#*************************************************************************************************************
# Set Maximum Delay
#*************************************************************************************************************
#
set_max_delay -from PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1] -to data_clk 26
set_max_delay -from PLL_inst|altpll_component|auto_generated|pll1|clk[1] -to _122MHz 5
set_max_delay -from PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1] -to PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1] 10

set_max_delay -from PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2] -to Hermes_clk_lrclk_gen:clrgen|BCLK 2
set_max_delay -from PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2] -to pro_count[2] 1
set_max_delay -from PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2] -to spc[2] 10

set_max_delay -from sidetone:sidetone_inst|sidetone_clock -to sidetone:sidetone_inst|sidetone_clock 13
set_max_delay -from sidetone:sidetone_inst|sidetone_clock -to LTC2208_122MHz 13

set_max_delay -from PHY_RX_CLOCK_2 -to PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0] 11

set_max_delay -from PLL_inst|altpll_component|auto_generated|pll1|clk[1] -to _122MHz 5
set_max_delay -from PLL_inst|altpll_component|auto_generated|pll1|clk[1] -to PLL_inst|altpll_component|auto_generated|pll1|clk[1] 5

set_max_delay -from pro_count[2] -to PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2] 8

set_max_delay -from _122MHz -to _122MHz 13

set_max_delay -from PHY_RX_CLOCK_2 -to pro_count[2] 7

set_max_delay -from LTC2208_122MHz -to Hermes_clk_lrclk_gen:clrgen|BCLK 14
set_max_delay -from LTC2208_122MHz -to spc[2] 13

set_max_delay -from pro_count[2] -to Attenuator:Attenuator_inst|clk_2 7

set_max_delay -from PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0] -to PLL_inst|altpll_component|auto_generated|pll1|clk[1] 4

set_max_delay -from pro_count[2] -to sidetone:sidetone_inst|sidetone_clock 9

set_max_delay -from PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1] -to PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2] 21

set_max_delay -from virt_PHY_RX_CLOCK -to PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0] 4

set_max_delay -from LTC2208_122MHz -to PLL_inst|altpll_component|auto_generated|pll1|clk[1] 7

set_max_delay -from LTC2208_122MHz -to _122MHz 10

set_max_delay -from Attenuator:Attenuator_inst|clk_2 -to data_clk 22

set_max_delay -from virt_CBCLK -to LTC2208_122MHz 18

set_max_delay -from LTC2208_122MHz -to LTC2208_122MHz 12


#*************************************************************************************************************
# Set Minimum Delay
#*************************************************************************************************************
#
set_min_delay -from PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2] -to pro_count[2] -2

set_min_delay -from sidetone:sidetone_inst|sidetone_clock -to LTC2208_122MHz -2 

set_min_delay -from pro_count[2] -to sidetone:sidetone_inst|sidetone_clock -2


#*************************************************************************************************************
# Set False Path
#*************************************************************************************************************
# don't need fast paths to the LEDs and adhoc outputs so set false paths so Timing will be ignored
set_false_path -from * -to { Status_LED DEBUG_LED* DITH FPGA_PTT  NCONFIG  RAND USEROUT* }

#don't need fast paths from the following inputs
set_false_path -from  {ANT_TUNE IO*  KEY_DASH KEY_DOT OVERFLOW PTT MODE2} -to *

