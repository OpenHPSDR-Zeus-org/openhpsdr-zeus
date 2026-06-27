# 2015 Aug  8 - Hermes.sdc, direct copy from Angelia.sdc
#          12 - Increased tx_output_clock delay


#**************************************************************
# Time Information
#**************************************************************

set_time_format -unit ns -decimal_places 3


#**************************************************************
# Create Clock (base clocks, external to the FPGA)
#**************************************************************

create_clock -name _122MHz 				-period 8.138 	[get_ports {_122MHz}]
create_clock -name LTC2208_122MHz 		-period 8.138 	[get_ports {LTC2208_122MHz}]
create_clock -name OSC_10MHZ 				-period 100.000 [get_ports {OSC_10MHZ}]
create_clock -name PHY_CLK125				-period 8.000 	[get_ports {PHY_CLK125}]
create_clock -name PHY_RX_CLOCK 			-period 8.000 	-waveform {2 6} [get_ports {PHY_RX_CLOCK}]

#virtual base clocks on required inputs
create_clock -name virt_PHY_RX_CLOCK 	-period 8.000
create_clock -name virt_122MHz 			-period 8.138
create_clock -name virt_CBCLK				-period 325.52


derive_pll_clocks

derive_clock_uncertainty

#assign more familiar names!
set CMCLK  PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]
set CBCLK  PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]
set CLRCLK PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]

set clock_12_5MHz network_inst|rgmii_send_inst|tx_pll_inst|altpll_component|auto_generated|pll1|clk[2]

set clock_2_5MHz  network_inst|rgmii_send_inst|tx_pll_inst|altpll_component|auto_generated|pll1|clk[4]


#**************************************************************
# Create Generated Clock (internal to the FPGA)
#**************************************************************
# NOTE: Whilst derive_pll_clocks constrains PLL clocks if these are connected to an FPGA output pin then a generated
# clock needs to be attached to the pin and a false path set to it

create_generated_clock -name tx_output_clock -source [get_pins {network_inst|rgmii_send_inst|tx_pll_inst|altpll_component|auto_generated|pll1|clk[1]}]  [get_ports {PHY_TX_CLOCK}]

# data_clock = CMCLK/2 used by Attenuator and TLV320 SPI
create_generated_clock -name data_clk -source $CMCLK -divide 2 

# data_clk2 = CBCLK/4 
create_generated_clock -name data_clk2 -source $CBCLK -divide 4

# PLL generated clocks feeding output pins 
create_generated_clock -name CBCLK   -source $CBCLK  [get_ports CBCLK]
create_generated_clock -name CMCLK   -source $CMCLK  [get_ports CMCLK]
create_generated_clock -name CLRCIN  -source $CLRCLK [get_ports CLRCIN]
create_generated_clock -name CLRCOUT -source $CLRCLK [get_ports CLRCOUT]


#************************************************************** 
# Set Input Delay
#**************************************************************

# If setup and hold delays are equal then only need to specify once without max or min 

#12.5MHz clock for Config EEPROM  +/- 10nS setup and hold
#set_input_delay 10  -clock  $clock_12_5MHz { ASMI_interface:ASMI_int_inst|ASMI:ASMI_inst|ASMI_altasmi_parallel_cv82:ASMI_altasmi_parallel_cv82_component|cycloneii_asmiblock2~ALTERA_DATA0}

# data from LTC2208 +/- 2nS setup and hold 
set_input_delay 2.000 -clock virt_122MHz  { INA*}

#PHY Data in 
#set_input_delay  0.8  -clock virt_PHY_RX_CLOCK {PHY_RX[*] RX_DV}  
#set_input_delay  0.8  -clock virt_PHY_RX_CLOCK {PHY_RX[*] RX_DV}  -clock_fall -add_delay

set_input_delay  -max 0.8  -clock virt_PHY_RX_CLOCK [get_ports {PHY_RX[*] RX_DV}]
set_input_delay  -min -0.8 -clock virt_PHY_RX_CLOCK -add_delay [get_ports {PHY_RX[*] RX_DV}]
set_input_delay  -max 0.8 -clock virt_PHY_RX_CLOCK -clock_fall -add_delay [get_ports {PHY_RX[*] RX_DV}]
set_input_delay  -min -0.8 -clock virt_PHY_RX_CLOCK -clock_fall -add_delay [get_ports {PHY_RX[*] RX_DV}]

#TLV320 Data in +/- 20nS setup and hold
set_input_delay  20  -clock virt_CBCLK  {CDOUT}

#EEPROM Data in +/- 40nS setup and hold
set_input_delay  40  -clock $clock_2_5MHz {SO}

#PHY PHY_MDIO Data in +/- 10nS setup and hold
set_input_delay  10  -clock $clock_2_5MHz {PHY_MDIO PHY_INT_N}

#ADC78H90 Data in +/- 10nS setup and hold
set_input_delay  10  -clock data_clk2 {ADCMISO}


#**************************************************************
# Set Output Delay
#**************************************************************

# If setup and hold delays are equal then only need to specify once without max or min 

#12.5MHz clock for Config EEPROM  +/- 10nS
#set_output_delay  10 -clock $clock_12_5MHz {ASMI_interface:ASMI_int_inst|ASMI:ASMI_inst|ASMI_altasmi_parallel_cv82:ASMI_altasmi_parallel_cv82_component|cycloneii_asmiblock2~ALTERA_SCE ASMI_interface:ASMI_int_inst|ASMI:ASMI_inst|ASMI_altasmi_parallel_cv82:ASMI_altasmi_parallel_cv82_component|cycloneii_asmiblock2~ALTERA_SDO ASMI_interface:ASMI_int_inst|ASMI:ASMI_inst|ASMI_altasmi_parallel_cv82:ASMI_altasmi_parallel_cv82_component|cycloneii_asmiblock2~ALTERA_DCLK}

#122.88MHz clock for Tx DAC 
set_output_delay  1.000 -clock _122MHz   { DACD[*] }

#PHY
set_output_delay  -max 1.0  -clock tx_output_clock [get_ports {PHY_TX* PHY_TX_EN}]
set_output_delay  -min -0.8 -clock tx_output_clock [get_ports {PHY_TX* PHY_TX_EN}]  -add_delay
set_output_delay  -max 1.0  -clock tx_output_clock [get_ports {PHY_TX* PHY_TX_EN}]  -clock_fall -add_delay
set_output_delay  -min -0.8 -clock tx_output_clock [get_ports {PHY_TX* PHY_TX_EN}]  -clock_fall -add_delay

# Attenuators - min is referenced to falling edge of clock 
set_output_delay  10  -clock data_clk { ATTN_DATA* ATTN_LE* }
set_output_delay  10  -clock data_clk { ATTN_DATA* ATTN_LE* } -clock_fall -add_delay

#TLV320 SPI  
set_output_delay  20 -clock data_clk { MOSI nCS}

#TLV320 Data out 
set_output_delay  10 -clock $CBCLK {CDIN CMODE}

#Alex  uses CBCLK/4
set_output_delay  10 -clock data_clk2 { SPI_SDO J15_5 J15_6}

#EEPROM (2.5MHz)
set_output_delay  40 -clock $clock_2_5MHz {SCK SI CS}

#ADC78H90 
set_output_delay  10 -clock data_clk2 {ADCMOSI nADCCS}

#PHY (2.5MHz)
set_output_delay  10 -clock $clock_2_5MHz {PHY_MDIO}

#**************************************************************
# Set Clock Groups
#**************************************************************
set_clock_groups -asynchronous  -group { \
					LTC2208_122MHz \
					_122MHz \
					PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0] \
					PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1] \
					PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2] \
					data_clk \
					data_clk2 \
					CBCLK \
					CMCLK \
					CLRCIN \
					CLRCOUT \
				       } \
				-group { \
					PHY_CLK125 \
					network_inst|rgmii_send_inst|tx_pll_inst|altpll_component|auto_generated|pll1|clk[0] \
					network_inst|rgmii_send_inst|tx_pll_inst|altpll_component|auto_generated|pll1|clk[1] \
					network_inst|rgmii_send_inst|tx_pll_inst|altpll_component|auto_generated|pll1|clk[2] \
					network_inst|rgmii_send_inst|tx_pll_inst|altpll_component|auto_generated|pll1|clk[4] \
					tx_output_clock \
				       } \
				-group {OSC_10MHZ PLL2_inst|altpll_component|auto_generated|pll1|clk[0]} \
				-group {PLL_inst|altpll_component|auto_generated|pll1|clk[0]} \
				-group {PHY_RX_CLOCK }
				
					
#**************************************************************
# Set Maximum Delay
#************************************************************** 

set_max_delay -from network_inst|rgmii_send_inst|tx_pll_inst|altpll_component|auto_generated|pll1|clk[0] -to network_inst|rgmii_send_inst|tx_pll_inst|altpll_component|auto_generated|pll1|clk[0] 18

set_max_delay -from network_inst|rgmii_send_inst|tx_pll_inst|altpll_component|auto_generated|pll1|clk[4] -to network_inst|rgmii_send_inst|tx_pll_inst|altpll_component|auto_generated|pll1|clk[0] 19 

set_max_delay -from LTC2208_122MHz -to LTC2208_122MHz 16

set_max_delay -from PHY_RX_CLOCK -to PHY_RX_CLOCK 11

set_max_delay -from _122MHz -to _122MHz 13

set_max_delay -from PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2] -to _122MHz 10

#**************************************************************
# Set Minimum Delay
#**************************************************************

#set_min_delay -from network_inst|rgmii_send_inst|tx_pll_inst|altpll_component|auto_generated|pll1|clk[1] -to tx_output_clock -2

#set_min_delay -from PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2] -to PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1] -1

#**************************************************************
# Set False Paths
#**************************************************************

set_false_path -from {network:network_inst|mac_recv:mac_recv_inst|is_arp} -to {High_Priority_CC:High_Priority_CC_inst|Rx_frequency*}

set_false_path -from [get_clocks {network_inst|rgmii_send_inst|tx_pll_inst|altpll_component|auto_generated|pll1|clk[0]}] -to [get_clocks {tx_output_clock}]

# Set false path to generated clocks that feed output pins
set_false_path -to [get_ports {CMCLK CBCLK CLRCIN CLRCOUT ATTN_CLK* SSCK ADCCLK SPI_SCK PHY_MDC PHY_TX_CLOCK}]

# Set false paths to remove irrelevant setup and hold analysis 
set_false_path -fall_from  virt_PHY_RX_CLOCK -rise_to PHY_RX_CLOCK -setup
set_false_path -rise_from  virt_PHY_RX_CLOCK -fall_to PHY_RX_CLOCK -setup
set_false_path -fall_from  virt_PHY_RX_CLOCK -fall_to PHY_RX_CLOCK -hold
set_false_path -rise_from  virt_PHY_RX_CLOCK -rise_to PHY_RX_CLOCK -hold

#set_false_path -fall_from [get_clocks network_inst|rgmii_send_inst|tx_pll_inst|altpll_component|auto_generated|pll1|clk[0]] -rise_to [get_clocks tx_output_clock] -setup
#set_false_path -rise_from [get_clocks network_inst|rgmii_send_inst|tx_pll_inst|altpll_component|auto_generated|pll1|clk[0]] -fall_to [get_clocks tx_output_clock] -setup
#set_false_path -fall_from [get_clocks network_inst|rgmii_send_inst|tx_pll_inst|altpll_component|auto_generated|pll1|clk[0]] -fall_to [get_clocks tx_output_clock] -hold
#set_false_path -rise_from [get_clocks network_inst|rgmii_send_inst|tx_pll_inst|altpll_component|auto_generated|pll1|clk[0]] -rise_to [get_clocks tx_output_clock] -hold

# don't need fast paths to the LEDs and adhoc outputs so set false paths so Timing will be ignored
set_false_path -to { Status_LED DEBUG_LED* DITH* FPGA_PTT  NCONFIG  RAND*  USEROUT* FPGA_PLL DAC_ALC}

#don't need fast paths from the following inputs
set_false_path -from  {ANT_TUNE IO4 IO5 IO6 IO8 KEY_DASH KEY_DOT OVERFLOW* PTT MODE2}

#these registers are set long before they are used

set_false_path -from [get_registers {network:network_inst|eeprom:eeprom_inst|mac[*]}] -to [all_registers]
set_false_path -from [get_registers {network:network_inst|local_ip[*]}] -to [all_registers]
set_false_path -from [get_registers {network:network_inst|arp:arp_inst|destination_mac[*]}] -to [all_registers]



