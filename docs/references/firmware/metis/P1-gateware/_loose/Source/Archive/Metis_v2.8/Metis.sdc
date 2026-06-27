# Metis.sdc
#
#*************************************************************************************
# Create Clock
#*************************************************************************************
# externally generated clocks (With respect to the FPGA)
#
create_clock -period 125.00MHz   -name PHY_CLK125 [get_ports PHY_CLK125]
create_clock -period  25.00MHz   -name CLK_25MHZ [get_ports CLK_25MHZ]
create_clock -period  25.00MHz   -name PHY_RX_CLOCK [get_ports PHY_RX_CLOCK]

derive_pll_clocks

derive_clock_uncertainty

#****************************************************************************************
# Create Generated Clocks
#****************************************************************************************										 
# internally generated clocks
#
create_generated_clock -name PHY_RX_CLOCK_2 -divide_by 2 -source PHY_RX_CLOCK PHY_RX_CLOCK_2
create_generated_clock -name i2c_interface:interface_inst|i2c_master:master_inst|scl_clk~en -source [get_pins {PLL_inst|altpll_component|auto_generated|pll1|clk[3]}] -divide_by 4 -master_clock {PLL_inst|altpll_component|auto_generated|pll1|clk[3]} [get_registers {i2c_interface:interface_inst|i2c_master:master_inst|scl_clk~en}]
create_generated_clock -name i2c_interface:interface_inst|i2c_master:master_inst|data_clk -source [get_pins {PLL_inst|altpll_component|auto_generated|pll1|clk[3]}] -divide_by 4 -master_clock {PLL_inst|altpll_component|auto_generated|pll1|clk[3]} [get_registers {i2c_interface:interface_inst|i2c_master:master_inst|data_clk}] 
create_generated_clock -name PHY_MDIO_clk -source [get_pins {PLL_inst|altpll_component|auto_generated|pll1|clk[1]}] -divide_by 10 PHY_MDIO_clk 


#*****************************************************************************************
# Set Clock Groups 
#*****************************************************************************************
# -- all clocks are generated from the 25MHZ crystal osc on Metis therefore all are related; no "asynchronous" groups


#*****************************************************************************************
# Set Input Delay
#*****************************************************************************************
#
set_input_delay -add_delay -clock PHY_CLK125 -max 1.000 {ASMI_interface:ASMI_int_inst|ASMI:ASMI_inst|ASMI_altasmi_parallel_cv82:ASMI_altasmi_parallel_cv82_component|cycloneii_asmiblock2~ALTERA_DATA0 ATLAS_A2 ATLAS_A3 ATLAS_A4 ATLAS_A5 ATLAS_A7 ATLAS_A8 ATLAS_A9 ATLAS_A10 ATLAS_A11 ATLAS_A12 ATLAS_A13 ATLAS_A21 ATLAS_C15 ATLAS_C22 ATLAS_C23 CONFIG IN0 IN1 IN2 MODE2 PHY_DV PHY_MDIO PHY_RX[*]}
set_input_delay -add_delay -clock PHY_CLK125 -min -1.000 {ASMI_interface:ASMI_int_inst|ASMI:ASMI_inst|ASMI_altasmi_parallel_cv82:ASMI_altasmi_parallel_cv82_component|cycloneii_asmiblock2~ALTERA_DATA0 ATLAS_A2 ATLAS_A3 ATLAS_A4 ATLAS_A5 ATLAS_A7 ATLAS_A8 ATLAS_A9 ATLAS_A10 ATLAS_A11 ATLAS_A12 ATLAS_A13 ATLAS_A21 ATLAS_C15 ATLAS_C22 ATLAS_C23 CONFIG IN0 IN1 IN2 MODE2 PHY_DV PHY_MDIO PHY_RX[*]}


#******************************************************************************************
# Set Output Delay
#******************************************************************************************
#
set_output_delay -add_delay -clock PHY_CLK125 -max 1.000 {ATLAS_A21 ASMI_interface:ASMI_int_inst|ASMI:ASMI_inst|ASMI_altasmi_parallel_cv82:ASMI_altasmi_parallel_cv82_component|cycloneii_asmiblock2~ALTERA_DCLK ASMI_interface:ASMI_int_inst|ASMI:ASMI_inst|ASMI_altasmi_parallel_cv82:ASMI_altasmi_parallel_cv82_component|cycloneii_asmiblock2~ALTERA_SCE ASMI_interface:ASMI_int_inst|ASMI:ASMI_inst|ASMI_altasmi_parallel_cv82:ASMI_altasmi_parallel_cv82_component|cycloneii_asmiblock2~ALTERA_SDO ATLAS_A20 ATLAS_C2 ATLAS_C19 ATLAS_C20 ATLAS_C21 ATLAS_C24 HEART_BEAT NCONFIG NODE_ADDR_CS PHY_MDC PHY_MDIO PHY_TX[*] PHY_TX_CLOCK PHY_TX_EN RAM_A0 RAM_A1 RAM_A2 RAM_A3 RAM_A4 RAM_A5 RAM_A6 RAM_A7 RAM_A10 RAM_A11 RAM_A12 RAM_A13 SCK SI USEROUT0 USEROUT1 USEROUT2 USEROUT3}
set_output_delay -add_delay -clock PHY_CLK125 -min -1.000 {ATLAS_A21 ASMI_interface:ASMI_int_inst|ASMI:ASMI_inst|ASMI_altasmi_parallel_cv82:ASMI_altasmi_parallel_cv82_component|cycloneii_asmiblock2~ALTERA_DCLK ASMI_interface:ASMI_int_inst|ASMI:ASMI_inst|ASMI_altasmi_parallel_cv82:ASMI_altasmi_parallel_cv82_component|cycloneii_asmiblock2~ALTERA_SCE ASMI_interface:ASMI_int_inst|ASMI:ASMI_inst|ASMI_altasmi_parallel_cv82:ASMI_altasmi_parallel_cv82_component|cycloneii_asmiblock2~ALTERA_SDO ATLAS_A20 ATLAS_C2 ATLAS_C19 ATLAS_C20 ATLAS_C21 ATLAS_C24 HEART_BEAT NCONFIG NODE_ADDR_CS PHY_MDC PHY_MDIO PHY_TX[*] PHY_TX_CLOCK PHY_TX_EN RAM_A0 RAM_A1 RAM_A2 RAM_A3 RAM_A4 RAM_A5 RAM_A6 RAM_A7 RAM_A10 RAM_A11 RAM_A12 RAM_A13 SCK SI USEROUT0 USEROUT1 USEROUT2 USEROUT3}


#******************************************************************************************
# Set Maximum Delay
#******************************************************************************************

set_max_delay -from i2c_interface:interface_inst|i2c_master:master_inst|scl_clk~en -to PHY_CLK125 32

set_max_delay -from i2c_interface:interface_inst|i2c_master:master_inst|data_clk -to PHY_CLK125 32

set_max_delay -from PHY_CLK125 -to PLL_inst|altpll_component|auto_generated|pll1|clk[0] 6
set_max_delay -from PHY_CLK125 -to PHY_CLK125 12
set_max_delay -from PHY_CLK125 -to i2c_interface:interface_inst|i2c_master:master_inst|scl_clk~en 2
set_max_delay -from PHY_CLK125 -to PHY_MDIO_clk 5

set_max_delay -from PHY_MDIO_clk -to PHY_CLK125 25

set_max_delay -from PHY_RX_CLOCK_2 -to PLL_inst|altpll_component|auto_generated|pll1|clk[0] 10
set_max_delay -from PHY_RX_CLOCK_2 -to PHY_CLK125 29

set_max_delay -from PLL_inst|altpll_component|auto_generated|pll1|clk[0] -to PHY_CLK125 19
set_max_delay -from PLL_inst|altpll_component|auto_generated|pll1|clk[0] -to PLL_inst|altpll_component|auto_generated|pll1|clk[2] 6

set_max_delay -from PLL_inst|altpll_component|auto_generated|pll1|clk[1] -to PHY_CLK125 16
set_max_delay -from PLL_inst|altpll_component|auto_generated|pll1|clk[1] -to PLL_inst|altpll_component|auto_generated|pll1|clk[0] 5

set_max_delay -from PLL_inst|altpll_component|auto_generated|pll1|clk[2] -to PLL_inst|altpll_component|auto_generated|pll1|clk[0] 8
set_max_delay -from PLL_inst|altpll_component|auto_generated|pll1|clk[2] -to PHY_CLK125  12
set_max_delay -from PLL_inst|altpll_component|auto_generated|pll1|clk[2] -to i2c_interface:interface_inst|i2c_master:master_inst|data_clk 8

set_max_delay -from PLL_inst|altpll_component|auto_generated|pll1|clk[3] -to PHY_CLK125 17


#******************************************************************************************
# Set Minimum Delay
#******************************************************************************************

set_min_delay -from i2c_interface:interface_inst|i2c_master:master_inst|scl_clk~en -to PHY_CLK125 8

set_min_delay -from i2c_interface:interface_inst|i2c_master:master_inst|data_clk -to PHY_CLK125 10

set_min_delay -from PHY_CLK125 -to PLL_inst|altpll_component|auto_generated|pll1|clk[0] -6
set_min_delay -from PHY_CLK125 -to PHY_CLK125 -2
set_min_delay -from PHY_CLK125 -to i2c_interface:interface_inst|i2c_master:master_inst|scl_clk~en -7
set_min_delay -from PHY_CLK125 -to PHY_MDIO_clk -2


set_min_delay -from PHY_MDIO_clk -to PHY_CLK125 8

set_min_delay -from PHY_RX_CLOCK_2 -to PLL_inst|altpll_component|auto_generated|pll1|clk[0] -2
set_min_delay -from PHY_RX_CLOCK_2 -to PHY_CLK125 9

set_min_delay -from PLL_inst|altpll_component|auto_generated|pll1|clk[0] -to PHY_CLK125 0
set_min_delay -from PLL_inst|altpll_component|auto_generated|pll1|clk[0] -to PLL_inst|altpll_component|auto_generated|pll1|clk[2] -2

set_min_delay -from PLL_inst|altpll_component|auto_generated|pll1|clk[1] -to PHY_CLK125 1
set_min_delay -from PLL_inst|altpll_component|auto_generated|pll1|clk[1] -to PLL_inst|altpll_component|auto_generated|pll1|clk[0] -2

set_min_delay -from PLL_inst|altpll_component|auto_generated|pll1|clk[2] -to PLL_inst|altpll_component|auto_generated|pll1|clk[0] -2
set_min_delay -from PLL_inst|altpll_component|auto_generated|pll1|clk[2] -to PHY_CLK125 1
set_min_delay -from PLL_inst|altpll_component|auto_generated|pll1|clk[2] -to i2c_interface:interface_inst|i2c_master:master_inst|data_clk -6

set_min_delay -from PLL_inst|altpll_component|auto_generated|pll1|clk[3] -to PHY_CLK125 5
