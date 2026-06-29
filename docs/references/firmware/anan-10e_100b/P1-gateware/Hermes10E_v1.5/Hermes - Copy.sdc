# Hermes.sdc
#
#**************************************************************
# Time Information
#**************************************************************

set_time_format -unit ns -decimal_places 3


#**************************************************************************************
# Create Clock
#**************************************************************************************
# externally generated clocks (with respect to the FPGA)
#
create_clock -period 122.880MHz	-name LTC2208_122MHz    [get_ports LTC2208_122MHz]
create_clock -period 122.880MHz	-name _122MHz		[get_ports _122MHz]
create_clock -period  10.000MHz	-name OSC_10MHZ		[get_ports OSC_10MHZ]
create_clock -period 125.000MHz	-name PHY_CLK125	[get_ports PHY_CLK125]
create_clock -period  25.000MHz	-name PHY_RX_CLOCK	[get_ports PHY_RX_CLOCK]


derive_pll_clocks

derive_clock_uncertainty


#*************************************************************************************
# Create Generated CloCK
#*************************************************************************************
# internally generated clocks
create_generated_clock -name PHY_RX_CLOCK_2 -source PHY_RX_CLOCK 	-divide_by 2 	PHY_RX_CLOCK_2

#*************************************************************************************
# Set Clock Groups
#*************************************************************************************
# Note: output clock c0 (48.034909 MHz) of PLL_IF_inst is asynchronous with input source clock inclk0 (122.88MHz)

set_clock_groups -asynchronous -group {PHY_CLK125 \
					PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0] \
					PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1] \
					PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[2] \
					PHY_RX_CLOCK \
					PHY_RX_CLOCK_2 \
					} \
				-group {_122MHz \
					LTC2208_122MHz \
					PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1] \
					PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2] \
					PLL_IF_inst|altpll_component|auto_generated|pll1|clk[3] \
					PLL_inst|altpll_component|auto_generated|pll1|clk[0] \
					} \
				-group {PLL2_inst|altpll_component|auto_generated|pll1|clk[0]}\
				-group {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0] }
										 

#*************************************************************************************************************
# set input delay
#*************************************************************************************************************
##
# was 2.000 max 0.000 min
set_input_delay -add_delay -max -clock PHY_CLK125 1.500  {PHY_MDIO PHY_RX[*]  RX_DV PHY_INT_N }
set_input_delay -add_delay -min -clock PHY_CLK125 -0.500 {PHY_MDIO PHY_RX[*] RX_DV PHY_INT_N }

set_input_delay -add_delay -max -clock LTC2208_122MHz 1.000  {ADCMISO ANT_TUNE CDOUT INA[*] IO4 IO5 IO6  IO8 KEY_DASH KEY_DOT OVERFLOW PTT SO SPI_SDI}
set_input_delay -add_delay -min -clock LTC2208_122MHz -1.000 {ADCMISO ANT_TUNE CDOUT INA[*] IO4 IO5 IO6 IO8 KEY_DASH KEY_DOT OVERFLOW PTT SO SPI_SDI}

#*************************************************************************************************************
# set output delay
#*************************************************************************************************************
# was 1.500 max -0.500 min
set_output_delay -add_delay -max -clock PHY_CLK125 1.500  {PHY_MDIO PHY_TX[*] PHY_TX_EN PHY_TX_CLOCK PHY_MDC }
set_output_delay -add_delay -min -clock PHY_CLK125 -0.500 {PHY_MDIO PHY_TX[*] PHY_TX_EN PHY_TX_CLOCK PHY_MDC }

# Rx appears to work, but Tx Ps works only when Hermes board is not warm 
set_output_delay -add_delay -max -clock _122MHz  1.000 {DACD[*] DAC_ALC ADCCLK ADCMOSI ATTN_CLK ATTN_DATA  ATTN_LE  CBCLK CDIN CLRCIN CLRCOUT CMCLK CS DEBUG_LED* DITH FPGA_PLL FPGA_PTT J15_5 J15_6  MOSI NCONFIG  RAND SCK SI SPI_SCK SPI_SDO SSCK Status_LED USEROUT* nADCCS nCS}
set_output_delay -add_delay -min -clock _122MHz -1.000 {DACD[*] DAC_ALC ADCCLK ADCMOSI ATTN_CLK ATTN_DATA ATTN_LE CBCLK CDIN CLRCIN CLRCOUT CMCLK CS DEBUG_LED* DITH FPGA_PLL FPGA_PTT J15_5 J15_6   MOSI NCONFIG  RAND SCK SI SPI_SCK SPI_SDO SSCK Status_LED USEROUT* nADCCS nCS}


#*************************************************************************************************************
# Set Maximum Delay
#*************************************************************************************************************
#

set_max_delay -from _122MHz -to _122MHz 15

set_max_delay -from LTC2208_122MHz -to LTC2208_122MHz 15
set_max_delay -from LTC2208_122MHz -to _122MHz 14
set_max_delay -from LTC2208_122MHz -to PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2] 10

set_max_delay -from PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0] -to PHY_CLK125 16

set_max_delay -from PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1] -to PHY_CLK125 17

set_max_delay -from PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2] -to _122MHz 14


#*************************************************************************************************************
# Set Minimum Delay
#*************************************************************************************************************
#

set_min_delay -from _122MHz -to _122MHz -2

set_min_delay -from LTC2208_122MHz -to LTC2208_122MHz -2

