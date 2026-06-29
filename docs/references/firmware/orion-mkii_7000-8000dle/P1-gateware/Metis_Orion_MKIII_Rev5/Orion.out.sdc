## Generated SDC file "Orion.out.sdc"

## Copyright (C) 2021  Intel Corporation. All rights reserved.
## Your use of Intel Corporation's design tools, logic functions 
## and other software and tools, and any partner logic 
## functions, and any output files from any of the foregoing 
## (including device programming or simulation files), and any 
## associated documentation or information are expressly subject 
## to the terms and conditions of the Intel Program License 
## Subscription Agreement, the Intel Quartus Prime License Agreement,
## the Intel FPGA IP License Agreement, or other applicable license
## agreement, including, without limitation, that your use is for
## the sole purpose of programming logic devices manufactured by
## Intel and sold by Intel or its authorized distributors.  Please
## refer to the applicable agreement for further details, at
## https://fpgasoftware.intel.com/eula.


## VENDOR  "Altera"
## PROGRAM "Quartus Prime"
## VERSION "Version 21.1.0 Build 842 10/21/2021 SJ Lite Edition"

## DATE    "Fri Mar 11 08:34:14 2022"

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

create_clock -name {LTC2208_122MHz} -period 8.138 -waveform { 0.000 4.069 } [get_ports {LTC2208_122MHz}]
create_clock -name {LTC2208_122MHz_2} -period 8.138 -waveform { 0.000 4.069 } [get_ports {LTC2208_122MHz_2}]
create_clock -name {_122MHz} -period 8.138 -waveform { 0.000 4.069 } [get_ports {_122MHz}]
create_clock -name {EXT_OSC_10MHZ} -period 100.000 -waveform { 0.000 50.000 } [get_ports {EXT_OSC_10MHZ}]
create_clock -name {OSC_10MHZ} -period 100.000 -waveform { 0.000 50.000 } [get_ports {OSC_10MHZ}]
create_clock -name {PHY_CLK125} -period 8.000 -waveform { 0.000 4.000 } [get_ports {PHY_CLK125}]
create_clock -name {PHY_RX_CLOCK} -period 40.000 -waveform { 0.000 20.000 } [get_ports {PHY_RX_CLOCK}]
create_clock -name {virt_PHY_RX_CLOCK} -period 8.000 -waveform { 0.000 4.000 } 
create_clock -name {virt_122MHz} -period 8.138 -waveform { 0.000 4.069 } 
create_clock -name {virt_122MHz_2} -period 8.138 -waveform { 0.000 4.069 } 
create_clock -name {virt_CBCLK} -period 325.520 -waveform { 0.000 162.760 } 


#**************************************************************
# Create Generated Clock
#**************************************************************

create_generated_clock -name {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]} -source [get_pins {PLL_clocks_inst|altpll_component|auto_generated|pll1|inclk[0]}] -duty_cycle 50/1 -multiply_by 1 -divide_by 50 -master_clock {PHY_CLK125} [get_pins {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] 
create_generated_clock -name {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]} -source [get_pins {PLL_clocks_inst|altpll_component|auto_generated|pll1|inclk[0]}] -duty_cycle 50/1 -multiply_by 1 -divide_by 5 -master_clock {PHY_CLK125} [get_pins {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] 
create_generated_clock -name {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]} -source [get_pins {PLL_clocks_inst|altpll_component|auto_generated|pll1|inclk[0]}] -duty_cycle 50/1 -multiply_by 1 -divide_by 10 -master_clock {PHY_CLK125} [get_pins {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] 
create_generated_clock -name {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]} -source [get_pins {PLL_IF_inst|altpll_component|auto_generated|pll1|inclk[0]}] -duty_cycle 50/1 -multiply_by 43 -divide_by 110 -master_clock {LTC2208_122MHz} [get_pins {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] 
create_generated_clock -name {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]} -source [get_pins {PLL_IF_inst|altpll_component|auto_generated|pll1|inclk[0]}] -duty_cycle 50/1 -multiply_by 1 -divide_by 10 -master_clock {LTC2208_122MHz} [get_pins {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}] 
create_generated_clock -name {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]} -source [get_pins {PLL_IF_inst|altpll_component|auto_generated|pll1|inclk[0]}] -duty_cycle 50/1 -multiply_by 1 -divide_by 40 -phase 90.000 -master_clock {LTC2208_122MHz} [get_pins {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] 
create_generated_clock -name {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[3]} -source [get_pins {PLL_IF_inst|altpll_component|auto_generated|pll1|inclk[0]}] -duty_cycle 50/1 -multiply_by 1 -divide_by 2560 -master_clock {LTC2208_122MHz} [get_pins {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[3]}] 
create_generated_clock -name {PLL_inst|altpll_component|auto_generated|pll1|clk[0]} -source [get_pins {PLL_inst|altpll_component|auto_generated|pll1|inclk[0]}] -duty_cycle 50/1 -multiply_by 125 -divide_by 1536 -master_clock {_122MHz} [get_pins {PLL_inst|altpll_component|auto_generated|pll1|clk[0]}] 
create_generated_clock -name {PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[0]} -source [get_pins {PLL_30MHz_inst|altpll_component|auto_generated|pll1|inclk[0]}] -duty_cycle 50/1 -multiply_by 1 -phase 11.250 -master_clock {_122MHz} [get_pins {PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[0]}] 
create_generated_clock -name {PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[1]} -source [get_pins {PLL_30MHz_inst|altpll_component|auto_generated|pll1|inclk[0]}] -duty_cycle 50/1 -multiply_by 1 -divide_by 4 -master_clock {_122MHz} [get_pins {PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[1]}] 
create_generated_clock -name {PHY_RX_CLOCK_2} -source [get_ports {PHY_RX_CLOCK}] -divide_by 2 -master_clock {PHY_RX_CLOCK} [get_registers {PHY_RX_CLOCK_2}] 
create_generated_clock -name {CBCLK} -source [get_pins {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] -master_clock {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]} [get_ports {CBCLK}] 
create_generated_clock -name {CMCLK} -source [get_pins {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}] -master_clock {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]} [get_ports {CMCLK}] 
create_generated_clock -name {CLRCIN} -source [get_pins {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[3]}] -master_clock {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[3]} [get_ports {CLRCIN}] 
create_generated_clock -name {CLRCOUT} -source [get_pins {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[3]}] -master_clock {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[3]} [get_ports {CLRCOUT}] 


#**************************************************************
# Set Clock Latency
#**************************************************************



#**************************************************************
# Set Clock Uncertainty
#**************************************************************

set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK_2}] -rise_to [get_clocks {PHY_RX_CLOCK_2}]  0.030  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK_2}] -fall_to [get_clocks {PHY_RX_CLOCK_2}]  0.030  
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
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK_2}] -rise_to [get_clocks {PHY_RX_CLOCK_2}]  0.030  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK_2}] -fall_to [get_clocks {PHY_RX_CLOCK_2}]  0.030  
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
set_clock_uncertainty -rise_from [get_clocks {PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.160  
set_clock_uncertainty -rise_from [get_clocks {PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.160  
set_clock_uncertainty -rise_from [get_clocks {PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.160  
set_clock_uncertainty -rise_from [get_clocks {PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.160  
set_clock_uncertainty -fall_from [get_clocks {PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.160  
set_clock_uncertainty -fall_from [get_clocks {PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.160  
set_clock_uncertainty -fall_from [get_clocks {PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.160  
set_clock_uncertainty -fall_from [get_clocks {PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.160  
set_clock_uncertainty -rise_from [get_clocks {PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {_122MHz}] -setup 0.090  
set_clock_uncertainty -rise_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {_122MHz}] -hold 0.060  
set_clock_uncertainty -rise_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {_122MHz}] -setup 0.090  
set_clock_uncertainty -rise_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {_122MHz}] -hold 0.060  
set_clock_uncertainty -fall_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {_122MHz}] -setup 0.090  
set_clock_uncertainty -fall_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {_122MHz}] -hold 0.060  
set_clock_uncertainty -fall_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {_122MHz}] -setup 0.090  
set_clock_uncertainty -fall_from [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {_122MHz}] -hold 0.060  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[3]}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[3]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[3]}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[3]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[3]}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[3]}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[3]}] -rise_to [get_clocks {_122MHz}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[3]}] -rise_to [get_clocks {_122MHz}] -hold 0.080  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[3]}] -fall_to [get_clocks {_122MHz}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[3]}] -fall_to [get_clocks {_122MHz}] -hold 0.080  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[3]}] -rise_to [get_clocks {LTC2208_122MHz}] -setup 0.100  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[3]}] -rise_to [get_clocks {LTC2208_122MHz}] -hold 0.070  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[3]}] -fall_to [get_clocks {LTC2208_122MHz}] -setup 0.100  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[3]}] -fall_to [get_clocks {LTC2208_122MHz}] -hold 0.070  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[3]}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[3]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[3]}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[3]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[3]}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[3]}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[3]}] -rise_to [get_clocks {_122MHz}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[3]}] -rise_to [get_clocks {_122MHz}] -hold 0.080  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[3]}] -fall_to [get_clocks {_122MHz}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[3]}] -fall_to [get_clocks {_122MHz}] -hold 0.080  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[3]}] -rise_to [get_clocks {LTC2208_122MHz}] -setup 0.100  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[3]}] -rise_to [get_clocks {LTC2208_122MHz}] -hold 0.070  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[3]}] -fall_to [get_clocks {LTC2208_122MHz}] -setup 0.100  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[3]}] -fall_to [get_clocks {LTC2208_122MHz}] -hold 0.070  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {LTC2208_122MHz}] -setup 0.100  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {LTC2208_122MHz}] -hold 0.070  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {LTC2208_122MHz}] -setup 0.100  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {LTC2208_122MHz}] -hold 0.070  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {LTC2208_122MHz}] -setup 0.100  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {LTC2208_122MHz}] -hold 0.070  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {LTC2208_122MHz}] -setup 0.100  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {LTC2208_122MHz}] -hold 0.070  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {LTC2208_122MHz}] -setup 0.100  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {LTC2208_122MHz}] -hold 0.070  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {LTC2208_122MHz}] -setup 0.100  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {LTC2208_122MHz}] -hold 0.070  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {LTC2208_122MHz}] -setup 0.100  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {LTC2208_122MHz}] -hold 0.070  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {LTC2208_122MHz}] -setup 0.100  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {LTC2208_122MHz}] -hold 0.070  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.080  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.080  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.080  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.080  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.080  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.080  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PHY_CLK125}] -setup 0.100  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PHY_CLK125}] -hold 0.070  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PHY_CLK125}] -setup 0.100  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PHY_CLK125}] -hold 0.070  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.080  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.080  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PHY_CLK125}] -setup 0.100  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -rise_to [get_clocks {PHY_CLK125}] -hold 0.070  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PHY_CLK125}] -setup 0.100  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] -fall_to [get_clocks {PHY_CLK125}] -hold 0.070  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.080  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.080  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.080  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -setup 0.110  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {PHY_RX_CLOCK_2}] -hold 0.080  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {virt_CBCLK}] -rise_to [get_clocks {PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[1]}] -setup 0.060  
set_clock_uncertainty -rise_from [get_clocks {virt_CBCLK}] -rise_to [get_clocks {PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[1]}] -hold 0.090  
set_clock_uncertainty -rise_from [get_clocks {virt_CBCLK}] -fall_to [get_clocks {PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[1]}] -setup 0.060  
set_clock_uncertainty -rise_from [get_clocks {virt_CBCLK}] -fall_to [get_clocks {PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[1]}] -hold 0.090  
set_clock_uncertainty -rise_from [get_clocks {virt_CBCLK}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.070  
set_clock_uncertainty -rise_from [get_clocks {virt_CBCLK}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.090  
set_clock_uncertainty -rise_from [get_clocks {virt_CBCLK}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.070  
set_clock_uncertainty -rise_from [get_clocks {virt_CBCLK}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.090  
set_clock_uncertainty -fall_from [get_clocks {virt_CBCLK}] -rise_to [get_clocks {PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[1]}] -setup 0.060  
set_clock_uncertainty -fall_from [get_clocks {virt_CBCLK}] -rise_to [get_clocks {PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[1]}] -hold 0.090  
set_clock_uncertainty -fall_from [get_clocks {virt_CBCLK}] -fall_to [get_clocks {PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[1]}] -setup 0.060  
set_clock_uncertainty -fall_from [get_clocks {virt_CBCLK}] -fall_to [get_clocks {PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[1]}] -hold 0.090  
set_clock_uncertainty -fall_from [get_clocks {virt_CBCLK}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.070  
set_clock_uncertainty -fall_from [get_clocks {virt_CBCLK}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.090  
set_clock_uncertainty -fall_from [get_clocks {virt_CBCLK}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.070  
set_clock_uncertainty -fall_from [get_clocks {virt_CBCLK}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.090  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK}] -rise_to [get_clocks {PHY_RX_CLOCK_2}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK}] -fall_to [get_clocks {PHY_RX_CLOCK_2}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK}] -rise_to [get_clocks {PHY_RX_CLOCK}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PHY_RX_CLOCK}] -fall_to [get_clocks {PHY_RX_CLOCK}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK}] -rise_to [get_clocks {PHY_RX_CLOCK_2}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK}] -fall_to [get_clocks {PHY_RX_CLOCK_2}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK}] -rise_to [get_clocks {PHY_RX_CLOCK}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PHY_RX_CLOCK}] -fall_to [get_clocks {PHY_RX_CLOCK}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PHY_CLK125}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.070  
set_clock_uncertainty -rise_from [get_clocks {PHY_CLK125}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.100  
set_clock_uncertainty -rise_from [get_clocks {PHY_CLK125}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.070  
set_clock_uncertainty -rise_from [get_clocks {PHY_CLK125}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.100  
set_clock_uncertainty -rise_from [get_clocks {PHY_CLK125}] -rise_to [get_clocks {PHY_RX_CLOCK}]  0.040  
set_clock_uncertainty -rise_from [get_clocks {PHY_CLK125}] -fall_to [get_clocks {PHY_RX_CLOCK}]  0.040  
set_clock_uncertainty -rise_from [get_clocks {PHY_CLK125}] -rise_to [get_clocks {PHY_CLK125}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {PHY_CLK125}] -fall_to [get_clocks {PHY_CLK125}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PHY_CLK125}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.070  
set_clock_uncertainty -fall_from [get_clocks {PHY_CLK125}] -rise_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.100  
set_clock_uncertainty -fall_from [get_clocks {PHY_CLK125}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.070  
set_clock_uncertainty -fall_from [get_clocks {PHY_CLK125}] -fall_to [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.100  
set_clock_uncertainty -fall_from [get_clocks {PHY_CLK125}] -rise_to [get_clocks {PHY_RX_CLOCK}]  0.040  
set_clock_uncertainty -fall_from [get_clocks {PHY_CLK125}] -fall_to [get_clocks {PHY_RX_CLOCK}]  0.040  
set_clock_uncertainty -fall_from [get_clocks {PHY_CLK125}] -rise_to [get_clocks {PHY_CLK125}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {PHY_CLK125}] -fall_to [get_clocks {PHY_CLK125}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {_122MHz}] -rise_to [get_clocks {PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.070  
set_clock_uncertainty -rise_from [get_clocks {_122MHz}] -rise_to [get_clocks {PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.100  
set_clock_uncertainty -rise_from [get_clocks {_122MHz}] -fall_to [get_clocks {PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.070  
set_clock_uncertainty -rise_from [get_clocks {_122MHz}] -fall_to [get_clocks {PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.100  
set_clock_uncertainty -rise_from [get_clocks {_122MHz}] -rise_to [get_clocks {_122MHz}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {_122MHz}] -fall_to [get_clocks {_122MHz}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {_122MHz}] -rise_to [get_clocks {LTC2208_122MHz}]  0.040  
set_clock_uncertainty -rise_from [get_clocks {_122MHz}] -fall_to [get_clocks {LTC2208_122MHz}]  0.040  
set_clock_uncertainty -fall_from [get_clocks {_122MHz}] -rise_to [get_clocks {PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.070  
set_clock_uncertainty -fall_from [get_clocks {_122MHz}] -rise_to [get_clocks {PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.100  
set_clock_uncertainty -fall_from [get_clocks {_122MHz}] -fall_to [get_clocks {PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[0]}] -setup 0.070  
set_clock_uncertainty -fall_from [get_clocks {_122MHz}] -fall_to [get_clocks {PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[0]}] -hold 0.100  
set_clock_uncertainty -fall_from [get_clocks {_122MHz}] -rise_to [get_clocks {_122MHz}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {_122MHz}] -fall_to [get_clocks {_122MHz}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {_122MHz}] -rise_to [get_clocks {LTC2208_122MHz}]  0.040  
set_clock_uncertainty -fall_from [get_clocks {_122MHz}] -fall_to [get_clocks {LTC2208_122MHz}]  0.040  
set_clock_uncertainty -rise_from [get_clocks {LTC2208_122MHz}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] -setup 0.070  
set_clock_uncertainty -rise_from [get_clocks {LTC2208_122MHz}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] -hold 0.100  
set_clock_uncertainty -rise_from [get_clocks {LTC2208_122MHz}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] -setup 0.070  
set_clock_uncertainty -rise_from [get_clocks {LTC2208_122MHz}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] -hold 0.100  
set_clock_uncertainty -rise_from [get_clocks {LTC2208_122MHz}] -rise_to [get_clocks {_122MHz}]  0.040  
set_clock_uncertainty -rise_from [get_clocks {LTC2208_122MHz}] -fall_to [get_clocks {_122MHz}]  0.040  
set_clock_uncertainty -rise_from [get_clocks {LTC2208_122MHz}] -rise_to [get_clocks {LTC2208_122MHz_2}]  0.030  
set_clock_uncertainty -rise_from [get_clocks {LTC2208_122MHz}] -fall_to [get_clocks {LTC2208_122MHz_2}]  0.030  
set_clock_uncertainty -rise_from [get_clocks {LTC2208_122MHz}] -rise_to [get_clocks {LTC2208_122MHz}]  0.020  
set_clock_uncertainty -rise_from [get_clocks {LTC2208_122MHz}] -fall_to [get_clocks {LTC2208_122MHz}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {LTC2208_122MHz}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] -setup 0.070  
set_clock_uncertainty -fall_from [get_clocks {LTC2208_122MHz}] -rise_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] -hold 0.100  
set_clock_uncertainty -fall_from [get_clocks {LTC2208_122MHz}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] -setup 0.070  
set_clock_uncertainty -fall_from [get_clocks {LTC2208_122MHz}] -fall_to [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] -hold 0.100  
set_clock_uncertainty -fall_from [get_clocks {LTC2208_122MHz}] -rise_to [get_clocks {_122MHz}]  0.040  
set_clock_uncertainty -fall_from [get_clocks {LTC2208_122MHz}] -fall_to [get_clocks {_122MHz}]  0.040  
set_clock_uncertainty -fall_from [get_clocks {LTC2208_122MHz}] -rise_to [get_clocks {LTC2208_122MHz_2}]  0.030  
set_clock_uncertainty -fall_from [get_clocks {LTC2208_122MHz}] -fall_to [get_clocks {LTC2208_122MHz_2}]  0.030  
set_clock_uncertainty -fall_from [get_clocks {LTC2208_122MHz}] -rise_to [get_clocks {LTC2208_122MHz}]  0.020  
set_clock_uncertainty -fall_from [get_clocks {LTC2208_122MHz}] -fall_to [get_clocks {LTC2208_122MHz}]  0.020  


#**************************************************************
# Set Input Delay
#**************************************************************

set_input_delay -add_delay  -clock [get_clocks {virt_CBCLK}]  10.000 [get_ports {ADCMISO}]
set_input_delay -add_delay  -clock [get_clocks {virt_CBCLK}]  20.000 [get_ports {CDOUT}]
set_input_delay -add_delay  -clock [get_clocks {LTC2208_122MHz}]  1.000 [get_ports {INA[0]}]
set_input_delay -add_delay  -clock [get_clocks {LTC2208_122MHz}]  1.000 [get_ports {INA[1]}]
set_input_delay -add_delay  -clock [get_clocks {LTC2208_122MHz}]  1.000 [get_ports {INA[2]}]
set_input_delay -add_delay  -clock [get_clocks {LTC2208_122MHz}]  1.000 [get_ports {INA[3]}]
set_input_delay -add_delay  -clock [get_clocks {LTC2208_122MHz}]  1.000 [get_ports {INA[4]}]
set_input_delay -add_delay  -clock [get_clocks {LTC2208_122MHz}]  1.000 [get_ports {INA[5]}]
set_input_delay -add_delay  -clock [get_clocks {LTC2208_122MHz}]  1.000 [get_ports {INA[6]}]
set_input_delay -add_delay  -clock [get_clocks {LTC2208_122MHz}]  1.000 [get_ports {INA[7]}]
set_input_delay -add_delay  -clock [get_clocks {LTC2208_122MHz}]  1.000 [get_ports {INA[8]}]
set_input_delay -add_delay  -clock [get_clocks {LTC2208_122MHz}]  1.000 [get_ports {INA[9]}]
set_input_delay -add_delay  -clock [get_clocks {LTC2208_122MHz}]  1.000 [get_ports {INA[10]}]
set_input_delay -add_delay  -clock [get_clocks {LTC2208_122MHz}]  1.000 [get_ports {INA[11]}]
set_input_delay -add_delay  -clock [get_clocks {LTC2208_122MHz}]  1.000 [get_ports {INA[12]}]
set_input_delay -add_delay  -clock [get_clocks {LTC2208_122MHz}]  1.000 [get_ports {INA[13]}]
set_input_delay -add_delay  -clock [get_clocks {LTC2208_122MHz}]  1.000 [get_ports {INA[14]}]
set_input_delay -add_delay  -clock [get_clocks {LTC2208_122MHz}]  1.000 [get_ports {INA[15]}]
set_input_delay -add_delay  -clock [get_clocks {LTC2208_122MHz_2}]  1.000 [get_ports {INA_2[0]}]
set_input_delay -add_delay  -clock [get_clocks {LTC2208_122MHz_2}]  1.000 [get_ports {INA_2[1]}]
set_input_delay -add_delay  -clock [get_clocks {LTC2208_122MHz_2}]  1.000 [get_ports {INA_2[2]}]
set_input_delay -add_delay  -clock [get_clocks {LTC2208_122MHz_2}]  1.000 [get_ports {INA_2[3]}]
set_input_delay -add_delay  -clock [get_clocks {LTC2208_122MHz_2}]  1.000 [get_ports {INA_2[4]}]
set_input_delay -add_delay  -clock [get_clocks {LTC2208_122MHz_2}]  1.000 [get_ports {INA_2[5]}]
set_input_delay -add_delay  -clock [get_clocks {LTC2208_122MHz_2}]  1.000 [get_ports {INA_2[6]}]
set_input_delay -add_delay  -clock [get_clocks {LTC2208_122MHz_2}]  1.000 [get_ports {INA_2[7]}]
set_input_delay -add_delay  -clock [get_clocks {LTC2208_122MHz_2}]  1.000 [get_ports {INA_2[8]}]
set_input_delay -add_delay  -clock [get_clocks {LTC2208_122MHz_2}]  1.000 [get_ports {INA_2[9]}]
set_input_delay -add_delay  -clock [get_clocks {LTC2208_122MHz_2}]  1.000 [get_ports {INA_2[10]}]
set_input_delay -add_delay  -clock [get_clocks {LTC2208_122MHz_2}]  1.000 [get_ports {INA_2[11]}]
set_input_delay -add_delay  -clock [get_clocks {LTC2208_122MHz_2}]  1.000 [get_ports {INA_2[12]}]
set_input_delay -add_delay  -clock [get_clocks {LTC2208_122MHz_2}]  1.000 [get_ports {INA_2[13]}]
set_input_delay -add_delay  -clock [get_clocks {LTC2208_122MHz_2}]  1.000 [get_ports {INA_2[14]}]
set_input_delay -add_delay  -clock [get_clocks {LTC2208_122MHz_2}]  1.000 [get_ports {INA_2[15]}]
set_input_delay -add_delay  -clock [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}]  10.000 [get_ports {PHY_INT_N}]
set_input_delay -add_delay -max -clock [get_clocks {PHY_CLK125}]  2.000 [get_ports {PHY_INT_N}]
set_input_delay -add_delay -min -clock [get_clocks {PHY_CLK125}]  -0.500 [get_ports {PHY_INT_N}]
set_input_delay -add_delay  -clock [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}]  10.000 [get_ports {PHY_MDIO}]
set_input_delay -add_delay -max -clock [get_clocks {PHY_CLK125}]  2.000 [get_ports {PHY_MDIO}]
set_input_delay -add_delay -min -clock [get_clocks {PHY_CLK125}]  -0.500 [get_ports {PHY_MDIO}]
set_input_delay -add_delay -max -clock [get_clocks {PHY_CLK125}]  2.000 [get_ports {PHY_RX[0]}]
set_input_delay -add_delay -min -clock [get_clocks {PHY_CLK125}]  -0.500 [get_ports {PHY_RX[0]}]
set_input_delay -add_delay -max -clock [get_clocks {PHY_CLK125}]  2.000 [get_ports {PHY_RX[1]}]
set_input_delay -add_delay -min -clock [get_clocks {PHY_CLK125}]  -0.500 [get_ports {PHY_RX[1]}]
set_input_delay -add_delay -max -clock [get_clocks {PHY_CLK125}]  2.000 [get_ports {PHY_RX[2]}]
set_input_delay -add_delay -min -clock [get_clocks {PHY_CLK125}]  -0.500 [get_ports {PHY_RX[2]}]
set_input_delay -add_delay -max -clock [get_clocks {PHY_CLK125}]  2.000 [get_ports {PHY_RX[3]}]
set_input_delay -add_delay -min -clock [get_clocks {PHY_CLK125}]  -0.500 [get_ports {PHY_RX[3]}]
set_input_delay -add_delay -max -clock [get_clocks {PHY_CLK125}]  2.000 [get_ports {RX_DV}]
set_input_delay -add_delay -min -clock [get_clocks {PHY_CLK125}]  -0.500 [get_ports {RX_DV}]
set_input_delay -add_delay  -clock [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}]  40.000 [get_ports {SO}]
set_input_delay -add_delay  -clock [get_clocks {LTC2208_122MHz}]  1.000 [get_ports {SPI_SDI}]


#**************************************************************
# Set Output Delay
#**************************************************************

set_output_delay -add_delay  -clock [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}]  10.000 [get_ports {ADCMOSI}]
set_output_delay -add_delay  -clock [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}]  10.000 [get_ports {ATTN_DATA}]
set_output_delay -add_delay  -clock_fall -clock [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}]  10.000 [get_ports {ATTN_DATA}]
set_output_delay -add_delay  -clock [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}]  10.000 [get_ports {ATTN_DATA_2}]
set_output_delay -add_delay  -clock_fall -clock [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}]  10.000 [get_ports {ATTN_DATA_2}]
set_output_delay -add_delay  -clock [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}]  10.000 [get_ports {ATTN_LE}]
set_output_delay -add_delay  -clock_fall -clock [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}]  10.000 [get_ports {ATTN_LE}]
set_output_delay -add_delay  -clock [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}]  10.000 [get_ports {ATTN_LE_2}]
set_output_delay -add_delay  -clock_fall -clock [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}]  10.000 [get_ports {ATTN_LE_2}]
set_output_delay -add_delay  -clock [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}]  10.000 [get_ports {CDIN}]
set_output_delay -add_delay  -clock [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}]  20.000 [get_ports {CMODE}]
set_output_delay -add_delay  -clock [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}]  40.000 [get_ports {CS}]
set_output_delay -add_delay  -clock [get_clocks {_122MHz}]  1.000 [get_ports {DAC_ALC}]
set_output_delay -add_delay  -clock [get_clocks {_122MHz}]  1.000 [get_ports {FPGA_PLL}]
set_output_delay -add_delay  -clock [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}]  10.000 [get_ports {J15_5}]
set_output_delay -add_delay  -clock [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}]  10.000 [get_ports {J15_6}]
set_output_delay -add_delay  -clock [get_clocks {_122MHz}]  1.000 [get_ports {MICBIAS_ENABLE}]
set_output_delay -add_delay  -clock [get_clocks {_122MHz}]  1.000 [get_ports {MICBIAS_SELECT}]
set_output_delay -add_delay  -clock [get_clocks {_122MHz}]  1.000 [get_ports {MIC_SIG_SELECT}]
set_output_delay -add_delay  -clock [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}]  20.000 [get_ports {MOSI}]
set_output_delay -add_delay  -clock [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}]  10.000 [get_ports {PHY_MDIO}]
set_output_delay -add_delay -max -clock [get_clocks {PHY_CLK125}]  2.000 [get_ports {PHY_TX[0]}]
set_output_delay -add_delay -min -clock [get_clocks {PHY_CLK125}]  -0.500 [get_ports {PHY_TX[0]}]
set_output_delay -add_delay -max -clock [get_clocks {PHY_CLK125}]  2.000 [get_ports {PHY_TX[1]}]
set_output_delay -add_delay -min -clock [get_clocks {PHY_CLK125}]  -0.500 [get_ports {PHY_TX[1]}]
set_output_delay -add_delay -max -clock [get_clocks {PHY_CLK125}]  2.000 [get_ports {PHY_TX[2]}]
set_output_delay -add_delay -min -clock [get_clocks {PHY_CLK125}]  -0.500 [get_ports {PHY_TX[2]}]
set_output_delay -add_delay -max -clock [get_clocks {PHY_CLK125}]  2.000 [get_ports {PHY_TX[3]}]
set_output_delay -add_delay -min -clock [get_clocks {PHY_CLK125}]  -0.500 [get_ports {PHY_TX[3]}]
set_output_delay -add_delay -max -clock [get_clocks {PHY_CLK125}]  2.000 [get_ports {PHY_TX_CLOCK}]
set_output_delay -add_delay -min -clock [get_clocks {PHY_CLK125}]  -0.500 [get_ports {PHY_TX_CLOCK}]
set_output_delay -add_delay -max -clock [get_clocks {PHY_CLK125}]  2.000 [get_ports {PHY_TX_EN}]
set_output_delay -add_delay -min -clock [get_clocks {PHY_CLK125}]  -0.500 [get_ports {PHY_TX_EN}]
set_output_delay -add_delay  -clock [get_clocks {_122MHz}]  1.000 [get_ports {PTT_SELECT}]
set_output_delay -add_delay  -clock [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}]  40.000 [get_ports {SCK}]
set_output_delay -add_delay  -clock [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]}]  40.000 [get_ports {SI}]
set_output_delay -add_delay  -clock [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}]  10.000 [get_ports {SPI_SDO}]
set_output_delay -add_delay  -clock [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}]  10.000 [get_ports {nADCCS}]
set_output_delay -add_delay  -clock [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]}]  20.000 [get_ports {nCS}]


#**************************************************************
# Set Clock Groups
#**************************************************************

set_clock_groups -asynchronous -group [get_clocks {PHY_CLK125  PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]  PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]  PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]  PHY_RX_CLOCK  PHY_RX_CLOCK_2  }] -group [get_clocks {_122MHz  LTC2208_122MHz  LTC2208_122MHz_2  PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]  PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]  PLL_IF_inst|altpll_component|auto_generated|pll1|clk[3]  PLL_inst|altpll_component|auto_generated|pll1|clk[0]  PLL_inst|altpll_component|auto_generated|pll1|clk[1]  SHIFT_inst|altpll_component|auto_generated|pll1|clk[0]  PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[0]  }] -group [get_clocks {OSC_10MHZ}] -group [get_clocks {EXT_OSC_10MHZ}] -group [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0] }] 
set_clock_groups -asynchronous -group [get_clocks {PHY_CLK125  PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0]  PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]  PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2]  PHY_RX_CLOCK  PHY_RX_CLOCK_2  }] -group [get_clocks {_122MHz  LTC2208_122MHz  LTC2208_122MHz_2  PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1]  PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]  PLL_IF_inst|altpll_component|auto_generated|pll1|clk[3]  PLL_inst|altpll_component|auto_generated|pll1|clk[0]  PLL_inst|altpll_component|auto_generated|pll1|clk[1]  SHIFT_inst|altpll_component|auto_generated|pll1|clk[0]  PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[0]  }] -group [get_clocks {OSC_10MHZ}] -group [get_clocks {EXT_OSC_10MHZ}] -group [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0] }] 


#**************************************************************
# Set False Path
#**************************************************************

set_false_path  -from  [get_clocks {LTC2208_122MHz_2}]  -to  [get_clocks {LTC2208_122MHz}]
set_false_path -setup -fall_from  [get_clocks {virt_PHY_RX_CLOCK}]  -rise_to  [get_clocks {PHY_RX_CLOCK}]
set_false_path -setup -rise_from  [get_clocks {virt_PHY_RX_CLOCK}]  -fall_to  [get_clocks {PHY_RX_CLOCK}]
set_false_path -hold -fall_from  [get_clocks {virt_PHY_RX_CLOCK}]  -fall_to  [get_clocks {PHY_RX_CLOCK}]
set_false_path -hold -rise_from  [get_clocks {virt_PHY_RX_CLOCK}]  -rise_to  [get_clocks {PHY_RX_CLOCK}]
set_false_path  -from  [get_clocks {LTC2208_122MHz_2}]  -to  [get_clocks {LTC2208_122MHz}]
set_false_path -setup -fall_from  [get_clocks {virt_PHY_RX_CLOCK}]  -rise_to  [get_clocks {PHY_RX_CLOCK}]
set_false_path -setup -rise_from  [get_clocks {virt_PHY_RX_CLOCK}]  -fall_to  [get_clocks {PHY_RX_CLOCK}]
set_false_path -hold -fall_from  [get_clocks {virt_PHY_RX_CLOCK}]  -fall_to  [get_clocks {PHY_RX_CLOCK}]
set_false_path -hold -rise_from  [get_clocks {virt_PHY_RX_CLOCK}]  -rise_to  [get_clocks {PHY_RX_CLOCK}]
set_false_path -from [get_keepers {*rdptr_g*}] -to [get_keepers {*ws_dgrp|dffpipe_ve9:dffpipe19|dffe20a*}]
set_false_path -from [get_keepers {*delayed_wrptr_g*}] -to [get_keepers {*rs_dgwp|dffpipe_te9:dffpipe15|dffe16a*}]
set_false_path -from [get_keepers {*rdptr_g*}] -to [get_keepers {*ws_dgrp|dffpipe_re9:dffpipe16|dffe17a*}]
set_false_path -from [get_keepers {*delayed_wrptr_g*}] -to [get_keepers {*rs_dgwp|dffpipe_qe9:dffpipe13|dffe14a*}]
set_false_path -from [get_keepers {*rdptr_g*}] -to [get_keepers {*ws_dgrp|dffpipe_1f9:dffpipe17|dffe18a*}]
set_false_path -from [get_keepers {*delayed_wrptr_g*}] -to [get_keepers {*rs_dgwp|dffpipe_0f9:dffpipe14|dffe15a*}]
set_false_path -from [get_keepers {*rdptr_g*}] -to [get_keepers {*ws_dgrp|dffpipe_4f9:dffpipe13|dffe14a*}]
set_false_path -from [get_keepers {*delayed_wrptr_g*}] -to [get_keepers {*rs_dgwp|dffpipe_3f9:dffpipe10|dffe11a*}]
set_false_path -from [get_keepers {*rdptr_g*}] -to [get_keepers {*ws_dgrp|dffpipe_fd9:dffpipe15|dffe16a*}]
set_false_path -from [get_keepers {*delayed_wrptr_g*}] -to [get_keepers {*rs_dgwp|dffpipe_ed9:dffpipe12|dffe13a*}]
set_false_path -to [get_ports {CMCLK CBCLK CLRCIN CLRCOUT ATTN_CLK* SSCK ADCCLK SPI_SCK PHY_MDC}]
set_false_path -to [get_keepers {Status_LED DEBUG_LED* DITH* FPGA_PTT  NCONFIG  RAND*  USEROUT*  DRIVER_PA_EN CTRL_TRSW TX_ATTEN*}]
set_false_path -from [get_keepers {ANT_TUNE IO*  KEY_DASH KEY_DOT OVERFLOW* PTT TX_ATTEN_SELECT MODE2}] 


#**************************************************************
# Set Multicycle Path
#**************************************************************



#**************************************************************
# Set Maximum Delay
#**************************************************************

set_max_delay -from  [get_clocks {_122MHz}]  -to  [get_clocks {_122MHz}] 10.000
set_max_delay -from  [get_clocks {LTC2208_122MHz}]  -to  [get_clocks {LTC2208_122MHz}] 13.000
set_max_delay -from  [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}]  -to  [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] 26.000
set_max_delay -from  [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}]  -to  [get_clocks {PHY_CLK125}] 8.000
set_max_delay -from  [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[0]}]  -to  [get_clocks {_122MHz}] 6.000
set_max_delay -from  [get_clocks {PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[0]}]  -to  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] 20.000
set_max_delay -from  [get_clocks {virt_CBCLK}]  -to  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] 23.000
set_max_delay -from  [get_clocks {_122MHz}]  -to  [get_clocks {_122MHz}] 10.000
set_max_delay -from  [get_clocks {LTC2208_122MHz}]  -to  [get_clocks {LTC2208_122MHz}] 13.000
set_max_delay -from  [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}]  -to  [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}] 26.000
set_max_delay -from  [get_clocks {PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1]}]  -to  [get_clocks {PHY_CLK125}] 8.000
set_max_delay -from  [get_clocks {PLL_inst|altpll_component|auto_generated|pll1|clk[0]}]  -to  [get_clocks {_122MHz}] 6.000
set_max_delay -from  [get_clocks {PLL_30MHz_inst|altpll_component|auto_generated|pll1|clk[0]}]  -to  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2]}] 20.000
set_max_delay -from  [get_clocks {virt_CBCLK}]  -to  [get_clocks {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] 23.000


#**************************************************************
# Set Minimum Delay
#**************************************************************



#**************************************************************
# Set Input Transition
#**************************************************************

