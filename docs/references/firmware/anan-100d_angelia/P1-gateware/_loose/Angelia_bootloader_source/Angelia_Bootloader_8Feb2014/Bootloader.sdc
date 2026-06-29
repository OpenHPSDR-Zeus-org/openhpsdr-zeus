create_clock -period 125.00MHz   -name PHY_CLK125 [get_ports PHY_CLK125]
create_clock -period  25.00MHz   -name PHY_RX_CLOCK [get_ports PHY_RX_CLOCK]
create_clock -period  12.50MHz   -name PHY_RX_CLOCK_2  PHY_RX_CLOCK_2

derive_pll_clocks

derive_clock_uncertainty

set_clock_groups -asynchronous -group {PLL_inst|altpll_component|auto_generated|pll1|clk[1] \
													PLL_inst|altpll_component|auto_generated|pll1|clk[2] \
													PLL_inst|altpll_component|auto_generated|pll1|clk[3]} \
										 -group {PHY_CLK125} \
										 -group {PHY_RX_CLOCK} \
										 -group {PHY_RX_CLOCK_2}

										 
# Add internally generated clocks
# create_generated_clock -divide_by 4 -source Hermes_clk_lrclk_gen:clrgen|BCLK -name spc[1]


# for an inverted clock creat a clock using the -invert option  in create_generated_clock


