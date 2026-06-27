## Generated SDC file "Metis.sdc"

## Copyright (C) 1991-2012 Altera Corporation
## Your use of Altera Corporation's design tools, logic functions 
## and other software and tools, and its AMPP partner logic 
## functions, and any output files from any of the foregoing 
## (including device programming or simulation files), and any 
## associated documentation or information are expressly subject 
## to the terms and conditions of the Altera Program License 
## Subscription Agreement, Altera MegaCore Function License 
## Agreement, or other applicable license agreement, including, 
## without limitation, that your use is for the sole purpose of 
## programming logic devices manufactured by Altera and sold by 
## Altera or its authorized distributors.  Please refer to the 
## applicable agreement for further details.


## VENDOR  "Altera"
## PROGRAM "Quartus II"
## VERSION "Version 12.1 Build 177 11/07/2012 SJ Web Edition"

## DATE    "Fri Mar 08 17:51:01 2013"

##
## DEVICE  "EP3C40Q240C8"
##


#**************************************************************
# Time Information
#**************************************************************

set_time_format -unit ns -decimal_places 3



#**************************************************************
# Create Clock
#**************************************************************

create_clock -name {PHY_CLK125} -period 8.000 -waveform { 0.000 4.000 } [get_ports {PHY_CLK125}]
create_clock -name {PHY_RX_CLOCK} -period 40.000 -waveform { 0.000 20.000 } [get_ports {PHY_RX_CLOCK}]

derive_pll_clocks

derive_clock_uncertainty

#**************************************************************
# Create Generated Clock
#**************************************************************

#create_generated_clock -name {PLL_inst|altpll_component|auto_generated|pll1|clk[0]} -source [get_pins {PLL_inst|altpll_component|auto_generated|pll1|inclk[0]}] -duty_cycle 50.000 -multiply_by 48 -divide_by 125 -master_clock {PHY_CLK125} [get_pins {PLL_inst|altpll_component|auto_generated|pll1|clk[0]}] 
#create_generated_clock -name {PLL_inst|altpll_component|auto_generated|pll1|clk[1]} -source [get_pins {PLL_inst|altpll_component|auto_generated|pll1|inclk[0]}] -duty_cycle 50.000 -multiply_by 1 -divide_by 5 -master_clock {PHY_CLK125} [get_pins {PLL_inst|altpll_component|auto_generated|pll1|clk[1]}] 
#create_generated_clock -name {PLL_inst|altpll_component|auto_generated|pll1|clk[2]} -source [get_pins {PLL_inst|altpll_component|auto_generated|pll1|inclk[0]}] -duty_cycle 50.000 -multiply_by 1 -divide_by 10 -master_clock {PHY_CLK125} [get_pins {PLL_inst|altpll_component|auto_generated|pll1|clk[2]}] 
#create_generated_clock -name {PLL_inst|altpll_component|auto_generated|pll1|clk[3]} -source [get_pins {PLL_inst|altpll_component|auto_generated|pll1|inclk[0]}] -duty_cycle 50.000 -multiply_by 4 -divide_by 625 -master_clock {PHY_CLK125} [get_pins {PLL_inst|altpll_component|auto_generated|pll1|clk[3]}] 
create_generated_clock -name {PHY_MDIO_clk} -source [get_pins {PLL_inst|altpll_component|auto_generated|pll1|clk[1]}] -divide_by 10 -master_clock {PLL_inst|altpll_component|auto_generated|pll1|clk[1]} [get_registers {PHY_MDIO_clk}] 
create_generated_clock -name {PHY_RX_CLOCK_2} -source [get_ports {PHY_RX_CLOCK}] -divide_by 2 -master_clock {PHY_RX_CLOCK} [get_registers {PHY_RX_CLOCK_2}] 
create_generated_clock -name {i2c_interface:interface_inst|i2c_master:master_inst|data_clk} -source [get_pins {PLL_inst|altpll_component|auto_generated|pll1|clk[3]}] -divide_by 4 -master_clock {PLL_inst|altpll_component|auto_generated|pll1|clk[3]} [get_registers {i2c_interface:interface_inst|i2c_master:master_inst|data_clk}] 
create_generated_clock -name {i2c_interface:interface_inst|i2c_master:master_inst|scl_clk~en} -source [get_pins {PLL_inst|altpll_component|auto_generated|pll1|clk[3]}] -divide_by 4 -master_clock {PLL_inst|altpll_component|auto_generated|pll1|clk[3]} [get_registers {i2c_interface:interface_inst|i2c_master:master_inst|scl_clk~en}]
#create_generated_clock -name {i2c_interface:interface_inst|i2c_master:master_inst|scl~ena} -source [get_pins {PLL_inst|altpll_component|auto_generated|pll1|clk[3]}] -divide_by 4 -master_clock {PLL_inst|altpll_component|auto_generated|pll1|clk[3]} [get_registers {i2c_interface:interface_inst|i2c_master:master_inst|scl~ena}] 
#create_generated_clock -name {i2c_interface:interface_inst|i2c_master:master_inst|scl_clk} -source [get_pins {PLL_inst|altpll_component|auto_generated|pll1|clk[3]}] -divide_by 4 -master_clock {PLL_inst|altpll_component|auto_generated|pll1|clk[3]} [get_registers {i2c_interface:interface_inst|i2c_master:master_inst|scl_clk}] 


#**************************************************************
# Set Clock Latency
#**************************************************************



#**************************************************************
# Set Clock Uncertainty
#**************************************************************



#**************************************************************
# Set Input Delay
#**************************************************************



#**************************************************************
# Set Output Delay
#**************************************************************



#**************************************************************
# Set Clock Groups
#**************************************************************
set_clock_groups -asynchronous -group PHY_CLK125 -group PHY_RX_CLOCK -group PLL_inst|altpll_component|auto_generated|pll1|clk[0] -group PLL_inst|altpll_component|auto_generated|pll1|clk[1] -group PLL_inst|altpll_component|auto_generated|pll1|clk[2] -group PLL_inst|altpll_component|auto_generated|pll1|clk[3] -group PHY_MDIO_clk -group PHY_RX_CLOCK_2 -group i2c_interface:interface_inst|i2c_master:master_inst|data_clk -group i2c_interface:interface_inst|i2c_master:master_inst|scl_clk~ena


#**************************************************************
# Set False Path
#**************************************************************

set_false_path -from [get_keepers {*rdptr_g*}] -to [get_keepers {*ws_dgrp|dffpipe_2f9:dffpipe12|dffe13a*}]
set_false_path -from [get_keepers {*delayed_wrptr_g*}] -to [get_keepers {*rs_dgwp|dffpipe_1f9:dffpipe9|dffe10a*}]
set_false_path -from [get_keepers {*rdptr_g*}] -to [get_keepers {*ws_dgrp|dffpipe_se9:dffpipe17|dffe18a*}]
set_false_path -from [get_keepers {*delayed_wrptr_g*}] -to [get_keepers {*rs_dgwp|dffpipe_re9:dffpipe14|dffe15a*}]
set_false_path -from [get_keepers {*rdptr_g*}] -to [get_keepers {*ws_dgrp|dffpipe_fd9:dffpipe15|dffe16a*}]
set_false_path -from [get_keepers {*delayed_wrptr_g*}] -to [get_keepers {*rs_dgwp|dffpipe_ed9:dffpipe12|dffe13a*}]
set_false_path -from [get_keepers {*rdptr_g*}] -to [get_keepers {*ws_dgrp|dffpipe_0f9:dffpipe19|dffe20a*}]
set_false_path -from [get_keepers {*delayed_wrptr_g*}] -to [get_keepers {*rs_dgwp|dffpipe_ue9:dffpipe15|dffe16a*}]
set_false_path -from [get_keepers {*rdptr_g*}] -to [get_keepers {*ws_dgrp|dffpipe_5f9:dffpipe16|dffe17a*}]
set_false_path -from [get_keepers {*delayed_wrptr_g*}] -to [get_keepers {*rs_dgwp|dffpipe_4f9:dffpipe13|dffe14a*}]


#**************************************************************
# Set Multicycle Path
#**************************************************************



#**************************************************************
# Set Maximum Delay
#**************************************************************



#**************************************************************
# Set Minimum Delay
#**************************************************************



#**************************************************************
# Set Input Transition
#**************************************************************

