## Generated SDC file "Bootloader.sdc"

## Copyright (C) 1991-2013 Altera Corporation
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
## VERSION "Version 13.1.0 Build 162 10/23/2013 SJ Web Edition"

## DATE    "Fri Nov 21 19:09:21 2014"

##
## DEVICE  "EP3C25Q240C8"
##


#**************************************************************
# Time Information
#**************************************************************

set_time_format -unit ns -decimal_places 3



#**************************************************************
# Create Clock
#**************************************************************

create_clock -name {PHY_CLK125} -period 8.000 -waveform { 0.000 4.000 } [get_ports {PHY_CLK125}]
create_clock -name {PHY_RX_CLOCK} -period 80.000 -waveform { 0.000 40.000 } [get_ports {PHY_RX_CLOCK}]


#**************************************************************
# Create Generated Clock
#**************************************************************

create_generated_clock -name {PLL_inst|altpll_component|auto_generated|pll1|clk[1]} -source [get_pins {PLL_inst|altpll_component|auto_generated|pll1|inclk[0]}] -duty_cycle 50.000 -multiply_by 1 -divide_by 5 -master_clock {PHY_CLK125} [get_pins {PLL_inst|altpll_component|auto_generated|pll1|clk[1]}] 
create_generated_clock -name {PLL_inst|altpll_component|auto_generated|pll1|clk[2]} -source [get_pins {PLL_inst|altpll_component|auto_generated|pll1|inclk[0]}] -duty_cycle 50.000 -multiply_by 1 -divide_by 10 -master_clock {PHY_CLK125} [get_pins {PLL_inst|altpll_component|auto_generated|pll1|clk[2]}] 
create_generated_clock -name {PLL_inst|altpll_component|auto_generated|pll1|clk[3]} -source [get_pins {PLL_inst|altpll_component|auto_generated|pll1|inclk[0]}] -duty_cycle 50.000 -multiply_by 1 -divide_by 50 -master_clock {PHY_CLK125} [get_pins {PLL_inst|altpll_component|auto_generated|pll1|clk[3]}] 
create_generated_clock -name {PHY_RX_CLOCK_2} -source [get_ports {PHY_RX_CLOCK}] -divide_by 2 -master_clock {PHY_RX_CLOCK} [get_keepers {PHY_RX_CLOCK_2}] 


#**************************************************************
# Set Clock Latency
#**************************************************************



#**************************************************************
# Set Clock Uncertainty
#**************************************************************

set_clock_uncertainty -rise_from [get_clocks {PHY_CLK125}] -rise_to [get_clocks {PHY_CLK125}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PHY_CLK125}] -fall_to [get_clocks {PHY_CLK125}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PHY_CLK125}] -rise_to [get_clocks {PHY_CLK125}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PHY_CLK125}] -fall_to [get_clocks {PHY_CLK125}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK_2}] -rise_to [get_clocks {PHY_RX_CLOCK_2}]  0.030  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK_2}] -fall_to [get_clocks {PHY_RX_CLOCK_2}]  0.030  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK_2}] -rise_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[2]}] -setup 0.080  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK_2}] -rise_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[2]}] -hold 0.110  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK_2}] -fall_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[2]}] -setup 0.080  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK_2}] -fall_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[2]}] -hold 0.110  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK_2}] -rise_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[1]}] -setup 0.080  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK_2}] -rise_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[1]}] -hold 0.110  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK_2}] -fall_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[1]}] -setup 0.080  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK_2}] -fall_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[1]}] -hold 0.110  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK_2}] -rise_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[3]}] -setup 0.080  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK_2}] -rise_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[3]}] -hold 0.110  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK_2}] -fall_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[3]}] -setup 0.080  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK_2}] -fall_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[3]}] -hold 0.110  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK_2}] -rise_to [get_clocks {PHY_RX_CLOCK}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK_2}] -fall_to [get_clocks {PHY_RX_CLOCK}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK_2}] -rise_to [get_clocks {PHY_RX_CLOCK_2}]  0.030  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK_2}] -fall_to [get_clocks {PHY_RX_CLOCK_2}]  0.030  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK_2}] -rise_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[2]}] -setup 0.080  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK_2}] -rise_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[2]}] -hold 0.110  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK_2}] -fall_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[2]}] -setup 0.080  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK_2}] -fall_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[2]}] -hold 0.110  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK_2}] -rise_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[1]}] -setup 0.080  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK_2}] -rise_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[1]}] -hold 0.110  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK_2}] -fall_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[1]}] -setup 0.080  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK_2}] -fall_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[1]}] -hold 0.110  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK_2}] -rise_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[3]}] -setup 0.080  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK_2}] -rise_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[3]}] -hold 0.110  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK_2}] -fall_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[3]}] -setup 0.080  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK_2}] -fall_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[3]}] -hold 0.110  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK_2}] -rise_to [get_clocks {PHY_RX_CLOCK}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK_2}] -fall_to [get_clocks {PHY_RX_CLOCK}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.080  
set_clock_uncertainty -rise_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.080  
set_clock_uncertainty -rise_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[3]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[3]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PHY_RX_CLOCK}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PHY_RX_CLOCK}] -hold 0.080  
set_clock_uncertainty -rise_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PHY_RX_CLOCK}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PHY_RX_CLOCK}] -hold 0.080  
set_clock_uncertainty -fall_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.080  
set_clock_uncertainty -fall_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.080  
set_clock_uncertainty -fall_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[3]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[3]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PHY_RX_CLOCK}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PHY_RX_CLOCK}] -hold 0.080  
set_clock_uncertainty -fall_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PHY_RX_CLOCK}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PHY_RX_CLOCK}] -hold 0.080  
set_clock_uncertainty -rise_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.080  
set_clock_uncertainty -rise_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.080  
set_clock_uncertainty -rise_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.080  
set_clock_uncertainty -fall_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.080  
set_clock_uncertainty -fall_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[3]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[3]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.080  
set_clock_uncertainty -rise_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[3]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[3]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.080  
set_clock_uncertainty -rise_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[3]}] -rise_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[3]}] -fall_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[3]}] -rise_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[3]}] -fall_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[3]}] -rise_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[3]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[3]}] -fall_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[3]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[3]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[3]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.080  
set_clock_uncertainty -fall_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[3]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[3]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.080  
set_clock_uncertainty -fall_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[3]}] -rise_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[3]}] -fall_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[3]}] -rise_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[3]}] -fall_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[3]}] -rise_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[3]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[3]}] -fall_to [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[3]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK}] -rise_to [get_clocks {PHY_RX_CLOCK_2}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK}] -fall_to [get_clocks {PHY_RX_CLOCK_2}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK}] -rise_to [get_clocks {PHY_RX_CLOCK}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK}] -fall_to [get_clocks {PHY_RX_CLOCK}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK}] -rise_to [get_clocks {PHY_RX_CLOCK_2}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK}] -fall_to [get_clocks {PHY_RX_CLOCK_2}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK}] -rise_to [get_clocks {PHY_RX_CLOCK}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK}] -fall_to [get_clocks {PHY_RX_CLOCK}]  0.020  


#**************************************************************
# Set Input Delay
#**************************************************************



#**************************************************************
# Set Output Delay
#**************************************************************



#**************************************************************
# Set Clock Groups
#**************************************************************

set_clock_groups -exclusive -group [get_clocks {PHY_RX_CLOCK}] -group [get_clocks {PHY_CLK125 PLL_inst|altpll_component|auto_generated|pll1|clk[1]  PLL_inst|altpll_component|auto_generated|pll1|clk[2]  PLL_inst|altpll_component|auto_generated|pll1|clk[3]}] 


#**************************************************************
# Set False Path
#**************************************************************

set_false_path -from [get_keepers {*rdptr_g*}] -to [get_keepers {*ws_dgrp|dffpipe_re9:dffpipe16|dffe17a*}]
set_false_path -from [get_keepers {*delayed_wrptr_g*}] -to [get_keepers {*rs_dgwp|dffpipe_qe9:dffpipe13|dffe14a*}]
set_false_path -from [get_keepers {*rdptr_g*}] -to [get_keepers {*ws_dgrp|dffpipe_ed9:dffpipe15|dffe16a*}]
set_false_path -from [get_keepers {*delayed_wrptr_g*}] -to [get_keepers {*rs_dgwp|dffpipe_dd9:dffpipe12|dffe13a*}]


#**************************************************************
# Set Multicycle Path
#**************************************************************



#**************************************************************
# Set Maximum Delay
#**************************************************************



#**************************************************************
# Set Minimum Delay
#**************************************************************

set_min_delay -from  [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[3]}]  -to  [get_clocks {PHY_RX_CLOCK_2}] -1.000


#**************************************************************
# Set Input Transition
#**************************************************************

