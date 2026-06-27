## Generated SDC file "Hermes.out.sdc"

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
## VERSION "Version 13.0.0 Build 156 04/24/2013 SJ Web Edition"

## DATE    "Sun Mar 09 18:10:32 2014"

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

create_clock -name {LTC2208_122MHz} -period 8.138 -waveform { 0.000 4.069 } [get_ports {LTC2208_122MHz}]
create_clock -name {_122MHz} -period 8.138 -waveform { 0.000 4.069 } [get_ports {_122MHz}]
create_clock -name {OSC_10MHZ} -period 100.000 -waveform { 0.000 50.000 } [get_ports {OSC_10MHZ}]
create_clock -name {PHY_CLK125} -period 8.000 -waveform { 0.000 4.000 } [get_ports {PHY_CLK125}]
create_clock -name {CLK_25MHZ} -period 40.000 -waveform { 0.000 20.000 } [get_ports {CLK_25MHZ}]
create_clock -name {PHY_RX_CLOCK} -period 40.000 -waveform { 0.000 20.000 } [get_ports {PHY_RX_CLOCK}]
create_clock -name {Rx_MAC_state} -period 80.000 -waveform { 0.000 40.000 } [get_registers {Rx_MAC:Rx_MAC_inst|PHY_100T_state}]
create_clock -name {Attenuator_clk} -period 41.666 -waveform { 0.000 20.833 } [get_registers {Attenuator:Attenuator_inst|clk_2}]


#**************************************************************
# Create Generated Clock
#**************************************************************

create_generated_clock -name {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]} -source [get_pins {PLL_clocks_inst|altpll_component|auto_generated|pll1|inclk[0]}] -duty_cycle 50.000 -multiply_by 1 -divide_by 50 -master_clock {PHY_CLK125} [get_pins {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] 
create_generated_clock -name {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]} -source [get_pins {PLL_clocks_inst|altpll_component|auto_generated|pll1|inclk[0]}] -duty_cycle 50.000 -multiply_by 1 -divide_by 5 -master_clock {PHY_CLK125} [get_pins {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] 
create_generated_clock -name {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]} -source [get_pins {PLL_clocks_inst|altpll_component|auto_generated|pll1|inclk[0]}] -duty_cycle 50.000 -multiply_by 1 -divide_by 10 -master_clock {PHY_CLK125} [get_pins {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] 
create_generated_clock -name {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]} -source [get_pins {PLL_IF_inst|altpll_component|auto_generated|pll1|inclk[0]}] -duty_cycle 50.000 -multiply_by 43 -divide_by 110 -master_clock {LTC2208_122MHz} [get_pins {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] 
create_generated_clock -name {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]} -source [get_pins {PLL_IF_inst|altpll_component|auto_generated|pll1|inclk[0]}] -duty_cycle 50.000 -multiply_by 1 -divide_by 10 -master_clock {LTC2208_122MHz} [get_pins {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}] 
create_generated_clock -name {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]} -source [get_pins {PLL_IF_inst|altpll_component|auto_generated|pll1|inclk[0]}] -duty_cycle 50.000 -multiply_by 43 -divide_by 176130 -master_clock {LTC2208_122MHz} [get_pins {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] 
create_generated_clock -name {PLL2_inst|altpll_component|auto_generated|pll1|clk[0]} -source [get_pins {PLL2_inst|altpll_component|auto_generated|pll1|inclk[0]}] -duty_cycle 50.000 -multiply_by 1 -divide_by 125 -master_clock {OSC_10MHZ} [get_pins {PLL2_inst|altpll_component|auto_generated|pll1|clk[0]}] 
create_generated_clock -name {PLL_inst|altpll_component|auto_generated|pll1|clk[0]} -source [get_pins {PLL_inst|altpll_component|auto_generated|pll1|inclk[0]}] -duty_cycle 50.000 -multiply_by 1 -divide_by 1536 -master_clock {_122MHz} [get_pins {PLL_inst|altpll_component|auto_generated|pll1|clk[0]}] 
create_generated_clock -name {Hermes_clk_lrclk_gen:clrgen|BCLK} -source [get_ports {LTC2208_122MHz}] -divide_by 40 -master_clock {LTC2208_122MHz} [get_registers {Hermes_clk_lrclk_gen:clrgen|BCLK}] 
create_generated_clock -name {spc[2]} -source [get_registers {Hermes_clk_lrclk_gen:clrgen|BCLK}] -divide_by 8 -master_clock {Hermes_clk_lrclk_gen:clrgen|BCLK} [get_registers {spc[2]}] 
create_generated_clock -name {PHY_RX_CLOCK_2} -source [get_ports {PHY_RX_CLOCK}] -divide_by 2 -master_clock {PHY_RX_CLOCK} [get_registers {PHY_RX_CLOCK_2}] 
create_generated_clock -name {PHY_TX_CLOCK} -source [get_pins {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -master_clock {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]} -invert [get_ports {PHY_TX_CLOCK}] 


#**************************************************************
# Set Clock Latency
#**************************************************************



#**************************************************************
# Set Clock Uncertainty
#**************************************************************

set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK_2}] -rise_to [get_clocks {PHY_RX_CLOCK_2}]  0.030  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK_2}] -fall_to [get_clocks {PHY_RX_CLOCK_2}]  0.030  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK_2}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -setup 0.080  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK_2}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -hold 0.110  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK_2}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -setup 0.080  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK_2}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -hold 0.110  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK_2}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.080  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK_2}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.110  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK_2}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.080  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK_2}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.110  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK_2}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -setup 0.080  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK_2}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -hold 0.110  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK_2}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -setup 0.080  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK_2}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -hold 0.110  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK_2}] -rise_to [get_clocks {PHY_RX_CLOCK}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK_2}] -fall_to [get_clocks {PHY_RX_CLOCK}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK_2}] -rise_to [get_clocks {PHY_RX_CLOCK_2}]  0.030  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK_2}] -fall_to [get_clocks {PHY_RX_CLOCK_2}]  0.030  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK_2}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -setup 0.080  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK_2}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -hold 0.110  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK_2}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -setup 0.080  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK_2}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -hold 0.110  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK_2}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.080  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK_2}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.110  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK_2}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.080  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK_2}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.110  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK_2}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -setup 0.080  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK_2}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -hold 0.110  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK_2}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -setup 0.080  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK_2}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -hold 0.110  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK_2}] -rise_to [get_clocks {PHY_RX_CLOCK}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK_2}] -fall_to [get_clocks {PHY_RX_CLOCK}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {spc[2]}] -rise_to [get_clocks {spc[2]}]  0.030  
set_clock_uncertainty -rise_from [get_clocks {spc[2]}] -fall_to [get_clocks {spc[2]}]  0.030  
set_clock_uncertainty -rise_from [get_clocks {spc[2]}] -rise_to [get_clocks {Hermes_clk_lrclk_gen:clrgen|BCLK}]  0.030  
set_clock_uncertainty -rise_from [get_clocks {spc[2]}] -fall_to [get_clocks {Hermes_clk_lrclk_gen:clrgen|BCLK}]  0.030  
set_clock_uncertainty -fall_from [get_clocks {spc[2]}] -rise_to [get_clocks {spc[2]}]  0.030  
set_clock_uncertainty -fall_from [get_clocks {spc[2]}] -fall_to [get_clocks {spc[2]}]  0.030  
set_clock_uncertainty -fall_from [get_clocks {spc[2]}] -rise_to [get_clocks {Hermes_clk_lrclk_gen:clrgen|BCLK}]  0.030  
set_clock_uncertainty -fall_from [get_clocks {spc[2]}] -fall_to [get_clocks {Hermes_clk_lrclk_gen:clrgen|BCLK}]  0.030  
set_clock_uncertainty -rise_from [get_clocks {Hermes_clk_lrclk_gen:clrgen|BCLK}] -rise_to [get_clocks {spc[2]}]  0.030  
set_clock_uncertainty -rise_from [get_clocks {Hermes_clk_lrclk_gen:clrgen|BCLK}] -fall_to [get_clocks {spc[2]}]  0.030  
set_clock_uncertainty -rise_from [get_clocks {Hermes_clk_lrclk_gen:clrgen|BCLK}] -rise_to [get_clocks {Hermes_clk_lrclk_gen:clrgen|BCLK}]  0.030  
set_clock_uncertainty -rise_from [get_clocks {Hermes_clk_lrclk_gen:clrgen|BCLK}] -fall_to [get_clocks {Hermes_clk_lrclk_gen:clrgen|BCLK}]  0.030  
set_clock_uncertainty -rise_from [get_clocks {Hermes_clk_lrclk_gen:clrgen|BCLK}] -rise_to [get_clocks {LTC2208_122MHz}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {Hermes_clk_lrclk_gen:clrgen|BCLK}] -fall_to [get_clocks {LTC2208_122MHz}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {Hermes_clk_lrclk_gen:clrgen|BCLK}] -rise_to [get_clocks {spc[2]}]  0.030  
set_clock_uncertainty -fall_from [get_clocks {Hermes_clk_lrclk_gen:clrgen|BCLK}] -fall_to [get_clocks {spc[2]}]  0.030  
set_clock_uncertainty -fall_from [get_clocks {Hermes_clk_lrclk_gen:clrgen|BCLK}] -rise_to [get_clocks {Hermes_clk_lrclk_gen:clrgen|BCLK}]  0.030  
set_clock_uncertainty -fall_from [get_clocks {Hermes_clk_lrclk_gen:clrgen|BCLK}] -fall_to [get_clocks {Hermes_clk_lrclk_gen:clrgen|BCLK}]  0.030  
set_clock_uncertainty -fall_from [get_clocks {Hermes_clk_lrclk_gen:clrgen|BCLK}] -rise_to [get_clocks {LTC2208_122MHz}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {Hermes_clk_lrclk_gen:clrgen|BCLK}] -fall_to [get_clocks {LTC2208_122MHz}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.090  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.090  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.090  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.090  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {LTC2208_122MHz}] -rise_to [get_clocks {Hermes_clk_lrclk_gen:clrgen|BCLK}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {LTC2208_122MHz}] -fall_to [get_clocks {Hermes_clk_lrclk_gen:clrgen|BCLK}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {LTC2208_122MHz}] -rise_to [get_clocks {LTC2208_122MHz}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {LTC2208_122MHz}] -fall_to [get_clocks {LTC2208_122MHz}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {LTC2208_122MHz}] -rise_to [get_clocks {Hermes_clk_lrclk_gen:clrgen|BCLK}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {LTC2208_122MHz}] -fall_to [get_clocks {Hermes_clk_lrclk_gen:clrgen|BCLK}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {LTC2208_122MHz}] -rise_to [get_clocks {LTC2208_122MHz}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {LTC2208_122MHz}] -fall_to [get_clocks {LTC2208_122MHz}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PHY_TX_CLOCK}]  0.110  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PHY_TX_CLOCK}]  0.110  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.080  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.080  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PHY_TX_CLOCK}]  0.110  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PHY_TX_CLOCK}]  0.110  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.080  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.080  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.080  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.080  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.080  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.080  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {Attenuator_clk}] -rise_to [get_clocks {Attenuator_clk}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {Attenuator_clk}] -fall_to [get_clocks {Attenuator_clk}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {Attenuator_clk}] -rise_to [get_clocks {Attenuator_clk}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {Attenuator_clk}] -fall_to [get_clocks {Attenuator_clk}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.080  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.080  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PHY_RX_CLOCK}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PHY_RX_CLOCK}] -hold 0.080  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PHY_RX_CLOCK}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PHY_RX_CLOCK}] -hold 0.080  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.080  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.080  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PHY_RX_CLOCK}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PHY_RX_CLOCK}] -hold 0.080  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PHY_RX_CLOCK}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PHY_RX_CLOCK}] -hold 0.080  
set_clock_uncertainty -rise_from [get_clocks {PHY_CLK125}] -rise_to [get_clocks {PHY_CLK125}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PHY_CLK125}] -fall_to [get_clocks {PHY_CLK125}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PHY_CLK125}] -rise_to [get_clocks {PHY_CLK125}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PHY_CLK125}] -fall_to [get_clocks {PHY_CLK125}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK}] -rise_to [get_clocks {PHY_RX_CLOCK_2}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK}] -fall_to [get_clocks {PHY_RX_CLOCK_2}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -setup 0.080  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -hold 0.110  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -setup 0.080  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -hold 0.110  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK}] -rise_to [get_clocks {PHY_RX_CLOCK}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK}] -fall_to [get_clocks {PHY_RX_CLOCK}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK}] -rise_to [get_clocks {PHY_RX_CLOCK_2}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK}] -fall_to [get_clocks {PHY_RX_CLOCK_2}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -setup 0.080  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -hold 0.110  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -setup 0.080  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -hold 0.110  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK}] -rise_to [get_clocks {PHY_RX_CLOCK}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK}] -fall_to [get_clocks {PHY_RX_CLOCK}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {Rx_MAC_state}] -rise_to [get_clocks {PHY_RX_CLOCK}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {Rx_MAC_state}] -fall_to [get_clocks {PHY_RX_CLOCK}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {Rx_MAC_state}] -rise_to [get_clocks {PHY_RX_CLOCK}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {Rx_MAC_state}] -fall_to [get_clocks {PHY_RX_CLOCK}]  0.020  


#**************************************************************
# Set Input Delay
#**************************************************************

set_input_delay -add_delay  -clock [get_clocks {PHY_CLK125}]  2.000 [get_ports {INA[0]}]
set_input_delay -add_delay  -clock [get_clocks {PHY_CLK125}]  2.000 [get_ports {INA[1]}]
set_input_delay -add_delay  -clock [get_clocks {PHY_CLK125}]  2.000 [get_ports {INA[2]}]
set_input_delay -add_delay  -clock [get_clocks {PHY_CLK125}]  2.000 [get_ports {INA[3]}]
set_input_delay -add_delay  -clock [get_clocks {PHY_CLK125}]  2.000 [get_ports {INA[4]}]
set_input_delay -add_delay  -clock [get_clocks {PHY_CLK125}]  2.000 [get_ports {INA[5]}]
set_input_delay -add_delay  -clock [get_clocks {PHY_CLK125}]  2.000 [get_ports {INA[6]}]
set_input_delay -add_delay  -clock [get_clocks {PHY_CLK125}]  2.000 [get_ports {INA[7]}]
set_input_delay -add_delay  -clock [get_clocks {PHY_CLK125}]  2.000 [get_ports {INA[8]}]
set_input_delay -add_delay  -clock [get_clocks {PHY_CLK125}]  2.000 [get_ports {INA[9]}]
set_input_delay -add_delay  -clock [get_clocks {PHY_CLK125}]  2.000 [get_ports {INA[10]}]
set_input_delay -add_delay  -clock [get_clocks {PHY_CLK125}]  2.000 [get_ports {INA[11]}]
set_input_delay -add_delay  -clock [get_clocks {PHY_CLK125}]  2.000 [get_ports {INA[12]}]
set_input_delay -add_delay  -clock [get_clocks {PHY_CLK125}]  2.000 [get_ports {INA[13]}]
set_input_delay -add_delay  -clock [get_clocks {PHY_CLK125}]  2.000 [get_ports {INA[14]}]
set_input_delay -add_delay  -clock [get_clocks {PHY_CLK125}]  2.000 [get_ports {INA[15]}]
set_input_delay -add_delay  -clock [get_clocks {PHY_CLK125}]  2.000 [get_ports {PHY_RX[0]}]
set_input_delay -add_delay  -clock [get_clocks {PHY_CLK125}]  2.000 [get_ports {PHY_RX[1]}]
set_input_delay -add_delay  -clock [get_clocks {PHY_CLK125}]  2.000 [get_ports {PHY_RX[2]}]
set_input_delay -add_delay  -clock [get_clocks {PHY_CLK125}]  2.000 [get_ports {PHY_RX[3]}]
set_input_delay -add_delay  -clock [get_clocks {PHY_CLK125}]  2.000 [get_ports {RX_DV}]


#**************************************************************
# Set Output Delay
#**************************************************************

set_output_delay -add_delay  -clock [get_clocks {_122MHz}]  -9.000 [get_ports {DACD[0]}]
set_output_delay -add_delay  -clock [get_clocks {_122MHz}]  -9.000 [get_ports {DACD[1]}]
set_output_delay -add_delay  -clock [get_clocks {_122MHz}]  -9.000 [get_ports {DACD[2]}]
set_output_delay -add_delay  -clock [get_clocks {_122MHz}]  -9.000 [get_ports {DACD[3]}]
set_output_delay -add_delay  -clock [get_clocks {_122MHz}]  -9.000 [get_ports {DACD[4]}]
set_output_delay -add_delay  -clock [get_clocks {_122MHz}]  -9.000 [get_ports {DACD[5]}]
set_output_delay -add_delay  -clock [get_clocks {_122MHz}]  -9.000 [get_ports {DACD[6]}]
set_output_delay -add_delay  -clock [get_clocks {_122MHz}]  -9.000 [get_ports {DACD[7]}]
set_output_delay -add_delay  -clock [get_clocks {_122MHz}]  -9.000 [get_ports {DACD[8]}]
set_output_delay -add_delay  -clock [get_clocks {_122MHz}]  -9.000 [get_ports {DACD[9]}]
set_output_delay -add_delay  -clock [get_clocks {_122MHz}]  -9.000 [get_ports {DACD[10]}]
set_output_delay -add_delay  -clock [get_clocks {_122MHz}]  -9.000 [get_ports {DACD[11]}]
set_output_delay -add_delay  -clock [get_clocks {_122MHz}]  -9.000 [get_ports {DACD[12]}]
set_output_delay -add_delay  -clock [get_clocks {_122MHz}]  -9.000 [get_ports {DACD[13]}]
set_output_delay -add_delay  -clock [get_clocks {PHY_TX_CLOCK}]  2.000 [get_ports {PHY_TX[0]}]
set_output_delay -add_delay  -clock [get_clocks {PHY_TX_CLOCK}]  2.000 [get_ports {PHY_TX[1]}]
set_output_delay -add_delay  -clock [get_clocks {PHY_TX_CLOCK}]  2.000 [get_ports {PHY_TX[2]}]
set_output_delay -add_delay  -clock [get_clocks {PHY_TX_CLOCK}]  2.000 [get_ports {PHY_TX[3]}]
set_output_delay -add_delay  -clock [get_clocks {PHY_TX_CLOCK}]  2.000 [get_ports {PHY_TX_CLOCK}]
set_output_delay -add_delay  -clock [get_clocks {PHY_TX_CLOCK}]  2.000 [get_ports {PHY_TX_EN}]


#**************************************************************
# Set Clock Groups
#**************************************************************

set_clock_groups -asynchronous -group [get_clocks {OSC_10MHZ PLL2_inst|altpll_component|auto_generated|pll1|clk[0]}] -group [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]  PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2] }] -group [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -group [get_clocks {PHY_CLK125 PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]   PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]   PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2] }] -group [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[0]}] -group [get_clocks {CLK_25MHZ}] -group [get_clocks {Rx_MAC_state}] -group [get_clocks {LTC2208_122MHz}] -group [get_clocks {_122MHz}] -group [get_clocks {Attenuator_clk}] 


#**************************************************************
# Set False Path
#**************************************************************

set_false_path  -from  [get_clocks {Hermes_clk_lrclk_gen:clrgen|BCLK}]  -to  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[*]}]
set_false_path  -from  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}]  -to  [get_clocks {spc[2]}]
set_false_path  -from  [get_clocks {LTC2208_122MHz}]  -to  [get_clocks {spc[2]}]
set_false_path  -from  [get_clocks {PHY_RX_CLOCK_2}]  -to  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]  LTC2208_122MHz}]
set_false_path  -from  [get_clocks {PHY_CLK125}]  -to  [get_clocks {PHY_RX_CLOCK}]
set_false_path -from [get_keepers {*rdptr_g*}] -to [get_keepers {*ws_dgrp|dffpipe_re9:dffpipe16|dffe17a*}]
set_false_path -from [get_keepers {*delayed_wrptr_g*}] -to [get_keepers {*rs_dgwp|dffpipe_qe9:dffpipe13|dffe14a*}]
set_false_path -from [get_keepers {*rdptr_g*}] -to [get_keepers {*ws_dgrp|dffpipe_4f9:dffpipe13|dffe14a*}]
set_false_path -from [get_keepers {*delayed_wrptr_g*}] -to [get_keepers {*rs_dgwp|dffpipe_3f9:dffpipe10|dffe11a*}]
set_false_path -from [get_keepers {*rdptr_g*}] -to [get_keepers {*ws_dgrp|dffpipe_1f9:dffpipe17|dffe18a*}]
set_false_path -from [get_keepers {*delayed_wrptr_g*}] -to [get_keepers {*rs_dgwp|dffpipe_0f9:dffpipe14|dffe15a*}]
set_false_path -from [get_keepers {*rdptr_g*}] -to [get_keepers {*ws_dgrp|dffpipe_fd9:dffpipe15|dffe16a*}]
set_false_path -from [get_keepers {*delayed_wrptr_g*}] -to [get_keepers {*rs_dgwp|dffpipe_ed9:dffpipe12|dffe13a*}]
set_false_path -from [get_keepers {*rdptr_g*}] -to [get_keepers {*ws_dgrp|dffpipe_ve9:dffpipe19|dffe20a*}]
set_false_path -from [get_keepers {*delayed_wrptr_g*}] -to [get_keepers {*rs_dgwp|dffpipe_te9:dffpipe15|dffe16a*}]
set_false_path -from [get_keepers {spc[1]}] -to [get_clocks {Hermes_clk_lrclk_gen:clrgen|BCLK}]


#**************************************************************
# Set Multicycle Path
#**************************************************************

set_multicycle_path -setup -end -from  [get_clocks {LTC2208_122MHz}]  -to  [get_clocks *] 2
set_multicycle_path -hold -end -from  [get_clocks {LTC2208_122MHz}]  -to  [get_clocks *] 1


#**************************************************************
# Set Maximum Delay
#**************************************************************



#**************************************************************
# Set Minimum Delay
#**************************************************************



#**************************************************************
# Set Input Transition
#**************************************************************

