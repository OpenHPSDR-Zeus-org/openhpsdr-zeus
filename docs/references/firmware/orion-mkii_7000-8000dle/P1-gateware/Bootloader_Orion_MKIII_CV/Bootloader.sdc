# Bootloader.sdc
#

# Create Clock (base clocks; external to FPGA)

create_clock -period 125.00MHz   -name PHY_CLK125 	[get_ports PHY_CLK125]
create_clock -period  25.00MHz   -name PHY_RX_CLOCK 	[get_ports PHY_RX_CLOCK]

derive_pll_clocks

derive_clock_uncertainty

#
# Create Generated Clock (internal to FPGA)
#
create_generated_clock -name PHY_RX-CLOCK_2 -source PHY_RX_CLOCK -divide_by 2 PHY_RX_CLOCK_2

#
# Set Clock Groups
#
# no asynchronous clock groups, all are synchronous


# Set Input Delay
#
set_input_delay -add_delay -max -clock PHY_CLK125  1.000 {CONFIG PHY_DV PHY_MDIO PHY_RX[*] SW17 SW18}
set_input_delay -add_delay -min -clock PHY_CLK125 -1.000 {CONFIG PHY_DV PHY_MDIO PHY_RX[*] SW17 SW18}

#
# Set Output Delay
#
set_output_delay -add_delay -max -clock PHY_CLK125  1.000 {DEBUG_LED* NODE_ADDR_CS PHY_MDC PHY_TX[*] PHY_TX_CLOCK PHY_TX_EN SCK SI STATUS_LED}
set_output_delay -add_delay -min -clock PHY_CLK125 -1.000 {DEBUG_LED* NODE_ADDR_CS PHY_MDC PHY_TX[*] PHY_TX_CLOCK PHY_TX_EN SCK SI STATUS_LED}

#
# Set Maximum Delay
#
set_max_delay -to PHY_MDIO 17
set_max_delay -from PHY_CLK125 -to PHY_CLK125 10
set_max_delay -from PHY_CLK125 -to PLL_inst|tx_pllv_inst|altera_pll_i|general[0].gpll~PLL_OUTPUT_COUNTER|divclk 9
set_max_delay -from PHY_CLK125 -to PLL_inst|tx_pllv_inst|altera_pll_i|general[2].gpll~PLL_OUTPUT_COUNTER|divclk 9
set_max_delay -from PLL_inst|tx_pllv_inst|altera_pll_i|general[0].gpll~PLL_OUTPUT_COUNTER|divclk -to PHY_CLK125 18
set_max_delay -from PLL_inst|tx_pllv_inst|altera_pll_i|general[1].gpll~PLL_OUTPUT_COUNTER|divclk -to PHY_CLK125 16
set_max_delay -from PLL_inst|tx_pllv_inst|altera_pll_i|general[2].gpll~PLL_OUTPUT_COUNTER|divclk -to PHY_CLK125 16


#
# Set Minimum Delay
#
set_min_delay -to PHY_MDIO 0

set_false_path -from  {EPCS_flash}
