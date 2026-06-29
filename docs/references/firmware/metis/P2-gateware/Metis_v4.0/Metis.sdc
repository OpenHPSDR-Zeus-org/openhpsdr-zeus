# Metis.sdc
#

#**************************************************************
# Time Information
#**************************************************************

set_time_format -unit ns -decimal_places 3

#*************************************************************************************
# Create Clock
#*************************************************************************************
# externally generated clocks (With respect to the FPGA)
#
create_clock -period  25.00MHz   -name CLK_25MHZ [get_ports CLK_25MHZ]
create_clock -name PHY_CLK125    -period 8.000   [get_ports {PHY_CLK125}]
create_clock -name PHY_RX_CLOCK  -period 8.000  -waveform {2 6} [get_ports {PHY_RX_CLOCK}]
create_clock -name {C22} -period 5208.33 [get_ports {C22}]
create_clock -name {C23} -period 5208.33 [get_ports {C23}]

#virtual base clocks on required inputs
create_clock -name virt_PHY_RX_CLOCK    -period 8.000

set_clock_groups -exclusive -group {virt_PHY_RX_CLOCK}

derive_pll_clocks

derive_clock_uncertainty

#assign more familiar names
set clock_12_5MHz network_inst|rgmii_send_inst|tx_pll_inst|altpll_component|auto_generated|pll1|clk[2]
set clock_2_5MHz  network_inst|rgmii_send_inst|tx_pll_inst|altpll_component|auto_generated|pll1|clk[3]
set I2C_clock     PLL_2_inst|altpll_component|auto_generated|pll1|clk[0]

#****************************************************************************************
# Create Generated Clocks
#****************************************************************************************										 
# internally generated clocks
#
create_generated_clock -name i2c_interface:interface_inst|i2c_master:master_inst|scl_clk~en -source [get_pins {PLL_2_inst|altpll_component|auto_generated|pll1|clk[0]}] -divide_by 4 -master_clock {PLL_2_inst|altpll_component|auto_generated|pll1|clk[0]} [get_registers {i2c_interface:interface_inst|i2c_master:master_inst|scl_clk~en}]
create_generated_clock -name i2c_interface:interface_inst|i2c_master:master_inst|data_clk -source [get_pins {PLL_2_inst|altpll_component|auto_generated|pll1|clk[0]}] -divide_by 4 -master_clock {PLL_2_inst|altpll_component|auto_generated|pll1|clk[0]} [get_registers {i2c_interface:interface_inst|i2c_master:master_inst|data_clk}] 
create_generated_clock -name sidetone2:sidetone_inst|sidetone_clock -source PHY_CLK125 -divide_by 690 sidetone2:sidetone_inst|sidetone_clock

create_generated_clock -source [get_pins {network_inst|rgmii_send_inst|tx_pll_inst|altpll_component|auto_generated|pll1|inclk[0]}] \
  -name tx_clock -duty_cycle 50.00 [get_pins {network_inst|rgmii_send_inst|tx_pll_inst|altpll_component|auto_generated|pll1|clk[0]}] -add

set_clock_groups -exclusive -group {tx_clock}

#create generated clock for PLL transmit clock output with 90 phase shift
create_generated_clock -source [get_pins {network_inst|rgmii_send_inst|tx_pll_inst|altpll_component|auto_generated|pll1|inclk[0]}] \
  -name PHY_TX_CLOCK -phase 90.00 -duty_cycle 50.00 [get_pins {network_inst|rgmii_send_inst|tx_pll_inst|altpll_component|auto_generated|pll1|clk[1]}] -add

set_clock_groups -exclusive -group {PHY_TX_CLOCK}



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
					network_inst|rgmii_send_inst|tx_pll_inst|altpll_component|auto_generated|pll1|clk[3] \
					tx_clock \
				       } \
				-group {OSC_10MHZ PLL2_inst|altpll_component|auto_generated|pll1|clk[0]} \
				-group {PHY_RX_CLOCK } \
				-group {PLL_2_inst|altpll_component|auto_generated|pll1|clk[0]} \
				-group  {C22} \
				-group  {C23}


#*****************************************************************************************
# Set Input Delay
#*****************************************************************************************
#
#12.5MHz clock for Config EEPROM  +/- 10nS setup and hold
set_input_delay 10  -clock  $clock_12_5MHz { ASMI_interface:ASMI_int_inst|ASMI:ASMI_inst|ASMI_altasmi_parallel_cv82:ASMI_altasmi_parallel_cv82_component|cycloneii_asmiblock2~ALTERA_DATA0}

# data from LTC2208 +/- 2nS setup and hold 
set_input_delay 2.000 -clock virt_122MHz  { INA*}

#PHY Data in 
#set_input_delay  0.8  -clock virt_PHY_RX_CLOCK {PHY_RX[*] RX_DV}  
#set_input_delay  0.8  -clock virt_PHY_RX_CLOCK {PHY_RX[*] RX_DV}  -clock_fall -add_delay

set_input_delay  -max 0.8  -clock virt_PHY_RX_CLOCK [get_ports {PHY_RX[*] RX_DV}]
set_input_delay  -min -0.8 -clock virt_PHY_RX_CLOCK -add_delay [get_ports {PHY_RX[*] RX_DV}]
set_input_delay  -max 0.8 -clock virt_PHY_RX_CLOCK -clock_fall -add_delay [get_ports {PHY_RX[*] RX_DV}]
set_input_delay  -min -0.8 -clock virt_PHY_RX_CLOCK -clock_fall -add_delay [get_ports {PHY_RX[*] RX_DV}]

#EEPROM Data in +/- 40nS setup and hold
set_input_delay  40  -clock $clock_2_5MHz {SO}

#PHY PHY_MDIO Data in +/- 10nS setup and hold
set_input_delay  10  -clock $clock_2_5MHz {PHY_MDIO PHY_INT_N}

#******************************************************************************************
# Set Output Delay
#******************************************************************************************
#
#12.5MHz clock for Config EEPROM  +/- 10nS
set_output_delay  10 -clock $clock_12_5MHz {ASMI_interface:ASMI_int_inst|ASMI:ASMI_inst|ASMI_altasmi_parallel_cv82:ASMI_altasmi_parallel_cv82_component|cycloneii_asmiblock2~ALTERA_SCE ASMI_interface:ASMI_int_inst|ASMI:ASMI_inst|ASMI_altasmi_parallel_cv82:ASMI_altasmi_parallel_cv82_component|cycloneii_asmiblock2~ALTERA_SDO ASMI_interface:ASMI_int_inst|ASMI:ASMI_inst|ASMI_altasmi_parallel_cv82:ASMI_altasmi_parallel_cv82_component|cycloneii_asmiblock2~ALTERA_DCLK}

#122.88MHz clock for Tx DAC 
set_output_delay  1.0 -clock _122MHz   { DACD[*]}

#PHY
set_output_delay  -max 1.0  -clock tx_clock [get_ports {PHY_TX[*] PHY_TX_EN}]
set_output_delay  -min -0.8 -clock tx_clock [get_ports {PHY_TX[*] PHY_TX_EN}]  -add_delay
set_output_delay  -max 1.0  -clock tx_clock [get_ports {PHY_TX[*] PHY_TX_EN}]  -clock_fall -add_delay
set_output_delay  -min -0.8 -clock tx_clock [get_ports {PHY_TX[*] PHY_TX_EN}]  -clock_fall -add_delay

#EEPROM (2.5MHz)
set_output_delay  40 -clock $clock_2_5MHz {SCK SI CS} -add_delay


#PHY (2.5MHz)
set_output_delay  10 -clock $clock_2_5MHz {PHY_MDIO} -add_delay



#******************************************************************************************
# Set Maximum Delay (for setup or recovery; low-level, over-riding timing adjustments)
#******************************************************************************************
set_max_delay -from network_inst|rgmii_send_inst|tx_pll_inst|altpll_component|auto_generated|pll1|clk[0] -to network_inst|rgmii_send_inst|tx_pll_inst|altpll_component|auto_generated|pll1|clk[0] 24
set_max_delay -from PHY_RX_CLOCK -to PHY_RX_CLOCK 11
set_max_delay -from tx_clock -to tx_clock 24
set_min_delay -from network_inst|rgmii_send_inst|tx_pll_inst|altpll_component|auto_generated|pll1|clk[0] -to C22 -1
set_min_delay -from network_inst|rgmii_send_inst|tx_pll_inst|altpll_component|auto_generated|pll1|clk[0] -to C23 -1


#******************************************************************************************
# Set Minimum Delay (for hold or removal; low-level, over-riding timing adjustments)
#******************************************************************************************


#**************************************************************
# Set False Paths
#**************************************************************

#set false paths for PHY Tx
set_false_path -setup -rise_from [get_clocks tx_clock] -fall_to [get_clocks PHY_TX_CLOCK]
set_false_path -setup -fall_from [get_clocks tx_clock] -rise_to [get_clocks PHY_TX_CLOCK]
set_false_path -setup -rise_from [get_clocks tx_clock] -rise_to [get_clocks PHY_TX_CLOCK]
set_false_path -setup -fall_from [get_clocks tx_clock] -fall_to [get_clocks PHY_TX_CLOCK]

#set false paths for PHY Rx
set_false_path -fall_from  virt_PHY_RX_CLOCK -rise_to PHY_RX_CLOCK -setup
set_false_path -rise_from  virt_PHY_RX_CLOCK -fall_to PHY_RX_CLOCK -setup
set_false_path -fall_from  virt_PHY_RX_CLOCK -fall_to PHY_RX_CLOCK -hold
set_false_path -rise_from  virt_PHY_RX_CLOCK -rise_to PHY_RX_CLOCK -hold

# don't need fast paths to the LEDs and adhoc outputs so set false paths so Timing will be ignored
set_false_path -to {Control_LED0 RAM_A* NCONFIG USEROUT*}

# Set false path to generated clocks that feed output pins
set_false_path -to [get_ports {PHY_MDC PHY_TX_CLOCK}]


