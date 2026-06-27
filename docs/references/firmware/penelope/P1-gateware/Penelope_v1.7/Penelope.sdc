create_clock -period 8.138   -name C122_clk C122_clk
create_clock -period 100.000 -name _10MHZ _10MHZ

# Add internally generated clocks
create_generated_clock -divide_by 40 -source [get_ports "C122_clk"] -name clk_lrclk_gen:clrgen|BCLK clk_lrclk_gen:clrgen|BCLK

# Automatically constrain PLL and other generated clocks
derive_pll_clocks -create_base_clocks

# Automatically calculate clock uncertainty to jitter and other effects.
#derive_clock_uncertainty
# Not supported for family Cyclone II

# clocks are normally considered related, seperate as required

set_clock_groups -exclusive -group {"_10MHZ", "C122_clk"}