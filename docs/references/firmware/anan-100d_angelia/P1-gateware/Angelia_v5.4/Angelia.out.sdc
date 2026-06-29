## Generated SDC file "Angelia.out.sdc"

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

## DATE    "Thu Mar 06 15:00:15 2014"

##
## DEVICE  "EP4CE115F29C8"
##


#**************************************************************
# Time Information
#**************************************************************

set_time_format -unit ns -decimal_places 3



#**************************************************************
# Create Clock
#**************************************************************

create_clock -name {PHY_CLK125} -period 8.000 -waveform { 0.000 4.000 } [get_ports {PHY_CLK125}]
create_clock -name {LTC2208_122MHz} -period 8.138 -waveform { 0.000 4.069 } [get_ports {LTC2208_122MHz}]
create_clock -name {LTC2208_122MHz_2} -period 8.138 -waveform { 0.000 4.069 } [get_ports {LTC2208_122MHz_2}]
create_clock -name {OSC_10MHZ} -period 100.000 -waveform { 0.000 50.000 } [get_ports {OSC_10MHZ}]
create_clock -name {_122MHz} -period 8.138 -waveform { 0.000 4.069 } [get_ports {_122MHz}]
create_clock -name {PHY_RX_CLOCK} -period 40.000 -waveform { 0.000 20.000 } [get_ports {PHY_RX_CLOCK}]
create_clock -name {virt_PHY_CLK125} -period 8.000 -waveform { 0.000 4.000 } 
create_clock -name {virt_PHY_RX_CLOCK} -period 40.000 -waveform { 0.000 20.000 } 
create_clock -name {virt_122MHz} -period 8.138 -waveform { 0.000 4.069 } 
create_clock -name {virt_122MHz_2} -period 8.138 -waveform { 0.000 4.069 } 


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
create_generated_clock -name {Angelia_clk_lrclk_gen:clrgen|BCLK} -source [get_ports {LTC2208_122MHz}] -divide_by 40 -master_clock {LTC2208_122MHz} [get_registers {Angelia_clk_lrclk_gen:clrgen|BCLK}] 
create_generated_clock -name {spc[2]} -source [get_registers {Angelia_clk_lrclk_gen:clrgen|BCLK}] -divide_by 8 -master_clock {Angelia_clk_lrclk_gen:clrgen|BCLK} [get_registers {spc[2]}] 
create_generated_clock -name {PHY_RX_CLOCK_2} -source [get_ports {PHY_RX_CLOCK}] -divide_by 2 -master_clock {PHY_RX_CLOCK} [get_registers {PHY_RX_CLOCK_2}] 
create_generated_clock -name {Attenuator:Attenuator_ADC1|clk_2} -source [get_pins {PLL_IF_inst|altpll_component|auto_generated|pll1|inclk[0]}] -master_clock {LTC2208_122MHz} [get_registers {Attenuator:Attenuator_ADC1|clk_2}] 
create_generated_clock -name {Attenuator:Attenuator_ADC2|clk_2} -source [get_pins {PLL_IF_inst|altpll_component|auto_generated|pll1|inclk[0]}] -master_clock {LTC2208_122MHz} [get_registers {Attenuator:Attenuator_ADC2|clk_2}] 


#**************************************************************
# Set Clock Latency
#**************************************************************



#**************************************************************
# Set Clock Uncertainty
#**************************************************************

set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -setup 0.150  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -hold 0.160  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -setup 0.150  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -hold 0.160  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {_122MHz}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {_122MHz}] -hold 0.080  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {_122MHz}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {_122MHz}] -hold 0.080  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {LTC2208_122MHz_2}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {LTC2208_122MHz_2}] -hold 0.080  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {LTC2208_122MHz_2}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {LTC2208_122MHz_2}] -hold 0.080  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {LTC2208_122MHz}] -setup 0.100  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {LTC2208_122MHz}] -hold 0.070  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {LTC2208_122MHz}] -setup 0.100  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {LTC2208_122MHz}] -hold 0.070  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {Attenuator:Attenuator_ADC1|clk_2}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {Attenuator:Attenuator_ADC1|clk_2}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {Attenuator:Attenuator_ADC2|clk_2}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {Attenuator:Attenuator_ADC2|clk_2}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {spc[2]}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {spc[2]}] -hold 0.080  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {spc[2]}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {spc[2]}] -hold 0.080  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -setup 0.150  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -hold 0.160  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -setup 0.150  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -hold 0.160  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {_122MHz}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {_122MHz}] -hold 0.080  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {_122MHz}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {_122MHz}] -hold 0.080  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {LTC2208_122MHz_2}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {LTC2208_122MHz_2}] -hold 0.080  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {LTC2208_122MHz_2}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {LTC2208_122MHz_2}] -hold 0.080  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {LTC2208_122MHz}] -setup 0.100  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {LTC2208_122MHz}] -hold 0.070  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {LTC2208_122MHz}] -setup 0.100  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {LTC2208_122MHz}] -hold 0.070  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {Attenuator:Attenuator_ADC1|clk_2}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {Attenuator:Attenuator_ADC1|clk_2}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {Attenuator:Attenuator_ADC2|clk_2}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {Attenuator:Attenuator_ADC2|clk_2}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {spc[2]}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {spc[2]}] -hold 0.080  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {spc[2]}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {spc[2]}] -hold 0.080  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.160  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.150  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.160  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.150  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {LTC2208_122MHz}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {LTC2208_122MHz}] -hold 0.080  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {LTC2208_122MHz}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {LTC2208_122MHz}] -hold 0.080  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.080  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.080  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] -setup 0.160  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] -hold 0.150  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] -setup 0.160  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] -hold 0.150  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.160  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.150  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.160  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.150  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {LTC2208_122MHz}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {LTC2208_122MHz}] -hold 0.080  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {LTC2208_122MHz}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {LTC2208_122MHz}] -hold 0.080  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.080  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.080  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] -setup 0.160  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] -hold 0.150  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] -setup 0.160  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] -hold 0.150  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.160  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.150  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.160  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.150  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {_122MHz}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {_122MHz}] -hold 0.080  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {_122MHz}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {_122MHz}] -hold 0.080  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PHY_CLK125}] -setup 0.100  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PHY_CLK125}] -hold 0.070  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PHY_CLK125}] -setup 0.100  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PHY_CLK125}] -hold 0.070  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.080  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.080  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.160  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.150  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.160  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.150  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {_122MHz}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {_122MHz}] -hold 0.080  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {_122MHz}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {_122MHz}] -hold 0.080  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PHY_CLK125}] -setup 0.100  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PHY_CLK125}] -hold 0.070  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PHY_CLK125}] -setup 0.100  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PHY_CLK125}] -hold 0.070  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.080  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.080  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {_122MHz}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {_122MHz}] -hold 0.080  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {_122MHz}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {_122MHz}] -hold 0.080  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {PHY_CLK125}] -setup 0.100  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {PHY_CLK125}] -hold 0.070  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {PHY_CLK125}] -setup 0.100  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {PHY_CLK125}] -hold 0.070  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.080  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.080  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {_122MHz}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {_122MHz}] -hold 0.080  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {_122MHz}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {_122MHz}] -hold 0.080  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {PHY_CLK125}] -setup 0.100  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {PHY_CLK125}] -hold 0.070  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {PHY_CLK125}] -setup 0.100  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {PHY_CLK125}] -hold 0.070  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.080  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.080  
set_clock_uncertainty -rise_from [get_clocks {virt_122MHz}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.070  
set_clock_uncertainty -rise_from [get_clocks {virt_122MHz}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.090  
set_clock_uncertainty -rise_from [get_clocks {virt_122MHz}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.070  
set_clock_uncertainty -rise_from [get_clocks {virt_122MHz}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.090  
set_clock_uncertainty -rise_from [get_clocks {virt_122MHz}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.060  
set_clock_uncertainty -rise_from [get_clocks {virt_122MHz}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.090  
set_clock_uncertainty -rise_from [get_clocks {virt_122MHz}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.060  
set_clock_uncertainty -rise_from [get_clocks {virt_122MHz}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.090  
set_clock_uncertainty -rise_from [get_clocks {virt_122MHz}] -rise_to [get_clocks {LTC2208_122MHz}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {virt_122MHz}] -fall_to [get_clocks {LTC2208_122MHz}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {virt_122MHz}] -rise_to [get_clocks {Angelia_clk_lrclk_gen:clrgen|BCLK}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {virt_122MHz}] -fall_to [get_clocks {Angelia_clk_lrclk_gen:clrgen|BCLK}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {virt_122MHz}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.070  
set_clock_uncertainty -fall_from [get_clocks {virt_122MHz}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.090  
set_clock_uncertainty -fall_from [get_clocks {virt_122MHz}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.070  
set_clock_uncertainty -fall_from [get_clocks {virt_122MHz}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.090  
set_clock_uncertainty -fall_from [get_clocks {virt_122MHz}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.060  
set_clock_uncertainty -fall_from [get_clocks {virt_122MHz}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.090  
set_clock_uncertainty -fall_from [get_clocks {virt_122MHz}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.060  
set_clock_uncertainty -fall_from [get_clocks {virt_122MHz}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.090  
set_clock_uncertainty -fall_from [get_clocks {virt_122MHz}] -rise_to [get_clocks {LTC2208_122MHz}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {virt_122MHz}] -fall_to [get_clocks {LTC2208_122MHz}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {virt_122MHz}] -rise_to [get_clocks {Angelia_clk_lrclk_gen:clrgen|BCLK}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {virt_122MHz}] -fall_to [get_clocks {Angelia_clk_lrclk_gen:clrgen|BCLK}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {virt_PHY_RX_CLOCK}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.070  
set_clock_uncertainty -rise_from [get_clocks {virt_PHY_RX_CLOCK}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.090  
set_clock_uncertainty -rise_from [get_clocks {virt_PHY_RX_CLOCK}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.070  
set_clock_uncertainty -rise_from [get_clocks {virt_PHY_RX_CLOCK}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.090  
set_clock_uncertainty -rise_from [get_clocks {virt_PHY_RX_CLOCK}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -setup 0.060  
set_clock_uncertainty -rise_from [get_clocks {virt_PHY_RX_CLOCK}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -hold 0.090  
set_clock_uncertainty -rise_from [get_clocks {virt_PHY_RX_CLOCK}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -setup 0.060  
set_clock_uncertainty -rise_from [get_clocks {virt_PHY_RX_CLOCK}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -hold 0.090  
set_clock_uncertainty -rise_from [get_clocks {virt_PHY_RX_CLOCK}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.060  
set_clock_uncertainty -rise_from [get_clocks {virt_PHY_RX_CLOCK}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.090  
set_clock_uncertainty -rise_from [get_clocks {virt_PHY_RX_CLOCK}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.060  
set_clock_uncertainty -rise_from [get_clocks {virt_PHY_RX_CLOCK}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.090  
set_clock_uncertainty -rise_from [get_clocks {virt_PHY_RX_CLOCK}] -rise_to [get_clocks {PHY_RX_CLOCK}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {virt_PHY_RX_CLOCK}] -fall_to [get_clocks {PHY_RX_CLOCK}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {virt_PHY_RX_CLOCK}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.070  
set_clock_uncertainty -fall_from [get_clocks {virt_PHY_RX_CLOCK}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.090  
set_clock_uncertainty -fall_from [get_clocks {virt_PHY_RX_CLOCK}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.070  
set_clock_uncertainty -fall_from [get_clocks {virt_PHY_RX_CLOCK}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.090  
set_clock_uncertainty -fall_from [get_clocks {virt_PHY_RX_CLOCK}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -setup 0.060  
set_clock_uncertainty -fall_from [get_clocks {virt_PHY_RX_CLOCK}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -hold 0.090  
set_clock_uncertainty -fall_from [get_clocks {virt_PHY_RX_CLOCK}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -setup 0.060  
set_clock_uncertainty -fall_from [get_clocks {virt_PHY_RX_CLOCK}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -hold 0.090  
set_clock_uncertainty -fall_from [get_clocks {virt_PHY_RX_CLOCK}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.060  
set_clock_uncertainty -fall_from [get_clocks {virt_PHY_RX_CLOCK}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.090  
set_clock_uncertainty -fall_from [get_clocks {virt_PHY_RX_CLOCK}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.060  
set_clock_uncertainty -fall_from [get_clocks {virt_PHY_RX_CLOCK}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.090  
set_clock_uncertainty -fall_from [get_clocks {virt_PHY_RX_CLOCK}] -rise_to [get_clocks {PHY_RX_CLOCK}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {virt_PHY_RX_CLOCK}] -fall_to [get_clocks {PHY_RX_CLOCK}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK}] -rise_to [get_clocks {PHY_RX_CLOCK}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK}] -fall_to [get_clocks {PHY_RX_CLOCK}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK}] -rise_to [get_clocks {PHY_RX_CLOCK_2}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK}] -fall_to [get_clocks {PHY_RX_CLOCK_2}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK}] -rise_to [get_clocks {PHY_RX_CLOCK}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK}] -fall_to [get_clocks {PHY_RX_CLOCK}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK}] -rise_to [get_clocks {PHY_RX_CLOCK_2}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK}] -fall_to [get_clocks {PHY_RX_CLOCK_2}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {_122MHz}] -rise_to [get_clocks {_122MHz}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {_122MHz}] -fall_to [get_clocks {_122MHz}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {_122MHz}] -rise_to [get_clocks {LTC2208_122MHz}]  0.040  
set_clock_uncertainty -rise_from [get_clocks {_122MHz}] -fall_to [get_clocks {LTC2208_122MHz}]  0.040  
set_clock_uncertainty -fall_from [get_clocks {_122MHz}] -rise_to [get_clocks {_122MHz}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {_122MHz}] -fall_to [get_clocks {_122MHz}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {_122MHz}] -rise_to [get_clocks {LTC2208_122MHz}]  0.040  
set_clock_uncertainty -fall_from [get_clocks {_122MHz}] -fall_to [get_clocks {LTC2208_122MHz}]  0.040  
set_clock_uncertainty -rise_from [get_clocks {LTC2208_122MHz_2}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.080  
set_clock_uncertainty -rise_from [get_clocks {LTC2208_122MHz_2}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.110  
set_clock_uncertainty -rise_from [get_clocks {LTC2208_122MHz_2}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.080  
set_clock_uncertainty -rise_from [get_clocks {LTC2208_122MHz_2}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.110  
set_clock_uncertainty -rise_from [get_clocks {LTC2208_122MHz_2}] -rise_to [get_clocks {LTC2208_122MHz_2}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {LTC2208_122MHz_2}] -fall_to [get_clocks {LTC2208_122MHz_2}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {LTC2208_122MHz_2}] -rise_to [get_clocks {LTC2208_122MHz}]  0.040  
set_clock_uncertainty -rise_from [get_clocks {LTC2208_122MHz_2}] -fall_to [get_clocks {LTC2208_122MHz}]  0.040  
set_clock_uncertainty -fall_from [get_clocks {LTC2208_122MHz_2}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.080  
set_clock_uncertainty -fall_from [get_clocks {LTC2208_122MHz_2}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.110  
set_clock_uncertainty -fall_from [get_clocks {LTC2208_122MHz_2}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.080  
set_clock_uncertainty -fall_from [get_clocks {LTC2208_122MHz_2}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.110  
set_clock_uncertainty -fall_from [get_clocks {LTC2208_122MHz_2}] -rise_to [get_clocks {LTC2208_122MHz_2}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {LTC2208_122MHz_2}] -fall_to [get_clocks {LTC2208_122MHz_2}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {LTC2208_122MHz_2}] -rise_to [get_clocks {LTC2208_122MHz}]  0.040  
set_clock_uncertainty -fall_from [get_clocks {LTC2208_122MHz_2}] -fall_to [get_clocks {LTC2208_122MHz}]  0.040  
set_clock_uncertainty -rise_from [get_clocks {LTC2208_122MHz}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.070  
set_clock_uncertainty -rise_from [get_clocks {LTC2208_122MHz}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.100  
set_clock_uncertainty -rise_from [get_clocks {LTC2208_122MHz}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.070  
set_clock_uncertainty -rise_from [get_clocks {LTC2208_122MHz}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.100  
set_clock_uncertainty -rise_from [get_clocks {LTC2208_122MHz}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -setup 0.080  
set_clock_uncertainty -rise_from [get_clocks {LTC2208_122MHz}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -hold 0.110  
set_clock_uncertainty -rise_from [get_clocks {LTC2208_122MHz}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -setup 0.080  
set_clock_uncertainty -rise_from [get_clocks {LTC2208_122MHz}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -hold 0.110  
set_clock_uncertainty -rise_from [get_clocks {LTC2208_122MHz}] -rise_to [get_clocks {_122MHz}]  0.040  
set_clock_uncertainty -rise_from [get_clocks {LTC2208_122MHz}] -fall_to [get_clocks {_122MHz}]  0.040  
set_clock_uncertainty -rise_from [get_clocks {LTC2208_122MHz}] -rise_to [get_clocks {LTC2208_122MHz_2}]  0.040  
set_clock_uncertainty -rise_from [get_clocks {LTC2208_122MHz}] -fall_to [get_clocks {LTC2208_122MHz_2}]  0.040  
set_clock_uncertainty -rise_from [get_clocks {LTC2208_122MHz}] -rise_to [get_clocks {LTC2208_122MHz}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {LTC2208_122MHz}] -fall_to [get_clocks {LTC2208_122MHz}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {LTC2208_122MHz}] -rise_to [get_clocks {spc[2]}]  0.030  
set_clock_uncertainty -rise_from [get_clocks {LTC2208_122MHz}] -fall_to [get_clocks {spc[2]}]  0.030  
set_clock_uncertainty -rise_from [get_clocks {LTC2208_122MHz}] -rise_to [get_clocks {Angelia_clk_lrclk_gen:clrgen|BCLK}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {LTC2208_122MHz}] -fall_to [get_clocks {Angelia_clk_lrclk_gen:clrgen|BCLK}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {LTC2208_122MHz}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.070  
set_clock_uncertainty -fall_from [get_clocks {LTC2208_122MHz}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.100  
set_clock_uncertainty -fall_from [get_clocks {LTC2208_122MHz}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.070  
set_clock_uncertainty -fall_from [get_clocks {LTC2208_122MHz}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.100  
set_clock_uncertainty -fall_from [get_clocks {LTC2208_122MHz}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -setup 0.080  
set_clock_uncertainty -fall_from [get_clocks {LTC2208_122MHz}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -hold 0.110  
set_clock_uncertainty -fall_from [get_clocks {LTC2208_122MHz}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -setup 0.080  
set_clock_uncertainty -fall_from [get_clocks {LTC2208_122MHz}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -hold 0.110  
set_clock_uncertainty -fall_from [get_clocks {LTC2208_122MHz}] -rise_to [get_clocks {_122MHz}]  0.040  
set_clock_uncertainty -fall_from [get_clocks {LTC2208_122MHz}] -fall_to [get_clocks {_122MHz}]  0.040  
set_clock_uncertainty -fall_from [get_clocks {LTC2208_122MHz}] -rise_to [get_clocks {LTC2208_122MHz_2}]  0.040  
set_clock_uncertainty -fall_from [get_clocks {LTC2208_122MHz}] -fall_to [get_clocks {LTC2208_122MHz_2}]  0.040  
set_clock_uncertainty -fall_from [get_clocks {LTC2208_122MHz}] -rise_to [get_clocks {LTC2208_122MHz}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {LTC2208_122MHz}] -fall_to [get_clocks {LTC2208_122MHz}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {LTC2208_122MHz}] -rise_to [get_clocks {spc[2]}]  0.030  
set_clock_uncertainty -fall_from [get_clocks {LTC2208_122MHz}] -fall_to [get_clocks {spc[2]}]  0.030  
set_clock_uncertainty -fall_from [get_clocks {LTC2208_122MHz}] -rise_to [get_clocks {Angelia_clk_lrclk_gen:clrgen|BCLK}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {LTC2208_122MHz}] -fall_to [get_clocks {Angelia_clk_lrclk_gen:clrgen|BCLK}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PHY_CLK125}] -rise_to [get_clocks {PHY_CLK125}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PHY_CLK125}] -fall_to [get_clocks {PHY_CLK125}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PHY_CLK125}] -rise_to [get_clocks {PHY_CLK125}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PHY_CLK125}] -fall_to [get_clocks {PHY_CLK125}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK_2}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -setup 0.080  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK_2}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -hold 0.110  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK_2}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -setup 0.080  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK_2}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -hold 0.110  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK_2}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -setup 0.080  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK_2}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -hold 0.110  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK_2}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -setup 0.080  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK_2}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -hold 0.110  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK_2}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.080  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK_2}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.110  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK_2}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.080  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK_2}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.110  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK_2}] -rise_to [get_clocks {PHY_RX_CLOCK}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK_2}] -fall_to [get_clocks {PHY_RX_CLOCK}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK_2}] -rise_to [get_clocks {PHY_RX_CLOCK_2}]  0.030  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK_2}] -fall_to [get_clocks {PHY_RX_CLOCK_2}]  0.030  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK_2}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -setup 0.080  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK_2}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -hold 0.110  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK_2}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -setup 0.080  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK_2}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -hold 0.110  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK_2}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -setup 0.080  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK_2}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -hold 0.110  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK_2}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -setup 0.080  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK_2}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -hold 0.110  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK_2}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.080  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK_2}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.110  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK_2}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.080  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK_2}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.110  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK_2}] -rise_to [get_clocks {PHY_RX_CLOCK}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK_2}] -fall_to [get_clocks {PHY_RX_CLOCK}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK_2}] -rise_to [get_clocks {PHY_RX_CLOCK_2}]  0.030  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK_2}] -fall_to [get_clocks {PHY_RX_CLOCK_2}]  0.030  
set_clock_uncertainty -rise_from [get_clocks {Attenuator:Attenuator_ADC1|clk_2}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {Attenuator:Attenuator_ADC1|clk_2}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {Attenuator:Attenuator_ADC1|clk_2}] -rise_to [get_clocks {_122MHz}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {Attenuator:Attenuator_ADC1|clk_2}] -rise_to [get_clocks {_122MHz}] -hold 0.080  
set_clock_uncertainty -rise_from [get_clocks {Attenuator:Attenuator_ADC1|clk_2}] -fall_to [get_clocks {_122MHz}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {Attenuator:Attenuator_ADC1|clk_2}] -fall_to [get_clocks {_122MHz}] -hold 0.080  
set_clock_uncertainty -rise_from [get_clocks {Attenuator:Attenuator_ADC1|clk_2}] -rise_to [get_clocks {Attenuator:Attenuator_ADC1|clk_2}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {Attenuator:Attenuator_ADC1|clk_2}] -fall_to [get_clocks {Attenuator:Attenuator_ADC1|clk_2}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {Attenuator:Attenuator_ADC1|clk_2}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {Attenuator:Attenuator_ADC1|clk_2}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {Attenuator:Attenuator_ADC1|clk_2}] -rise_to [get_clocks {_122MHz}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {Attenuator:Attenuator_ADC1|clk_2}] -rise_to [get_clocks {_122MHz}] -hold 0.080  
set_clock_uncertainty -fall_from [get_clocks {Attenuator:Attenuator_ADC1|clk_2}] -fall_to [get_clocks {_122MHz}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {Attenuator:Attenuator_ADC1|clk_2}] -fall_to [get_clocks {_122MHz}] -hold 0.080  
set_clock_uncertainty -fall_from [get_clocks {Attenuator:Attenuator_ADC1|clk_2}] -rise_to [get_clocks {Attenuator:Attenuator_ADC1|clk_2}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {Attenuator:Attenuator_ADC1|clk_2}] -fall_to [get_clocks {Attenuator:Attenuator_ADC1|clk_2}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {Attenuator:Attenuator_ADC2|clk_2}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {Attenuator:Attenuator_ADC2|clk_2}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {Attenuator:Attenuator_ADC2|clk_2}] -rise_to [get_clocks {_122MHz}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {Attenuator:Attenuator_ADC2|clk_2}] -rise_to [get_clocks {_122MHz}] -hold 0.080  
set_clock_uncertainty -rise_from [get_clocks {Attenuator:Attenuator_ADC2|clk_2}] -fall_to [get_clocks {_122MHz}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {Attenuator:Attenuator_ADC2|clk_2}] -fall_to [get_clocks {_122MHz}] -hold 0.080  
set_clock_uncertainty -rise_from [get_clocks {Attenuator:Attenuator_ADC2|clk_2}] -rise_to [get_clocks {Attenuator:Attenuator_ADC2|clk_2}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {Attenuator:Attenuator_ADC2|clk_2}] -fall_to [get_clocks {Attenuator:Attenuator_ADC2|clk_2}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {Attenuator:Attenuator_ADC2|clk_2}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {Attenuator:Attenuator_ADC2|clk_2}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {Attenuator:Attenuator_ADC2|clk_2}] -rise_to [get_clocks {_122MHz}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {Attenuator:Attenuator_ADC2|clk_2}] -rise_to [get_clocks {_122MHz}] -hold 0.080  
set_clock_uncertainty -fall_from [get_clocks {Attenuator:Attenuator_ADC2|clk_2}] -fall_to [get_clocks {_122MHz}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {Attenuator:Attenuator_ADC2|clk_2}] -fall_to [get_clocks {_122MHz}] -hold 0.080  
set_clock_uncertainty -fall_from [get_clocks {Attenuator:Attenuator_ADC2|clk_2}] -rise_to [get_clocks {Attenuator:Attenuator_ADC2|clk_2}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {Attenuator:Attenuator_ADC2|clk_2}] -fall_to [get_clocks {Attenuator:Attenuator_ADC2|clk_2}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {_122MHz}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {_122MHz}] -hold 0.080  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {_122MHz}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {_122MHz}] -hold 0.080  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {_122MHz}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {_122MHz}] -hold 0.080  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {_122MHz}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {_122MHz}] -hold 0.080  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {_122MHz}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {_122MHz}] -hold 0.080  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {_122MHz}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {_122MHz}] -hold 0.080  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {_122MHz}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {_122MHz}] -hold 0.080  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {_122MHz}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {_122MHz}] -hold 0.080  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {spc[2]}] -rise_to [get_clocks {_122MHz}]  0.040  
set_clock_uncertainty -rise_from [get_clocks {spc[2]}] -fall_to [get_clocks {_122MHz}]  0.040  
set_clock_uncertainty -rise_from [get_clocks {spc[2]}] -rise_to [get_clocks {spc[2]}]  0.030  
set_clock_uncertainty -rise_from [get_clocks {spc[2]}] -fall_to [get_clocks {spc[2]}]  0.030  
set_clock_uncertainty -rise_from [get_clocks {spc[2]}] -rise_to [get_clocks {Angelia_clk_lrclk_gen:clrgen|BCLK}]  0.030  
set_clock_uncertainty -rise_from [get_clocks {spc[2]}] -fall_to [get_clocks {Angelia_clk_lrclk_gen:clrgen|BCLK}]  0.030  
set_clock_uncertainty -fall_from [get_clocks {spc[2]}] -rise_to [get_clocks {_122MHz}]  0.040  
set_clock_uncertainty -fall_from [get_clocks {spc[2]}] -fall_to [get_clocks {_122MHz}]  0.040  
set_clock_uncertainty -fall_from [get_clocks {spc[2]}] -rise_to [get_clocks {spc[2]}]  0.030  
set_clock_uncertainty -fall_from [get_clocks {spc[2]}] -fall_to [get_clocks {spc[2]}]  0.030  
set_clock_uncertainty -fall_from [get_clocks {spc[2]}] -rise_to [get_clocks {Angelia_clk_lrclk_gen:clrgen|BCLK}]  0.030  
set_clock_uncertainty -fall_from [get_clocks {spc[2]}] -fall_to [get_clocks {Angelia_clk_lrclk_gen:clrgen|BCLK}]  0.030  
set_clock_uncertainty -rise_from [get_clocks {Angelia_clk_lrclk_gen:clrgen|BCLK}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.080  
set_clock_uncertainty -rise_from [get_clocks {Angelia_clk_lrclk_gen:clrgen|BCLK}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.100  
set_clock_uncertainty -rise_from [get_clocks {Angelia_clk_lrclk_gen:clrgen|BCLK}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.080  
set_clock_uncertainty -rise_from [get_clocks {Angelia_clk_lrclk_gen:clrgen|BCLK}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.100  
set_clock_uncertainty -rise_from [get_clocks {Angelia_clk_lrclk_gen:clrgen|BCLK}] -rise_to [get_clocks {_122MHz}]  0.040  
set_clock_uncertainty -rise_from [get_clocks {Angelia_clk_lrclk_gen:clrgen|BCLK}] -fall_to [get_clocks {_122MHz}]  0.040  
set_clock_uncertainty -rise_from [get_clocks {Angelia_clk_lrclk_gen:clrgen|BCLK}] -rise_to [get_clocks {LTC2208_122MHz}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {Angelia_clk_lrclk_gen:clrgen|BCLK}] -fall_to [get_clocks {LTC2208_122MHz}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {Angelia_clk_lrclk_gen:clrgen|BCLK}] -rise_to [get_clocks {spc[2]}]  0.030  
set_clock_uncertainty -rise_from [get_clocks {Angelia_clk_lrclk_gen:clrgen|BCLK}] -fall_to [get_clocks {spc[2]}]  0.030  
set_clock_uncertainty -rise_from [get_clocks {Angelia_clk_lrclk_gen:clrgen|BCLK}] -rise_to [get_clocks {Angelia_clk_lrclk_gen:clrgen|BCLK}]  0.030  
set_clock_uncertainty -rise_from [get_clocks {Angelia_clk_lrclk_gen:clrgen|BCLK}] -fall_to [get_clocks {Angelia_clk_lrclk_gen:clrgen|BCLK}]  0.030  
set_clock_uncertainty -fall_from [get_clocks {Angelia_clk_lrclk_gen:clrgen|BCLK}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.080  
set_clock_uncertainty -fall_from [get_clocks {Angelia_clk_lrclk_gen:clrgen|BCLK}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.100  
set_clock_uncertainty -fall_from [get_clocks {Angelia_clk_lrclk_gen:clrgen|BCLK}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.080  
set_clock_uncertainty -fall_from [get_clocks {Angelia_clk_lrclk_gen:clrgen|BCLK}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.100  
set_clock_uncertainty -fall_from [get_clocks {Angelia_clk_lrclk_gen:clrgen|BCLK}] -rise_to [get_clocks {_122MHz}]  0.040  
set_clock_uncertainty -fall_from [get_clocks {Angelia_clk_lrclk_gen:clrgen|BCLK}] -fall_to [get_clocks {_122MHz}]  0.040  
set_clock_uncertainty -fall_from [get_clocks {Angelia_clk_lrclk_gen:clrgen|BCLK}] -rise_to [get_clocks {LTC2208_122MHz}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {Angelia_clk_lrclk_gen:clrgen|BCLK}] -fall_to [get_clocks {LTC2208_122MHz}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {Angelia_clk_lrclk_gen:clrgen|BCLK}] -rise_to [get_clocks {spc[2]}]  0.030  
set_clock_uncertainty -fall_from [get_clocks {Angelia_clk_lrclk_gen:clrgen|BCLK}] -fall_to [get_clocks {spc[2]}]  0.030  
set_clock_uncertainty -fall_from [get_clocks {Angelia_clk_lrclk_gen:clrgen|BCLK}] -rise_to [get_clocks {Angelia_clk_lrclk_gen:clrgen|BCLK}]  0.030  
set_clock_uncertainty -fall_from [get_clocks {Angelia_clk_lrclk_gen:clrgen|BCLK}] -fall_to [get_clocks {Angelia_clk_lrclk_gen:clrgen|BCLK}]  0.030  
set_clock_uncertainty -rise_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {_122MHz}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {_122MHz}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {_122MHz}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {_122MHz}]  0.020  


#**************************************************************
# Set Input Delay
#**************************************************************

set_input_delay -add_delay -max -clock [get_clocks {virt_122MHz}]  1.000 [get_ports {ADCMISO}]
set_input_delay -add_delay -min -clock [get_clocks {virt_122MHz}]  -1.000 [get_ports {ADCMISO}]
set_input_delay -add_delay -max -clock [get_clocks {virt_122MHz}]  1.000 [get_ports {ANT_TUNE}]
set_input_delay -add_delay -min -clock [get_clocks {virt_122MHz}]  -1.000 [get_ports {ANT_TUNE}]
set_input_delay -add_delay -max -clock [get_clocks {virt_PHY_RX_CLOCK}]  1.000 [get_ports {ASMI_interface:ASMI_int_inst|ASMI:ASMI_inst|ASMI_altasmi_parallel_cv82:ASMI_altasmi_parallel_cv82_component|cycloneii_asmiblock2~ALTERA_DATA0}]
set_input_delay -add_delay -min -clock [get_clocks {virt_PHY_RX_CLOCK}]  -1.000 [get_ports {ASMI_interface:ASMI_int_inst|ASMI:ASMI_inst|ASMI_altasmi_parallel_cv82:ASMI_altasmi_parallel_cv82_component|cycloneii_asmiblock2~ALTERA_DATA0}]
set_input_delay -add_delay -max -clock [get_clocks {virt_122MHz}]  1.000 [get_ports {CDOUT}]
set_input_delay -add_delay -min -clock [get_clocks {virt_122MHz}]  -1.000 [get_ports {CDOUT}]
set_input_delay -add_delay -max -clock [get_clocks {virt_122MHz}]  1.000 [get_ports {INA[0]}]
set_input_delay -add_delay -min -clock [get_clocks {virt_122MHz}]  -1.000 [get_ports {INA[0]}]
set_input_delay -add_delay -max -clock [get_clocks {virt_122MHz}]  1.000 [get_ports {INA[1]}]
set_input_delay -add_delay -min -clock [get_clocks {virt_122MHz}]  -1.000 [get_ports {INA[1]}]
set_input_delay -add_delay -max -clock [get_clocks {virt_122MHz}]  1.000 [get_ports {INA[2]}]
set_input_delay -add_delay -min -clock [get_clocks {virt_122MHz}]  -1.000 [get_ports {INA[2]}]
set_input_delay -add_delay -max -clock [get_clocks {virt_122MHz}]  1.000 [get_ports {INA[3]}]
set_input_delay -add_delay -min -clock [get_clocks {virt_122MHz}]  -1.000 [get_ports {INA[3]}]
set_input_delay -add_delay -max -clock [get_clocks {virt_122MHz}]  1.000 [get_ports {INA[4]}]
set_input_delay -add_delay -min -clock [get_clocks {virt_122MHz}]  -1.000 [get_ports {INA[4]}]
set_input_delay -add_delay -max -clock [get_clocks {virt_122MHz}]  1.000 [get_ports {INA[5]}]
set_input_delay -add_delay -min -clock [get_clocks {virt_122MHz}]  -1.000 [get_ports {INA[5]}]
set_input_delay -add_delay -max -clock [get_clocks {virt_122MHz}]  1.000 [get_ports {INA[6]}]
set_input_delay -add_delay -min -clock [get_clocks {virt_122MHz}]  -1.000 [get_ports {INA[6]}]
set_input_delay -add_delay -max -clock [get_clocks {virt_122MHz}]  1.000 [get_ports {INA[7]}]
set_input_delay -add_delay -min -clock [get_clocks {virt_122MHz}]  -1.000 [get_ports {INA[7]}]
set_input_delay -add_delay -max -clock [get_clocks {virt_122MHz}]  1.000 [get_ports {INA[8]}]
set_input_delay -add_delay -min -clock [get_clocks {virt_122MHz}]  -1.000 [get_ports {INA[8]}]
set_input_delay -add_delay -max -clock [get_clocks {virt_122MHz}]  1.000 [get_ports {INA[9]}]
set_input_delay -add_delay -min -clock [get_clocks {virt_122MHz}]  -1.000 [get_ports {INA[9]}]
set_input_delay -add_delay -max -clock [get_clocks {virt_122MHz}]  1.000 [get_ports {INA[10]}]
set_input_delay -add_delay -min -clock [get_clocks {virt_122MHz}]  -1.000 [get_ports {INA[10]}]
set_input_delay -add_delay -max -clock [get_clocks {virt_122MHz}]  1.000 [get_ports {INA[11]}]
set_input_delay -add_delay -min -clock [get_clocks {virt_122MHz}]  -1.000 [get_ports {INA[11]}]
set_input_delay -add_delay -max -clock [get_clocks {virt_122MHz}]  1.000 [get_ports {INA[12]}]
set_input_delay -add_delay -min -clock [get_clocks {virt_122MHz}]  -1.000 [get_ports {INA[12]}]
set_input_delay -add_delay -max -clock [get_clocks {virt_122MHz}]  1.000 [get_ports {INA[13]}]
set_input_delay -add_delay -min -clock [get_clocks {virt_122MHz}]  -1.000 [get_ports {INA[13]}]
set_input_delay -add_delay -max -clock [get_clocks {virt_122MHz}]  1.000 [get_ports {INA[14]}]
set_input_delay -add_delay -min -clock [get_clocks {virt_122MHz}]  -1.000 [get_ports {INA[14]}]
set_input_delay -add_delay -max -clock [get_clocks {virt_122MHz}]  1.000 [get_ports {INA[15]}]
set_input_delay -add_delay -min -clock [get_clocks {virt_122MHz}]  -1.000 [get_ports {INA[15]}]
set_input_delay -add_delay -max -clock [get_clocks {virt_122MHz}]  1.000 [get_ports {INA_2[0]}]
set_input_delay -add_delay -min -clock [get_clocks {virt_122MHz}]  -1.000 [get_ports {INA_2[0]}]
set_input_delay -add_delay -max -clock [get_clocks {virt_122MHz}]  1.000 [get_ports {INA_2[1]}]
set_input_delay -add_delay -min -clock [get_clocks {virt_122MHz}]  -1.000 [get_ports {INA_2[1]}]
set_input_delay -add_delay -max -clock [get_clocks {virt_122MHz}]  1.000 [get_ports {INA_2[2]}]
set_input_delay -add_delay -min -clock [get_clocks {virt_122MHz}]  -1.000 [get_ports {INA_2[2]}]
set_input_delay -add_delay -max -clock [get_clocks {virt_122MHz}]  1.000 [get_ports {INA_2[3]}]
set_input_delay -add_delay -min -clock [get_clocks {virt_122MHz}]  -1.000 [get_ports {INA_2[3]}]
set_input_delay -add_delay -max -clock [get_clocks {virt_122MHz}]  1.000 [get_ports {INA_2[4]}]
set_input_delay -add_delay -min -clock [get_clocks {virt_122MHz}]  -1.000 [get_ports {INA_2[4]}]
set_input_delay -add_delay -max -clock [get_clocks {virt_122MHz}]  1.000 [get_ports {INA_2[5]}]
set_input_delay -add_delay -min -clock [get_clocks {virt_122MHz}]  -1.000 [get_ports {INA_2[5]}]
set_input_delay -add_delay -max -clock [get_clocks {virt_122MHz}]  1.000 [get_ports {INA_2[6]}]
set_input_delay -add_delay -min -clock [get_clocks {virt_122MHz}]  -1.000 [get_ports {INA_2[6]}]
set_input_delay -add_delay -max -clock [get_clocks {virt_122MHz}]  1.000 [get_ports {INA_2[7]}]
set_input_delay -add_delay -min -clock [get_clocks {virt_122MHz}]  -1.000 [get_ports {INA_2[7]}]
set_input_delay -add_delay -max -clock [get_clocks {virt_122MHz}]  1.000 [get_ports {INA_2[8]}]
set_input_delay -add_delay -min -clock [get_clocks {virt_122MHz}]  -1.000 [get_ports {INA_2[8]}]
set_input_delay -add_delay -max -clock [get_clocks {virt_122MHz}]  1.000 [get_ports {INA_2[9]}]
set_input_delay -add_delay -min -clock [get_clocks {virt_122MHz}]  -1.000 [get_ports {INA_2[9]}]
set_input_delay -add_delay -max -clock [get_clocks {virt_122MHz}]  1.000 [get_ports {INA_2[10]}]
set_input_delay -add_delay -min -clock [get_clocks {virt_122MHz}]  -1.000 [get_ports {INA_2[10]}]
set_input_delay -add_delay -max -clock [get_clocks {virt_122MHz}]  1.000 [get_ports {INA_2[11]}]
set_input_delay -add_delay -min -clock [get_clocks {virt_122MHz}]  -1.000 [get_ports {INA_2[11]}]
set_input_delay -add_delay -max -clock [get_clocks {virt_122MHz}]  1.000 [get_ports {INA_2[12]}]
set_input_delay -add_delay -min -clock [get_clocks {virt_122MHz}]  -1.000 [get_ports {INA_2[12]}]
set_input_delay -add_delay -max -clock [get_clocks {virt_122MHz}]  1.000 [get_ports {INA_2[13]}]
set_input_delay -add_delay -min -clock [get_clocks {virt_122MHz}]  -1.000 [get_ports {INA_2[13]}]
set_input_delay -add_delay -max -clock [get_clocks {virt_122MHz}]  1.000 [get_ports {INA_2[14]}]
set_input_delay -add_delay -min -clock [get_clocks {virt_122MHz}]  -1.000 [get_ports {INA_2[14]}]
set_input_delay -add_delay -max -clock [get_clocks {virt_122MHz}]  1.000 [get_ports {INA_2[15]}]
set_input_delay -add_delay -min -clock [get_clocks {virt_122MHz}]  -1.000 [get_ports {INA_2[15]}]
set_input_delay -add_delay -max -clock [get_clocks {virt_122MHz}]  1.000 [get_ports {IO4}]
set_input_delay -add_delay -min -clock [get_clocks {virt_122MHz}]  -1.000 [get_ports {IO4}]
set_input_delay -add_delay -max -clock [get_clocks {virt_122MHz}]  1.000 [get_ports {IO5}]
set_input_delay -add_delay -min -clock [get_clocks {virt_122MHz}]  -1.000 [get_ports {IO5}]
set_input_delay -add_delay -max -clock [get_clocks {virt_122MHz}]  1.000 [get_ports {IO6}]
set_input_delay -add_delay -min -clock [get_clocks {virt_122MHz}]  -1.000 [get_ports {IO6}]
set_input_delay -add_delay -max -clock [get_clocks {virt_122MHz}]  1.000 [get_ports {IO8}]
set_input_delay -add_delay -min -clock [get_clocks {virt_122MHz}]  -1.000 [get_ports {IO8}]
set_input_delay -add_delay -max -clock [get_clocks {virt_122MHz}]  1.000 [get_ports {KEY_DASH}]
set_input_delay -add_delay -min -clock [get_clocks {virt_122MHz}]  -1.000 [get_ports {KEY_DASH}]
set_input_delay -add_delay -max -clock [get_clocks {virt_122MHz}]  1.000 [get_ports {KEY_DOT}]
set_input_delay -add_delay -min -clock [get_clocks {virt_122MHz}]  -1.000 [get_ports {KEY_DOT}]
set_input_delay -add_delay -max -clock [get_clocks {virt_122MHz}]  1.000 [get_ports {MODE2}]
set_input_delay -add_delay -min -clock [get_clocks {virt_122MHz}]  -1.000 [get_ports {MODE2}]
set_input_delay -add_delay -max -clock [get_clocks {virt_122MHz}]  1.000 [get_ports {OVERFLOW}]
set_input_delay -add_delay -min -clock [get_clocks {virt_122MHz}]  -1.000 [get_ports {OVERFLOW}]
set_input_delay -add_delay -max -clock [get_clocks {virt_122MHz}]  1.000 [get_ports {OVERFLOW_2}]
set_input_delay -add_delay -min -clock [get_clocks {virt_122MHz}]  -1.000 [get_ports {OVERFLOW_2}]
set_input_delay -add_delay -max -clock [get_clocks {virt_PHY_RX_CLOCK}]  1.000 [get_ports {PHY_CLK125}]
set_input_delay -add_delay -min -clock [get_clocks {virt_PHY_RX_CLOCK}]  -1.000 [get_ports {PHY_CLK125}]
set_input_delay -add_delay -max -clock [get_clocks {virt_PHY_RX_CLOCK}]  1.000 [get_ports {PHY_INT_N}]
set_input_delay -add_delay -min -clock [get_clocks {virt_PHY_RX_CLOCK}]  -1.000 [get_ports {PHY_INT_N}]
set_input_delay -add_delay -max -clock [get_clocks {virt_PHY_RX_CLOCK}]  1.000 [get_ports {PHY_MDIO}]
set_input_delay -add_delay -min -clock [get_clocks {virt_PHY_RX_CLOCK}]  -1.000 [get_ports {PHY_MDIO}]
set_input_delay -add_delay -max -clock [get_clocks {virt_PHY_RX_CLOCK}]  1.000 [get_ports {PHY_RX[0]}]
set_input_delay -add_delay -min -clock [get_clocks {virt_PHY_RX_CLOCK}]  -1.000 [get_ports {PHY_RX[0]}]
set_input_delay -add_delay -max -clock [get_clocks {virt_PHY_RX_CLOCK}]  1.000 [get_ports {PHY_RX[1]}]
set_input_delay -add_delay -min -clock [get_clocks {virt_PHY_RX_CLOCK}]  -1.000 [get_ports {PHY_RX[1]}]
set_input_delay -add_delay -max -clock [get_clocks {virt_PHY_RX_CLOCK}]  1.000 [get_ports {PHY_RX[2]}]
set_input_delay -add_delay -min -clock [get_clocks {virt_PHY_RX_CLOCK}]  -1.000 [get_ports {PHY_RX[2]}]
set_input_delay -add_delay -max -clock [get_clocks {virt_PHY_RX_CLOCK}]  1.000 [get_ports {PHY_RX[3]}]
set_input_delay -add_delay -min -clock [get_clocks {virt_PHY_RX_CLOCK}]  -1.000 [get_ports {PHY_RX[3]}]
set_input_delay -add_delay -max -clock [get_clocks {virt_122MHz}]  1.000 [get_ports {PTT}]
set_input_delay -add_delay -min -clock [get_clocks {virt_122MHz}]  -1.000 [get_ports {PTT}]
set_input_delay -add_delay -max -clock [get_clocks {virt_PHY_RX_CLOCK}]  1.000 [get_ports {RX_DV}]
set_input_delay -add_delay -min -clock [get_clocks {virt_PHY_RX_CLOCK}]  -1.000 [get_ports {RX_DV}]
set_input_delay -add_delay -max -clock [get_clocks {virt_122MHz}]  1.000 [get_ports {SO}]
set_input_delay -add_delay -min -clock [get_clocks {virt_122MHz}]  -1.000 [get_ports {SO}]
set_input_delay -add_delay -max -clock [get_clocks {virt_122MHz}]  1.000 [get_ports {SPI_SDI}]
set_input_delay -add_delay -min -clock [get_clocks {virt_122MHz}]  -1.000 [get_ports {SPI_SDI}]


#**************************************************************
# Set Output Delay
#**************************************************************

set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {ADCCLK}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {ADCCLK}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {ADCMOSI}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {ADCMOSI}]
set_output_delay -add_delay -max -clock [get_clocks {PHY_CLK125}]  1.000 [get_ports {ASMI_interface:ASMI_int_inst|ASMI:ASMI_inst|ASMI_altasmi_parallel_cv82:ASMI_altasmi_parallel_cv82_component|cycloneii_asmiblock2~ALTERA_DCLK}]
set_output_delay -add_delay -min -clock [get_clocks {PHY_CLK125}]  -1.000 [get_ports {ASMI_interface:ASMI_int_inst|ASMI:ASMI_inst|ASMI_altasmi_parallel_cv82:ASMI_altasmi_parallel_cv82_component|cycloneii_asmiblock2~ALTERA_DCLK}]
set_output_delay -add_delay -max -clock [get_clocks {PHY_CLK125}]  1.000 [get_ports {ASMI_interface:ASMI_int_inst|ASMI:ASMI_inst|ASMI_altasmi_parallel_cv82:ASMI_altasmi_parallel_cv82_component|cycloneii_asmiblock2~ALTERA_SCE}]
set_output_delay -add_delay -min -clock [get_clocks {PHY_CLK125}]  -1.000 [get_ports {ASMI_interface:ASMI_int_inst|ASMI:ASMI_inst|ASMI_altasmi_parallel_cv82:ASMI_altasmi_parallel_cv82_component|cycloneii_asmiblock2~ALTERA_SCE}]
set_output_delay -add_delay -max -clock [get_clocks {PHY_CLK125}]  1.000 [get_ports {ASMI_interface:ASMI_int_inst|ASMI:ASMI_inst|ASMI_altasmi_parallel_cv82:ASMI_altasmi_parallel_cv82_component|cycloneii_asmiblock2~ALTERA_SDO}]
set_output_delay -add_delay -min -clock [get_clocks {PHY_CLK125}]  -1.000 [get_ports {ASMI_interface:ASMI_int_inst|ASMI:ASMI_inst|ASMI_altasmi_parallel_cv82:ASMI_altasmi_parallel_cv82_component|cycloneii_asmiblock2~ALTERA_SDO}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {ATTN_CLK}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {ATTN_CLK}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {ATTN_CLK_2}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {ATTN_CLK_2}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {ATTN_DATA}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {ATTN_DATA}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {ATTN_DATA_2}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {ATTN_DATA_2}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {ATTN_LE}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {ATTN_LE}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {ATTN_LE_2}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {ATTN_LE_2}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {CBCLK}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {CBCLK}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {CDIN}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {CDIN}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {CLRCIN}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {CLRCIN}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {CLRCOUT}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {CLRCOUT}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {CMCLK}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {CMCLK}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {CS}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {CS}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {DACD[0]}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {DACD[0]}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {DACD[1]}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {DACD[1]}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {DACD[2]}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {DACD[2]}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {DACD[3]}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {DACD[3]}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {DACD[4]}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {DACD[4]}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {DACD[5]}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {DACD[5]}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {DACD[6]}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {DACD[6]}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {DACD[7]}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {DACD[7]}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {DACD[8]}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {DACD[8]}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {DACD[9]}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {DACD[9]}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {DACD[10]}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {DACD[10]}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {DACD[11]}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {DACD[11]}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {DACD[12]}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {DACD[12]}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {DACD[13]}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {DACD[13]}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {DAC_ALC}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {DAC_ALC}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {DEBUG_LED1}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {DEBUG_LED1}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {DEBUG_LED2}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {DEBUG_LED2}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {DEBUG_LED3}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {DEBUG_LED3}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {DEBUG_LED4}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {DEBUG_LED4}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {DEBUG_LED5}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {DEBUG_LED5}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {DEBUG_LED6}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {DEBUG_LED6}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {DEBUG_LED7}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {DEBUG_LED7}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {DEBUG_LED8}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {DEBUG_LED8}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {DEBUG_LED9}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {DEBUG_LED9}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {DEBUG_LED10}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {DEBUG_LED10}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {DITH}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {DITH}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {DITH_2}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {DITH_2}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {FPGA_PLL}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {FPGA_PLL}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {FPGA_PTT}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {FPGA_PTT}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {J15_5}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {J15_5}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {J15_6}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {J15_6}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {MOSI}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {MOSI}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {NCONFIG}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {NCONFIG}]
set_output_delay -add_delay -max -clock [get_clocks {PHY_CLK125}]  1.000 [get_ports {PHY_MDC}]
set_output_delay -add_delay -min -clock [get_clocks {PHY_CLK125}]  -1.000 [get_ports {PHY_MDC}]
set_output_delay -add_delay -max -clock [get_clocks {PHY_CLK125}]  1.000 [get_ports {PHY_MDIO}]
set_output_delay -add_delay -min -clock [get_clocks {PHY_CLK125}]  -1.000 [get_ports {PHY_MDIO}]
set_output_delay -add_delay -max -clock [get_clocks {PHY_CLK125}]  1.000 [get_ports {PHY_TX[0]}]
set_output_delay -add_delay -min -clock [get_clocks {PHY_CLK125}]  -1.000 [get_ports {PHY_TX[0]}]
set_output_delay -add_delay -max -clock [get_clocks {PHY_CLK125}]  1.000 [get_ports {PHY_TX[1]}]
set_output_delay -add_delay -min -clock [get_clocks {PHY_CLK125}]  -1.000 [get_ports {PHY_TX[1]}]
set_output_delay -add_delay -max -clock [get_clocks {PHY_CLK125}]  1.000 [get_ports {PHY_TX[2]}]
set_output_delay -add_delay -min -clock [get_clocks {PHY_CLK125}]  -1.000 [get_ports {PHY_TX[2]}]
set_output_delay -add_delay -max -clock [get_clocks {PHY_CLK125}]  1.000 [get_ports {PHY_TX[3]}]
set_output_delay -add_delay -min -clock [get_clocks {PHY_CLK125}]  -1.000 [get_ports {PHY_TX[3]}]
set_output_delay -add_delay -max -clock [get_clocks {PHY_CLK125}]  1.000 [get_ports {PHY_TX_CLOCK}]
set_output_delay -add_delay -min -clock [get_clocks {PHY_CLK125}]  -1.000 [get_ports {PHY_TX_CLOCK}]
set_output_delay -add_delay -max -clock [get_clocks {PHY_CLK125}]  1.000 [get_ports {PHY_TX_EN}]
set_output_delay -add_delay -min -clock [get_clocks {PHY_CLK125}]  -1.000 [get_ports {PHY_TX_EN}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {RAND}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {RAND}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {RAND_2}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {RAND_2}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {SCK}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {SCK}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {SI}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {SI}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {SPI_SCK}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {SPI_SCK}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {SPI_SDO}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {SPI_SDO}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {SSCK}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {SSCK}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {Status_LED}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {Status_LED}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {USEROUT0}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {USEROUT0}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {USEROUT1}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {USEROUT1}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {USEROUT2}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {USEROUT2}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {USEROUT3}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {USEROUT3}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {USEROUT4}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {USEROUT4}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {USEROUT5}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {USEROUT5}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {USEROUT6}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {USEROUT6}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {nADCCS}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {nADCCS}]
set_output_delay -add_delay -max -clock [get_clocks {_122MHz}]  1.000 [get_ports {nCS}]
set_output_delay -add_delay -min -clock [get_clocks {_122MHz}]  -1.000 [get_ports {nCS}]


#**************************************************************
# Set Clock Groups
#**************************************************************

set_clock_groups -asynchronous -group [get_clocks {  LTC2208_122MHz  LTC2208_122MHz_2  _122MHz  PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]  PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]  PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]  Angelia_clk_lrclk_gen:clrgen|BCLK  spc[2]  Attenuator:Attenuator_ADC1|clk_2  Attenuator:Attenuator_ADC2|clk_2  }] -group [get_clocks {  PHY_RX_CLOCK  PHY_RX_CLOCK_2  PHY_CLK125  }] -group [get_clocks {OSC_10MHZ  PLL2_inst|altpll_component|auto_generated|pll1|clk[0]  }] -group [get_clocks {EXT_OSC_10MHZ  PLL3_inst|altpll_component|auto_generated|pll1|clk[0]}] 


#**************************************************************
# Set False Path
#**************************************************************

set_false_path -from [get_keepers {*rdptr_g*}] -to [get_keepers {*ws_dgrp|dffpipe_re9:dffpipe16|dffe17a*}]
set_false_path -from [get_keepers {*delayed_wrptr_g*}] -to [get_keepers {*rs_dgwp|dffpipe_qe9:dffpipe13|dffe14a*}]
set_false_path -from [get_keepers {*rdptr_g*}] -to [get_keepers {*ws_dgrp|dffpipe_1f9:dffpipe17|dffe18a*}]
set_false_path -from [get_keepers {*delayed_wrptr_g*}] -to [get_keepers {*rs_dgwp|dffpipe_0f9:dffpipe14|dffe15a*}]
set_false_path -from [get_keepers {*rdptr_g*}] -to [get_keepers {*ws_dgrp|dffpipe_4f9:dffpipe13|dffe14a*}]
set_false_path -from [get_keepers {*delayed_wrptr_g*}] -to [get_keepers {*rs_dgwp|dffpipe_3f9:dffpipe10|dffe11a*}]
set_false_path -from [get_keepers {*rdptr_g*}] -to [get_keepers {*ws_dgrp|dffpipe_fd9:dffpipe15|dffe16a*}]
set_false_path -from [get_keepers {*delayed_wrptr_g*}] -to [get_keepers {*rs_dgwp|dffpipe_ed9:dffpipe12|dffe13a*}]
set_false_path -from [get_keepers {*rdptr_g*}] -to [get_keepers {*ws_dgrp|dffpipe_ve9:dffpipe19|dffe20a*}]
set_false_path -from [get_keepers {*delayed_wrptr_g*}] -to [get_keepers {*rs_dgwp|dffpipe_te9:dffpipe15|dffe16a*}]


#**************************************************************
# Set Multicycle Path
#**************************************************************

set_multicycle_path -setup -end -from  [get_clocks {spc[2]}]  -to  [get_clocks {_122MHz}] 4
set_multicycle_path -hold -end -from  [get_clocks {spc[2]}]  -to  [get_clocks {_122MHz}] 3
set_multicycle_path -setup -end -from  [get_clocks {Attenuator:Attenuator_ADC1|clk_2}]  -to  [get_clocks {_122MHz}] 4
set_multicycle_path -hold -end -from  [get_clocks {Attenuator:Attenuator_ADC1|clk_2}]  -to  [get_clocks {_122MHz}] 3
set_multicycle_path -setup -end -from  [get_clocks {Attenuator:Attenuator_ADC2|clk_2}]  -to  [get_clocks {_122MHz}] 4
set_multicycle_path -hold -end -from  [get_clocks {Attenuator:Attenuator_ADC2|clk_2}]  -to  [get_clocks {_122MHz}] 3


#**************************************************************
# Set Maximum Delay
#**************************************************************

set_max_delay -from  [get_clocks {_122MHz}]  -to  [get_clocks {_122MHz}] 20.000
set_max_delay -from  [get_clocks {Angelia_clk_lrclk_gen:clrgen|BCLK}]  -to  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] 17.000
set_max_delay -from  [get_clocks {Angelia_clk_lrclk_gen:clrgen|BCLK}]  -to  [get_clocks {_122MHz}] 15.000
set_max_delay -from  [get_clocks {Attenuator:Attenuator_ADC1|clk_2}]  -to  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] 4.000
set_max_delay -from  [get_clocks {Attenuator:Attenuator_ADC2|clk_2}]  -to  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] 4.000
set_max_delay -from  [get_clocks {LTC2208_122MHz}]  -to  [get_clocks {LTC2208_122MHz}] 15.000
set_max_delay -from  [get_clocks {LTC2208_122MHz}]  -to  [get_clocks {spc[2]}] 21.000
set_max_delay -from  [get_clocks {LTC2208_122MHz}]  -to  [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] 21.000
set_max_delay -from  [get_clocks {LTC2208_122MHz}]  -to  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] 8.000
set_max_delay -from  [get_clocks {LTC2208_122MHz_2}]  -to  [get_clocks {LTC2208_122MHz}] 15.000
set_max_delay -from  [get_clocks {LTC2208_122MHz_2}]  -to  [get_clocks {LTC2208_122MHz_2}] 11.000
set_max_delay -from  [get_clocks {LTC2208_122MHz_2}]  -to  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] 7.000
set_max_delay -from  [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}]  -to  [get_clocks {PHY_CLK125}] 25.000
set_max_delay -from  [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}]  -to  [get_clocks {_122MHz}] 31.000
set_max_delay -from  [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}]  -to  [get_clocks {_122MHz}] 26.000
set_max_delay -from  [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}]  -to  [get_clocks {PHY_CLK125}] 21.000
set_max_delay -from  [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}]  -to  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] 14.000
set_max_delay -from  [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}]  -to  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] 20.000
set_max_delay -from  [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}]  -to  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] 30.000
set_max_delay -from  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}]  -to  [get_clocks {Attenuator:Attenuator_ADC1|clk_2}] 9.000
set_max_delay -from  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}]  -to  [get_clocks {Attenuator:Attenuator_ADC2|clk_2}] 3.000
set_max_delay -from  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}]  -to  [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] 18.000
set_max_delay -from  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}]  -to  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}] 6.000
set_max_delay -from  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}]  -to  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] 18.000
set_max_delay -from  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}]  -to  [get_clocks {_122MHz}] 17.000
set_max_delay -from  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}]  -to  [get_clocks {LTC2208_122MHz}] 5.000
set_max_delay -from  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}]  -to  [get_clocks {LTC2208_122MHz_2}] 5.000
set_max_delay -from  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}]  -to  [get_clocks {spc[2]}] -2.000
set_max_delay -from  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}]  -to  [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] 19.000
set_max_delay -from  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}]  -to  [get_clocks {_122MHz}] 15.000
set_max_delay -from  [get_clocks {virt_122MHz}]  -to  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] 15.000
set_max_delay -from  [get_clocks {virt_122MHz}]  -to  [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] 4.000
set_max_delay -from  [get_clocks {virt_PHY_RX_CLOCK}]  -to  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] 14.000


#**************************************************************
# Set Minimum Delay
#**************************************************************

set_min_delay -from  [get_clocks {_122MHz}]  -to  [get_clocks {_122MHz}] -2.000
set_min_delay -from  [get_clocks {Angelia_clk_lrclk_gen:clrgen|BCLK}]  -to  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] 4.000
set_min_delay -from  [get_clocks {Angelia_clk_lrclk_gen:clrgen|BCLK}]  -to  [get_clocks {_122MHz}] 4.000
set_min_delay -from  [get_clocks {Attenuator:Attenuator_ADC1|clk_2}]  -to  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -2.000
set_min_delay -from  [get_clocks {Attenuator:Attenuator_ADC2|clk_2}]  -to  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -2.000
set_min_delay -from  [get_clocks {LTC2208_122MHz}]  -to  [get_clocks {LTC2208_122MHz}] -2.000
set_min_delay -from  [get_clocks {LTC2208_122MHz}]  -to  [get_clocks {spc[2]}] -1.000
set_min_delay -from  [get_clocks {LTC2208_122MHz}]  -to  [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] 11.000
set_min_delay -from  [get_clocks {LTC2208_122MHz}]  -to  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] 1.000
set_min_delay -from  [get_clocks {LTC2208_122MHz_2}]  -to  [get_clocks {LTC2208_122MHz}] -2.000
set_min_delay -from  [get_clocks {LTC2208_122MHz_2}]  -to  [get_clocks {LTC2208_122MHz_2}] -2.000
set_min_delay -from  [get_clocks {LTC2208_122MHz_2}]  -to  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] 1.000
set_min_delay -from  [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}]  -to  [get_clocks {PHY_CLK125}] 8.000
set_min_delay -from  [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}]  -to  [get_clocks {_122MHz}] 10.000
set_min_delay -from  [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}]  -to  [get_clocks {PHY_CLK125}] 5.000
set_min_delay -from  [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}]  -to  [get_clocks {_122MHz}] 10.000
set_min_delay -from  [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}]  -to  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] 4.000
set_min_delay -from  [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}]  -to  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] 3.000
set_min_delay -from  [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}]  -to  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] 15.000
set_min_delay -from  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}]  -to  [get_clocks {Attenuator:Attenuator_ADC1|clk_2}] 1.000
set_min_delay -from  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}]  -to  [get_clocks {Attenuator:Attenuator_ADC2|clk_2}] -4.000
set_min_delay -from  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}]  -to  [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] 4.000
set_min_delay -from  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}]  -to  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}] -2.000
set_min_delay -from  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}]  -to  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] -1.000
set_min_delay -from  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}]  -to  [get_clocks {_122MHz}] 0.000
set_min_delay -from  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}]  -to  [get_clocks {LTC2208_122MHz}] -4.000
set_min_delay -from  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}]  -to  [get_clocks {LTC2208_122MHz_2}] -4.000
set_min_delay -from  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}]  -to  [get_clocks {spc[2]}] -12.000
set_min_delay -from  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}]  -to  [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] 0.000
set_min_delay -from  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}]  -to  [get_clocks {_122MHz}] 0.000
set_min_delay -from  [get_clocks {virt_122MHz}]  -to  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] 1.000
set_min_delay -from  [get_clocks {virt_122MHz}]  -to  [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -6.000
set_min_delay -from  [get_clocks {virt_PHY_RX_CLOCK}]  -to  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -2.000


#**************************************************************
# Set Input Transition
#**************************************************************

