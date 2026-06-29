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
set_input_delay -add_delay -max -clock PHY_CLK125  1.000 {ASMI2:ASMI_inst|ASMI2_altasmi_parallel_smm2:ASMI2_altasmi_parallel_smm2_component|cycloneii_asmiblock2~ALTERA_DATA0 CONFIG PHY_DV PHY_MDIO PHY_RX[*] SW17 SW18}
set_input_delay -add_delay -min -clock PHY_CLK125 -1.000 {ASMI2:ASMI_inst|ASMI2_altasmi_parallel_smm2:ASMI2_altasmi_parallel_smm2_component|cycloneii_asmiblock2~ALTERA_DATA0 CONFIG PHY_DV PHY_MDIO PHY_RX[*] SW17 SW18}

#
# Set Output Delay
#
set_output_delay -add_delay -max -clock PHY_CLK125  1.000 {ASMI2:ASMI_inst|ASMI2_altasmi_parallel_smm2:ASMI2_altasmi_parallel_smm2_component|cycloneii_asmiblock2~ALTERA_DCLK ASMI2:ASMI_inst|ASMI2_altasmi_parallel_smm2:ASMI2_altasmi_parallel_smm2_component|cycloneii_asmiblock2~ALTERA_SCE ASMI2:ASMI_inst|ASMI2_altasmi_parallel_smm2:ASMI2_altasmi_parallel_smm2_component|cycloneii_asmiblock2~ALTERA_SDO DEBUG_LED* NODE_ADDR_CS PHY_MDC PHY_TX[*] PHY_TX_CLOCK PHY_TX_EN SCK SI STATUS_LED}
set_output_delay -add_delay -min -clock PHY_CLK125 -1.000 {ASMI2:ASMI_inst|ASMI2_altasmi_parallel_smm2:ASMI2_altasmi_parallel_smm2_component|cycloneii_asmiblock2~ALTERA_DCLK ASMI2:ASMI_inst|ASMI2_altasmi_parallel_smm2:ASMI2_altasmi_parallel_smm2_component|cycloneii_asmiblock2~ALTERA_SCE ASMI2:ASMI_inst|ASMI2_altasmi_parallel_smm2:ASMI2_altasmi_parallel_smm2_component|cycloneii_asmiblock2~ALTERA_SDO DEBUG_LED* NODE_ADDR_CS PHY_MDC PHY_TX[*] PHY_TX_CLOCK PHY_TX_EN SCK SI STATUS_LED}

#
# Set Maximum Delay
#
set_max_delay -to PHY_MDIO 14

set_max_delay -from PLL_inst|altpll_component|auto_generated|pll1|clk[1] -to PHY_CLK125 18

set_max_delay -from PLL_inst|altpll_component|auto_generated|pll1|clk[2] -to PHY_CLK125 13

set_max_delay -from PLL_inst|altpll_component|auto_generated|pll1|clk[3] -to PHY_CLK125 15


#
# Set Minimum Delay
#
set_min_delay -to PHY_MDIO 0
