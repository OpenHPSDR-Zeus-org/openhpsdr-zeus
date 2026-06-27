## Generated SDC file "Penelope.out.sdc"

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

## DATE    "Sun Mar 17 09:28:12 2013"

##
## DEVICE  "EP2C8Q208C8"
##


#**************************************************************
# Time Information
#**************************************************************

set_time_format -unit ns -decimal_places 3



#**************************************************************
# Create Clock
#**************************************************************

create_clock -name {C122_clk} -period 8.138 -waveform { 0.000 4.069 } [get_ports {C122_clk}]
create_clock -name {_10MHZ} -period 100.000 -waveform { 0.000 50.000 } [get_ports {_10MHZ}]
create_clock -name I2C:I2C_inst|deglitch:deglitch_scl|out -period 5000 I2C:I2C_inst|deglitch:deglitch_scl|out
create_clock -name I2C:I2C_inst|deglitch:deglitch_sda|out -period 5000 I2C:I2C_inst|deglitch:deglitch_sda|out

derive_pll_clocks

#derive_clock_uncertainties


#**************************************************************
# Create Generated Clock
#**************************************************************

create_generated_clock -name {clk_lrclk_gen:clrgen|BCLK} -source [get_ports {C122_clk}] -divide_by 40 -master_clock {C122_clk} [get_registers {clk_lrclk_gen:clrgen|BCLK}] 
create_generated_clock -name {clk_div:TLVCLK|clk_out} -source [get_ports {C122_clk}] -divide_by 10 -master_clock {C122_clk} [get_registers {clk_div:TLVCLK|clk_out}] 


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
set_clock_groups -asynchronous 	-group  {_10MHZ} \
				-group  {clk_div:TLVCLK|clk_out} \
					{I2C:I2C_inst|deglitch:deglitch_scl|out} \
					{I2C:I2C_inst|deglitch:deglitch_sda|out}


#**************************************************************
# Set False Path
#**************************************************************
set_false_path -from C122_clk -to {clk_div:TLVCLK|clk_out}


#**************************************************************
# Set Multicycle Path
#**************************************************************
set_multicycle_path -from C122_clk -to C122_clk -setup 2
set_multicycle_path -from C122_clk -to C122_clk -hold 1


#**************************************************************
# Set Maximum Delay
#**************************************************************



#**************************************************************
# Set Minimum Delay
#**************************************************************



#**************************************************************
# Set Input Transition
#**************************************************************

