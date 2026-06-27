## Generated SDC file "Mercury.out.sdc"

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

## DATE    "Sun Mar 17 12:51:30 2013"

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
create_clock -name {C122_clk} -period 8.138 -waveform { 0.000 4.069 } [get_ports {C122_clk}]
create_clock -name {AUX_CLK} -period 100.000 -waveform { 0.000 50.000 } [get_ports {AUX_CLK}]
create_clock -name {OSC_10MHZ} -period 100 -waveform { 0.000 50 } [get_ports {OSC_10MHZ}]
create_clock -name {spc[1]} -period 1302 [get_registers {spc[1]}]
create_clock -name {i2c_interface:i2c_interface|deglitch:deglitch_scl|out} -period 10000 [get_registers {i2c_interface:i2c_interface|deglitch:deglitch_scl|out}]
create_clock -name {i2c_interface:i2c_interface|deglitch:deglitch_sda|out} -period 10000 [get_registers {i2c_interface:i2c_interface|deglitch:deglitch_sda|out}]


#create_clock -name {spc[1]} -period 10000 -waveform { 0.000 5000 } [get_registers {spc[1]}]
#create_clock -name {spc[1]} -period 1.000 -waveform { 0.000 0.500 } [get_registers {spc[1]}]
#create_clock -name {clk_lrclk_gen:clrgen|BCLK} -period 1.000 -waveform { 0.000 0.500 } [get_registers {clk_lrclk_gen:clrgen|BCLK}]
#create_clock -name {clk_lrclk_gen:lrgen|BCLK} -period 1.000 -waveform { 0.000 0.500 } [get_registers {clk_lrclk_gen:lrgen|BCLK}]
#create_clock -name {spc[1]} -period 10000 -waveform { 0.000 5000 } [get_registers {spc[1]}]
#create_clock -name {clk_div:TLVCLK|clk_out} -period 1.000 -waveform { 0.000 0.500 } [get_registers {clk_div:TLVCLK|clk_out}]

derive_pll_clocks

#derive_clock_uncertainty

#**************************************************************
# Create Generated Clock
#**************************************************************
create_generated_clock -name {clk_lrclk_gen:clrgen|BCLK} -source [get_ports {C122_clk}] -divide_by 40 -master_clock {C122_clk} [get_registers {clk_lrclk_gen:clrgen|BCLK}] 
create_generated_clock -name {clk_div:TLVCLK|clk_out} -source [get_ports {C122_clk}] -divide_by 10 -master_clock {C122_clk} [get_registers {clk_div:TLVCLK|clk_out}] 
create_generated_clock -name {clk_lrclk_gen:lrgen|BCLK} -source [get_ports {C122_clk}] -divide_by 640 -master_clock {C122_clk} [get_registers {clk_lrclk_gen:lrgen|BCLK}] 

#create_generated_clock -name {spc[1]} -source [get_ports {BCLK}] -divide_by 4 -master_clock {C122_clk} [get_registers {spc[1]}] 
#create_generated_clock -name {clk_lrclk_gen:clrgen|BCLK} -source [get_ports {C122_clk}] -divide_by 640 -master_clock {C122_clk} [get_registers {clk_lrclk_gen:clrgen|BCLK}] 
#create_generated_clock -name {clk_lrclk_gen:clrgen|c122_cbclk} -source [get_ports {C122_clk}] -divide_by 640 -master_clock {C122_clk} [get_registers {clk_lrclk_gen:clrgen|c122_cbclk}] 
#create_generated_clock -name {PLL2_inst|altpll_component|auto_generated|pll1|clk[0]} -source [get_pins {PLL2_inst|altpll_component|auto_generated|pll1|inclk[0]}] -duty_cycle 50.000 -multiply_by 1 -divide_by 125 -master_clock {AUX_CLK} [get_pins {PLL2_inst|altpll_component|auto_generated|pll1|clk[0]}] 
#create_generated_clock -name {PLL_inst|altpll_component|auto_generated|pll1|clk[0]} -source [get_pins {PLL_inst|altpll_component|auto_generated|pll1|inclk[0]}] -duty_cycle 50.000 -multiply_by 1 -divide_by 1536 -master_clock {C122_clk} [get_pins {PLL_inst|altpll_component|auto_generated|pll1|clk[0]}] 

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
set_clock_groups -asynchronous 	-group {spc[1]} \
				-group {i2c_interface:i2c_interface|deglitch:deglitch_scl|out} \
				-group {i2c_interface:i2c_interface|deglitch:deglitch_sda|out} 

#-group {AUX_CLK} \
#				-group {clk_lrclk_gen:lrgen|BCLK} \
#				-group {clk_lrclk_gen:clrgen|BCLK} \
#				-group {clk_div:TLVCLK|clk_out} \
#				-group {i2c_interface:interface|deglitch:deglitch_scl|out} \
#				-group {i2c_interface:interface|deglitch:deglitch_sda|out} \
#				-group {OSC_10MHZ}
#
#				-group {spc[1]} \
#**************************************************************
# Set False Path
#**************************************************************



#**************************************************************
# Set Multicycle Path
#**************************************************************
set_multicycle_path -from C122_clk -to C122_clk -setup -end 3
set_multicycle_path -from C122_clk -to C122_clk -hold 2
set_multicycle_path -from C122_clk -to spc[1] -setup 2
set_multicycle_path -from C122_clk -to spc[1] -hold 1
set_multicycle_path -from spc[1] -to C122_clk -setup 2
set_multicycle_path -from spc[1] -to C122_clk -hold 1
set_multicycle_path -from spc[1] -to clk_lrclk_gen:clrgen|BCLK -setup 2
set_multicycle_path -from spc[1] -to clk_lrclk_gen:clrgen|BCLK -hold 1
set_multicycle_path -from i2c_interface:i2c_interface|deglitch:deglitch_scl|out -to clk_div:TLVCLK|clk_out -setup 2
set_multicycle_path -from i2c_interface:i2c_interface|deglitch:deglitch_scl|out -to clk_div:TLVCLK|clk_out -hold 1
set_multicycle_path -from i2c_interface:i2c_interface|deglitch:deglitch_sda|out -to clk_div:TLVCLK|clk_out -setup 2
set_multicycle_path -from i2c_interface:i2c_interface|deglitch:deglitch_sda|out -to clk_div:TLVCLK|clk_out -hold 1
set_multicycle_path -from clk_div:TLVCLK|clk_out -to i2c_interface:i2c_interface|deglitch:deglitch_scl|out -setup 2
set_multicycle_path -from clk_div:TLVCLK|clk_out -to i2c_interface:i2c_interface|deglitch:deglitch_scl|out -hold 1
set_multicycle_path -from clk_div:TLVCLK|clk_out -to i2c_interface:i2c_interface|deglitch:deglitch_sda|out -setup 2
set_multicycle_path -from clk_div:TLVCLK|clk_out -to i2c_interface:i2c_interface|deglitch:deglitch_sda|out -hold 1



#**************************************************************
# Set Maximum Delay
#**************************************************************



#**************************************************************
# Set Minimum Delay
#**************************************************************



#**************************************************************
# Set Input Transition
#**************************************************************

