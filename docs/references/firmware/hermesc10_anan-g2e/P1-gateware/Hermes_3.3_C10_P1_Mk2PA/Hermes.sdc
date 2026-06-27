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
create_clock -period 122.880MHz	-name _122MHz				[get_ports _122MHz]
create_clock -period  10.000MHz	-name OSC_10MHZ			[get_ports OSC_10MHZ]
create_clock -period 125.000MHz	-name PHY_CLK125			[get_ports PHY_CLK125]
create_clock -period  25.000MHz	-name PHY_RX_CLOCK		[get_ports PHY_RX_CLOCK]


derive_pll_clocks

derive_clock_uncertainty


#*************************************************************************************
# Create Generated CloCK
#*************************************************************************************
# internally generated clocks

create_generated_clock -name Hermes_clk_lrclk_gen:clrgen|BCLK 		-source [get_ports LTC2208_122MHz] 	-divide_by 40 	Hermes_clk_lrclk_gen:clrgen|BCLK
create_generated_clock -name spc[2] -source Hermes_clk_lrclk_gen:clrgen|BCLK 	-divide_by 8 	spc[2]
create_generated_clock -name PHY_RX_CLOCK_2 -source PHY_RX_CLOCK -divide_by 2 	PHY_RX_CLOCK_2
create_generated_clock -name {Attenuator:Attenuator_inst|clk_2} -source [get_pins {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0]}] -multiply_by 1 [get_registers {Attenuator:Attenuator_inst|clk_2}]
create_generated_clock -name sidetone:sidetone_inst|sidetone_clock -source PHY_CLK125 -divide_by 690 	sidetone:sidetone_inst|sidetone_clock
create_generated_clock -name pro_count[2] -source [get_pins {PLL_IF_inst|altpll_component|auto_generated|pll1|clk[3]}] 	-divide_by 8 	pro_count[2]

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
					Hermes_clk_lrclk_gen:clrgen|BCLK \
					spc[2] \
					PLL_IF_inst|altpll_component|auto_generated|pll1|clk[1] \
					PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2] \
					PLL_inst|altpll_component|auto_generated|pll1|clk[1] \
					pro_count[2] \
					sidetone:sidetone_inst|sidetone_clock \
					} \
				-group {OSC_10MHZ \
				PLL2_inst|altpll_component|auto_generated|pll1|clk[0]} \
				-group	{PLL_IF_inst|altpll_component|auto_generated|pll1|clk[0] \
				Attenuator:Attenuator_inst|clk_2}
										 

#*************************************************************************************************************
# set input delay
#*************************************************************************************************************
##
# was 2.000 max 0.000 min
set_input_delay -add_delay -max -clock PHY_CLK125 1.500 {PHY_MDIO PHY_RX[*] RX_DV PHY_INT_N ASMI_interface:ASMI_int_inst|ASMI:ASMI_inst|ASMI_altasmi_parallel_cv82:ASMI_altasmi_parallel_cv82_component|cyclone_asmiblock2~ALTERA_DATA0 }
set_input_delay -add_delay -min -clock PHY_CLK125 -0.500 {PHY_MDIO PHY_RX[*] RX_DV PHY_INT_N ASMI_interface:ASMI_int_inst|ASMI:ASMI_inst|ASMI_altasmi_parallel_cv82:ASMI_altasmi_parallel_cv82_component|cyclone_asmiblock2~ALTERA_DATA0 }

set_input_delay 1.000  -clock LTC2208_122MHz {ADCMISO CDOUT INA[*] IO4 IO5 IO6 IO8 OVERFLOW SO SPI_SDI}

#*************************************************************************************************************
# set output delay
#*************************************************************************************************************
# was 1.500 max -0.500 min
set_output_delay -add_delay -max -clock PHY_CLK125 1.500 {PHY_MDIO PHY_TX[*] PHY_TX_EN PHY_TX_CLOCK PHY_MDC ASMI_interface:ASMI_int_inst|ASMI:ASMI_inst|ASMI_altasmi_parallel_cv82:ASMI_altasmi_parallel_cv82_component|cyclone_asmiblock2~ALTERA_SCE ASMI_interface:ASMI_int_inst|ASMI:ASMI_inst|ASMI_altasmi_parallel_cv82:ASMI_altasmi_parallel_cv82_component|cyclone_asmiblock2~ALTERA_SDO ASMI_interface:ASMI_int_inst|ASMI:ASMI_inst|ASMI_altasmi_parallel_cv82:ASMI_altasmi_parallel_cv82_component|cyclone_asmiblock2~ALTERA_DCLK}
set_output_delay -add_delay -min -clock PHY_CLK125 -0.500 {PHY_MDIO PHY_TX[*] PHY_TX_EN PHY_TX_CLOCK PHY_MDC ASMI_interface:ASMI_int_inst|ASMI:ASMI_inst|ASMI_altasmi_parallel_cv82:ASMI_altasmi_parallel_cv82_component|cyclone_asmiblock2~ALTERA_SCE ASMI_interface:ASMI_int_inst|ASMI:ASMI_inst|ASMI_altasmi_parallel_cv82:ASMI_altasmi_parallel_cv82_component|cyclone_asmiblock2~ALTERA_SDO ASMI_interface:ASMI_int_inst|ASMI:ASMI_inst|ASMI_altasmi_parallel_cv82:ASMI_altasmi_parallel_cv82_component|cyclone_asmiblock2~ALTERA_DCLK}

# Rx appears to work, but Tx Ps works only when Hermes board is not warm 
set_output_delay 1.000 -clock _122MHz {PWM_out DACD[*] DAC_ALC ADCCLK ADCMOSI ATTN_CLK ATTN_DATA  ATTN_LE  CBCLK CDIN CLRCIN CLRCOUT CMCLK CS  FPGA_PLL  J15_5 J15_6  MOSI SCK SI SPI_SCK SPI_SDO SSCK  nADCCS nCS}


#*************************************************************************************************************
# Set Maximum Delay
#*************************************************************************************************************
#

set_max_delay -from _122MHz -to _122MHz 14

set_max_delay -from Hermes_clk_lrclk_gen:clrgen|BCLK -to _122MHz 14

set_max_delay -from LTC2208_122MHz -to LTC2208_122MHz 14
set_max_delay -from LTC2208_122MHz -to _122MHz 12
set_max_delay -from LTC2208_122MHz -to PLL_inst|altpll_component|auto_generated|pll1|clk[1] 10

set_max_delay -from PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[0] -to PHY_CLK125 15

set_max_delay -from PLL_clocks_inst|altpll_component|auto_generated|pll1|clk[1] -to PHY_CLK125 16

set_max_delay -from PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2] -to _122MHz 13
set_max_delay -from PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2] -to pro_count[2] 8

set_max_delay -from PLL_inst|altpll_component|auto_generated|pll1|clk[1] -to _122MHz 10
set_max_delay -from PLL_inst|altpll_component|auto_generated|pll1|clk[1] -to PLL_inst|altpll_component|auto_generated|pll1|clk[1] 6

set_max_delay -from pro_count[2] -to sidetone:sidetone_inst|sidetone_clock 6
set_max_delay -from pro_count[2] -to PLL_IF_inst|altpll_component|auto_generated|pll1|clk[2] 10
set_max_delay -from pro_count[2] -to _122MHz 14
set_max_delay -from pro_count[2] -to LTC2208_122MHz 9

set_max_delay -from sidetone:sidetone_inst|sidetone_clock -to LTC2208_122MHz 14

set_max_delay -from spc[2] -to _122MHz 23
set_max_delay -from LTC2208_122MHz -to spc[2] 9


#*************************************************************************************************************
# Set Minimum Delay
#*************************************************************************************************************
#

set_min_delay -from _122MHz -to _122MHz -2

set_min_delay -from LTC2208_122MHz -to LTC2208_122MHz -1

set_min_delay -from sidetone:sidetone_inst|sidetone_clock -to LTC2208_122MHz -5


#*************************************************************************************************************
# Set False Path
#*************************************************************************************************************
# don't need fast paths to the LEDs and adhoc outputs so set false paths so Timing will be ignored
set_false_path -from * -to { Status_LED DEBUG_LED* DITH FPGA_PTT  NCONFIG  RAND USEROUT* }

#don't need fast paths from the following inputs
set_false_path -from  {ANT_TUNE IO*  KEY_DASH KEY_DOT OVERFLOW PTT MODE2} -to *
