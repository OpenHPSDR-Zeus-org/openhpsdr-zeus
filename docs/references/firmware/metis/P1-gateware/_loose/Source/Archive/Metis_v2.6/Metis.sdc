create_clock -period 125.00MHz   -name PHY_CLK125 [get_ports PHY_CLK125]
create_clock -period  25.00MHz   -name CLK_25MHZ [get_ports CLK_25MHZ]
create_clock -period  25.00MHz   -name PHY_RX_CLOCK [get_ports PHY_RX_CLOCK]
create_clock -period  12.50MHz   -name Rx_MAC:Rx_MAC_inst|PHY_100T_state Rx_MAC:Rx_MAC_inst|PHY_100T_state
create_clock -period   2.50MHz   -name PHY_MDIO_clk PHY_MDIO_clk


derive_pll_clocks

derive_clock_uncertainty

										 
# Add internally generated clocks

create_generated_clock -divide_by 2 -source PHY_RX_CLOCK -name PHY_RX_CLOCK_2  PHY_RX_CLOCK_2
create_generated_clock -name {i2c_interface:interface_inst|i2c_master:master_inst|scl_clk~en} -source [get_pins {PLL_inst|altpll_component|auto_generated|pll1|clk[3]}] -divide_by 4 -master_clock {PLL_inst|altpll_component|auto_generated|pll1|clk[3]} [get_registers {i2c_interface:interface_inst|i2c_master:master_inst|scl_clk~en}]
create_generated_clock -name {i2c_interface:interface_inst|i2c_master:master_inst|data_clk} -source [get_pins {PLL_inst|altpll_component|auto_generated|pll1|clk[3]}] -divide_by 4 -master_clock {PLL_inst|altpll_component|auto_generated|pll1|clk[3]} [get_registers {i2c_interface:interface_inst|i2c_master:master_inst|data_clk}] 
 
# create_generated_clock -divide_by 4 -source Hermes_clk_lrclk_gen:clrgen|BCLK -name spc[1]
#create_generated_clock -source PLL_inst|altpll_component|auto_generated|pll1|clk[1] -name Tx_clock

set_clock_groups -asynchronous -group {PLL_inst|altpll_component|auto_generated|pll1|clk[0]} \
										  {PLL_inst|altpll_component|auto_generated|pll1|clk[1]} \
										  {PLL_inst|altpll_component|auto_generated|pll1|clk[2]} \
										  {PLL_inst|altpll_component|auto_generated|pll1|clk[3]} \
										 -group {CLK_25MHZ} \
										 -group {PHY_CLK125} \
										 -group {PHY_RX_CLOCK} \
										 -group {PHY_MDIO_clk} \
										 -group {i2c_interface:interface_inst|i2c_master:master_inst|data_clk} \
										 -group {i2c_interface:interface_inst|i2c_master:master_inst|scl_clk~en}


# for an inverted clock creat a clock using the -invert option  in create_generated_clock
set_false_path -from PHY_RX_CLOCK_2 -to PLL_inst|altpll_component|auto_generated|pll1|clk[0]  
