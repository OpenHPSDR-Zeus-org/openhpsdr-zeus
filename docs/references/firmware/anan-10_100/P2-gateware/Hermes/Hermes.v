/***********************************************************
*
*	Hermes - new Protocol 
*
************************************************************/


//
//  HPSDR - High Performance Software Defined Radio
//
//  Hermes code. 
//
//  This program is free software; you can redistribute it and/or modify
//  it under the terms of the GNU General Public License as published by
//  the Free Software Foundation; either version 2 of the License, or
//  (at your option) any later version.
//
//  This program is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//  GNU General Public License for more details.
//
//  You should have received a copy of the GNU General Public License
//  along with this program; if not, write to the Free Software
//  Foundation, Inc., 59 Temple Place, Suite 330, Boston, MA  02111-1307  USA

// (C) Phil Harman VK6APH/VK6PH, Kirk Weedman KD7IRS  2006, 2007, 2008, 2009, 2010, 2011, 2012, 2013, 2014, 2015 


/*
					You can get rid of the annoying critical warnings on the Data[1]/ASDO, FLASH_nCE/nCSO, DCLK and Data[0] pins by going into the <Device and Pin Options> 
					dialog (under <Assignments><Device>) and changing those four pins to "Use as regular I/O".
					Cyclone II devices required them to be set here, but in Cyclone III and later, they are used directly by the ASMI module.
						
				 
					**** IMPORTANT: Prevent Quartus merging PLLs! *****
*/

module Hermes(
	//clock PLL
  input _122MHz,                 //122.88MHz from VCXO
  input  OSC_10MHZ,              //10MHz reference in 
  output FPGA_PLL,               //122.88MHz VCXO contol voltage

  //attenuator (DAT-31-SP+)
  output ATTN_DATA,              //data for input attenuator
  output ATTN_CLK,               //clock for input attenuator
  output ATTN_LE,                //Latch enable for input attenuator

  //rx adc (LTC2208)
  input  [15:0]INA,              //samples from LTC2208
  input  LTC2208_122MHz,         //122.88MHz from LTC2208_122MHz pin 
  input  OVERFLOW,               //high indicates LTC2208 has overflow
  output RAND,            			//high turns ramdom on
  output PGA,            			//high turns LTC2208 internal preamp on
  output DITH,            			//high turns LTC2208 dither on 
  output SHDN,            			//x shuts LTC2208 off

  //tx adc (AD9744ARU)
  output reg  DAC_ALC,           //sets Tx DAC output level
  output reg signed [13:0]DACD,  //Tx DAC data bus
  
  //audio codec (TLV320AIC23B)
  output CBCLK,               
  output CLRCIN, 
  output CLRCOUT,
  output CDIN,                   
  output CMCLK,                  //Master Clock to TLV320 
  output CMODE,                  //sets TLV320 mode - I2C or SPI
  output nCS,                    //chip select on TLV320
  output MOSI,                   //SPI data for TLV320
  output SSCK,                   //SPI clock for TLV320
  input  CDOUT,                  //Mic data from TLV320  
  
  //phy rgmii (KSZ9021RL)
  output [3:0]PHY_TX,
  output PHY_TX_EN,              //PHY Tx enable
  output PHY_TX_CLOCK,           //PHY Tx data clock
  input  [3:0]PHY_RX,     
  input  RX_DV,                 //PHY has data flag
  input  PHY_RX_CLOCK,           //PHY Rx data clock
  input  PHY_CLK125,             //125MHz clock from PHY PLL
  input  PHY_INT_N,              //interrupt (n.c.)
  input PHY_RESET_N,
  input  CLK_25MHZ,              //25MHz clock (n.c.)  
  
	//phy mdio (KSZ9021RL)
	inout  PHY_MDIO,               //data line to PHY MDIO
	output PHY_MDC,                //2.5MHz clock to PHY MDIO
  
	//eeprom (25AA02E48T-I/OT)
	output 	SCK, 							// clock on MAC EEPROM
	output 	SI,							// serial in on MAC EEPROM
	input   	SO, 							// SO on MAC EEPROM
	output  	CS,							// CS on MAC EEPROM
	
  //eeprom (M25P16VMW6G)  
  output NCONFIG,                //when high causes FPGA to reload from eeprom EPCS16	
  
  //12 bit adc's (ADC78H90CIMT)
  output ADCMOSI,                
  output ADCCLK,
  input  ADCMISO,
  output nADCCS, 
 
  //alex/apollo spi
  output SPI_SDO,                //SPI data to Alex or Apollo 
//  input  SPI_SDI,                //SPI data from Apollo 
  output SPI_SCK,                //SPI clock to Alex or Apollo 
  output J15_5,                  //SPI Rx data load strobe to Alex / Apollo enable
  output J15_6,                  //SPI Tx data load strobe to Alex / Apollo ~reset 
  
  //misc. i/o
  input  PTT,                    //PTT active low
  input  KEY_DOT,                //dot input from J11
  input  KEY_DASH,               //dash input from J11
  output FPGA_PTT,               //high turns Q4 on for PTTOUT
  input  MODE2,                  //jumper J13 on Hermes, 1 if removed
  input  ANT_TUNE,               //atu
  output IO1,                    //high to mute AF amp    
  input  IO2,                    //PTT, used by Apollo 
  
  //user digital inputs
  input  IO4,                    
  input  IO5,
  input  IO6,
  input  IO8,
  
  //user outputs
  output USEROUT0,               
  output USEROUT1,
  output USEROUT2,
  output USEROUT3,
  output USEROUT4,
  output USEROUT5,
  output USEROUT6,
  
    //debug led's
  output Status_LED,      
  output DEBUG_LED1,             
  output DEBUG_LED2,
  output DEBUG_LED3,
  output DEBUG_LED4,
  output DEBUG_LED5,
  output DEBUG_LED6,
  output DEBUG_LED7,
  output DEBUG_LED8,
  output DEBUG_LED9,
  output DEBUG_LED10  
);

assign USEROUT0 = run ? Open_Collector[1] : 1'b0;					
assign USEROUT1 = run ? Open_Collector[2] : 1'b0;   				
assign USEROUT2 = run ? Open_Collector[3] : 1'b0;  					
assign USEROUT3 = run ? Open_Collector[4] : 1'b0;  		
assign USEROUT4 = run ? Open_Collector[5] : 1'b0; 
assign USEROUT5 = run ? Open_Collector[6] : 1'b0; 
assign USEROUT6 = run ? Open_Collector[7] : 1'b0; 

assign PGA = 0;								// 1 = gain of 3dB, 0 = gain of 0dB
assign SHDN = 1'b0;				   		// normal LTC2208 operation

assign NCONFIG = IP_write_done || reset_FPGA;

wire speed = 1'b1; // high for 1000T
// enable AF Amp
assign  IO1 = 1'b0;  						// low to enable, high to mute

localparam NR = 10;			// number of receivers to implement
localparam master_clock = 122880000; 	// DSP  master clock in Hz.

parameter M_TPD   = 4;
parameter IF_TPD  = 2;

localparam board_type = 8'h01;		  	// 00 for Metis, 01 for Hermes, 02 for ANAN-10E, 03 for Angelia, and 05 for Orion
parameter  Hermes_version = 8'd109;	// FPGA code version
parameter  beta_version = 8'd1;	// Should be 0 for official release
parameter  protocol_version = 8'd39;	// openHPSDR protocol version implemented

//--------------------------------------------------------------
// Reset Lines - C122_rst, IF_rst, SPI_Alex_reset
//--------------------------------------------------------------

wire  IF_rst;
wire SPI_Alex_rst;
wire C122_rst;
//wire SPI_clk;
	
assign IF_rst = network_state;  // hold code in reset until Ethernet code is running.


// transfer IF_rst to 122.88MHz clock domain to generate C122_rst
cdc_sync #(1)
	reset_C122 (.siga(IF_rst), .rstb(0), .clkb(C122_clk), .sigb(C122_rst)); // 122.88MHz clock domain reset

// PHY_RESET_N will go high after ~100ms due to RC, use to create Alex reset pulse
pulsegen reset_Alex  (.sig(PHY_RESET_N), .rst(0), .clk(CBCLK), .pulse(SPI_Alex_rst));
	
// Deadman timer - clears run if HW_timer_enable and no C&C commands received for ~2 seconds.
wire timer_reset = (HW_reset1 | HW_reset2 | HW_reset3 | HW_reset4);

reg [27:0] sec_count;
wire HW_timeout;
always @ (posedge rx_clock)
begin
	if (HW_timer_enable) begin
		if (timer_reset) sec_count <= 28'b0;
		else if (sec_count < 28'd250_000_000) 	// approx 2 secs. 
			sec_count <= sec_count + 28'b1;
	end
	else sec_count <= 28'd0;
end

assign HW_timeout = (sec_count >= 28'd250_000_000) ? 1'd1 : 1'd0;

//---------------------------------------------------------
//		CLOCKS
//---------------------------------------------------------

wire C122_clk = LTC2208_122MHz;
wire CLRCLK;
assign CLRCIN  = CLRCLK;
assign CLRCOUT = CLRCLK;


wire 	IF_locked;
wire _122_90;
wire C122_div2_clk; 

// Generate CMCLK (12.288MHz), CBCLK(3.072MHz) and CLRCLK (48kHz) from 122.88MHz using PLL
// NOTE: CBCLK is generated at 180 degress so that LRCLK occurs on negative edge of BCLK 
//PLL_IF PLL_IF_inst (.inclk0(_122MHz), .c0(CMCLK), .c1(CBCLK), .c2(CLRCLK),  .c3(_122_90), .locked(IF_locked));
PLL_IF PLL_IF_inst (.inclk0(C122_clk), .c0(C122_div2_clk), .c1(CMCLK), .c2(CBCLK), .c3(CLRCLK), .locked(IF_locked));


//-----------------------------------------------------------------------------
//                           network module
//-----------------------------------------------------------------------------
wire network_state;
wire speed_1Gbit;
wire clock_12_5MHz;
wire [7:0] network_status;
wire rx_clock;
wire tx_clock;
wire udp_rx_active;
wire [7:0] udp_rx_data;
wire udp_tx_active;
wire [47:0] local_mac;	
wire broadcast;
wire [15:0] udp_tx_length;
wire [7:0] udp_tx_data;
wire udp_tx_request;
wire udp_tx_enable;
wire set_ip;
wire IP_write_done;	
wire static_ip_assigned;
wire dhcp_timeout;
wire dhcp_success;
wire icmp_rx_enable;

network network_inst (

	// inputs
  .speed(speed),	
  .udp_tx_request(udp_tx_request),
  .udp_tx_data(udp_tx_data),  
  .set_ip(set_ip),
  .assign_ip(assign_ip),
  .port_ID(port_ID), 
  
  // outputs
  .clock_12_5MHz(clock_12_5MHz),
  .rx_clock(rx_clock),
  .tx_clock(tx_clock),
  .broadcast(broadcast),
  .udp_rx_active(udp_rx_active),
  .udp_rx_data(udp_rx_data),
  .udp_tx_length(udp_tx_length),
  .udp_tx_active(udp_tx_active),
  .local_mac(local_mac),
  .udp_tx_enable(udp_tx_enable), 
  .IP_write_done(IP_write_done),
  .icmp_rx_enable(icmp_rx_enable),   // test for ping bug
  .to_port(to_port),   					// UDP port the PC is sending to

	// status outputs
  .speed_1Gbit(speed_1Gbit),	
  .network_state(network_state),	
  .network_status(network_status),
  .static_ip_assigned(static_ip_assigned),
  .dhcp_timeout(dhcp_timeout),
  .dhcp_success(dhcp_success),

  //make hardware pins available inside this module
  .MODE2(1'b1),
  .PHY_TX(PHY_TX),
  .PHY_TX_EN(PHY_TX_EN),            
  .PHY_TX_CLOCK(PHY_TX_CLOCK),         
  .PHY_RX(PHY_RX),     
  .PHY_DV(RX_DV),    					// use PHY_DV to be consistent with Metis            
  .PHY_RX_CLOCK(PHY_RX_CLOCK),         
  .PHY_CLK125(PHY_CLK125),           
  .PHY_MDIO(PHY_MDIO),             
  .PHY_MDC(PHY_MDC),
  .SCK(SCK),                  
  .SI(SI),                   
  .SO(SO), 				
  .CS(CS)
  );


//-----------------------------------------------------------------------------
//                          sdr receive
//-----------------------------------------------------------------------------
wire sending_sync;
wire discovery_reply;
wire pc_send;
wire debug;
wire seq_error;
wire erase_ACK;
wire EPCS_FIFO_enable;
wire erase;	
wire send_more;
wire send_more_ACK;
wire set_up;
wire [31:0] assign_ip;
wire [15:0]to_port;
wire [31:0] PC_seq_number;				// sequence number sent by PC when programming
wire discovery_ACK;
wire discovery_ACK_sync;


sdr_receive sdr_receive_inst(
	//inputs 
	.rx_clock(rx_clock),
	.udp_rx_data(udp_rx_data),
	.udp_rx_active(udp_rx_active),
	.sending_sync(sending_sync),
	.broadcast(broadcast),
	.erase_ACK(busy),						// busy is set when erase command is active in ASMI_interface
	.EPCS_wrused(EPCS_wrused),
	.local_mac(local_mac),
	.to_port(to_port),
	.discovery_ACK(discovery_ACK_sync),	// set when discovery reply request received by sdr_send
	
	//outputs
	.discovery_reply(discovery_reply),
	.seq_error(seq_error),
	.erase(erase),
	.num_blocks(num_blocks),
	.EPCS_FIFO_enable(EPCS_FIFO_enable),
	.set_ip(set_ip),
	.assign_ip(assign_ip),
	.sequence_number(PC_seq_number)
	);
			        


//-----------------------------------------------------------------------------
//                               sdr rx, tx & IF clock domain transfers
//-----------------------------------------------------------------------------
wire run_sync;
wire wideband_sync;
wire discovery_reply_sync;

// transfer tx clock domain signals to rx clock domain
sync sync_inst1(.clock(rx_clock), .sig_in(udp_tx_active), .sig_out(sending_sync));   
sync sync_inst2(.clock(rx_clock), .sig_in(discovery_ACK), .sig_out(discovery_ACK_sync));

// transfer rx clock domain signals to tx clock domain  
sync sync_inst5(.clock(tx_clock), .sig_in(discovery_reply), .sig_out(discovery_reply_sync)); 
sync sync_inst6(.clock(tx_clock), .sig_in(run), .sig_out(run_sync)); 
sync sync_inst7(.clock(tx_clock), .sig_in(wideband), .sig_out(wideband_sync));


//-----------------------------------------------------------------------------
//                          sdr send
//-----------------------------------------------------------------------------

wire [7:0] port_ID;
wire [7:0]Mic_data;
wire mic_fifo_rdreq;
wire [8:0]Rx_data[0:NR-1];
wire fifo_ready[0:NR-1];
wire fifo_rdreq[0:NR-1];
logic [15:0] checksum;

sdr_send #(board_type, NR, master_clock, protocol_version) sdr_send_inst(
	//inputs
	.tx_clock(tx_clock),
	.udp_tx_active(udp_tx_active),
	.discovery(discovery_reply_sync),
	.run(run_sync),
	.wideband(wideband_sync),
	.sp_data_ready(sp_data_ready),
	.sp_fifo_rddata(sp_fifo_rddata),		// **** why the odd name - use spectrum_data ?
	.local_mac(local_mac),
	.code_version(Hermes_version),
	.beta_version(beta_version),
	.Rx_data(Rx_data),						// Rx I&Q data to send to PHY
	.udp_tx_enable(udp_tx_enable),
	.erase_done(erase_done | erase),    // send ACK when erase command received and when erase complete
	.send_more(send_more),
	.Mic_data(Mic_data),						// mic data to send to PHY
	.fifo_ready(fifo_ready),				// data available in Rx fifo
	.mic_fifo_ready(mic_fifo_ready),		// data avaiable in mic fifo
	.CC_data_ready(CC_data_ready),      // C&C data availble 
	.CC_data(CC_data),
	.sequence_number(PC_seq_number),		// sequence number to send when programming and requesting more data
	.Wideband_packets_per_frame(Wideband_packets_per_frame),  
	.checksum(checksum),  
	
	//outputs
	.udp_tx_data(udp_tx_data),
	.udp_tx_length(udp_tx_length),
	.udp_tx_request(udp_tx_request),
	.fifo_rdreq(fifo_rdreq),				// high to indicate read from Rx fifo required
	.sp_fifo_rdreq	(sp_fifo_rdreq	),		// high to indicate read from spectrum fifo required
	.erase_done_ACK(erase_done_ACK),		
	.send_more_ACK(send_more_ACK),
	.port_ID(port_ID),
	.mic_fifo_rdreq(mic_fifo_rdreq),		// high to indicate read from mic fifo required
	.CC_ack(CC_ack),							// ack to CC_encoder that send request received
	.WB_ack(WB_ack),							// ack to WB controller that send request received	
	.phy_ready(phy_ready),					// set when PHY is not sending DDC data
	.discovery_ACK(discovery_ACK) 		// set to acknowlege discovery reply received
	 ); 		

//---------------------------------------------------------
// 		Set up TLV320 using SPI 
//---------------------------------------------------------


TLV320_SPI TLV (.clk(CMCLK), .CMODE(CMODE), .nCS(nCS), .MOSI(MOSI), .SSCK(SSCK), .boost(Mic_boost), .line(Line_In), .line_in_gain(Line_In_Gain));


//------------------------------------------------------------------------
//   Rx(n)_fifo  (2k Bytes) Dual clock FIFO - Altera Megafunction (dcfifo)
//------------------------------------------------------------------------

/*
	  
						   +-------------------+
     Rx(n)_fifo_data                               |data[7:0]     wrful| Rx(n)_fifo_full
						   |                   |
     Rx(n)_fifo_wreq                               |wreq               | 
						   |                   |
           C122_clk                                |>wrclk  wrused[9:0]| 
                                                   +-------------------+
     fifo_rdreq[n]                                 |rdreq        q[7:0]| Rx_data[n]
                                                   |                   |
        tx_clock                                   |>rdclk     rdempty | Rx_fifo_empty[n]
                                                   |                   |
						   |      rdusedw[10:0]| Rx(n)_used  (0 to 2047 bytes)
						   +-------------------+
                                                   |                   |
   Rx_fifo_clr[n] OR                               |aclr               |
         IF_rst	OR !run                            +-------------------+
         OR fifo_clear
		
    

*/

wire        Rx_fifo_wreq[0:NR-1];
wire  [7:0] Rx_fifo_data[0:NR-1];
wire        Rx_fifo_full[0:NR-1];
wire [11:0] Rx_used[0:NR-1];
wire        Rx_fifo_clr[0:NR-1];
wire        Rx_fifo_empty[0:NR-1];
wire        write_enable;
wire        phy_ready;
wire        C122_run;


// move flags into correct clock domains
cdc_sync #(1) C122_run_sync  (.siga(run), .rstb(C122_rst), .clkb(C122_div2_clk), .sigb(C122_run));
cdc_sync #(16) C122_EnableRx0_15_sync  (.siga(EnableRx0_15), .rstb(C122_rst), .clkb(C122_div2_clk), .sigb(C122_EnableRx0_15));

generate
genvar d;

for (d = 0 ; d < NR; d++)
	begin:p
		Rx_fifo RxX_fifo_inst(.wrclk (C122_div2_clk),.rdreq (fifo_rdreq[d]),.rdclk (tx_clock),.wrreq (Rx_fifo_wreq[d]), .rdempty(Rx_fifo_empty[d]),
							 .data (Rx_fifo_data[d]), .q (Rx_data[d]), .wrfull(Rx_fifo_full[d]),
							 .rdusedw(Rx_used[d]), .aclr (IF_rst | Rx_fifo_clr[d] | !C122_run));

		// Convert 48 bit Rx I&Q data (24bit I, 24 bit Q) into 8 bits to feed Tx FIFO. Only run if EnableRx0_15[x] is set.
		// If Sync[n] enabled then select the data from the receiver to be synchronised.
		// Do this by using C122_SyncRx(n) to select the required receiver I & Q data.

		Rx_fifo_ctrl #(NR) RxX_fifo_ctrl_inst( .reset(!C122_run || !C122_EnableRx0_15[d]), .clock(C122_div2_clk),
							.spd_rdy(strobe[d]), .fifo_full(Rx_fifo_full[d]),
							.wrenable(Rx_fifo_wreq[d]), .data_out(Rx_fifo_data[d]), .fifo_clear(Rx_fifo_clr[d]),
							.Sync_data_in_I(rx_I[d]), .Sync_data_in_Q(rx_Q[d]));

		always @ (posedge tx_clock)    
			fifo_ready[d] = (Rx_used[d] > 12'd1427) ? 1'b1 : 1'b0;  // used to signal that fifo has enough data to send to PC
	end
endgenerate

//------------------------------------------------------------------------
//   Mic_fifo  (1024 words) Dual clock FIFO - Altera Megafunction (dcfifo)
//------------------------------------------------------------------------

/*
						   +-------------------+
         mic_data 	|data[15:0]	  wrfull| 
						   |				        |
		mic_data_ready	|wrreq		        |
						   |					     |
				 CBCLK	|>wrclk	           | 
						   +-------------------+
   mic_fifo_rdreq		|rdreq		  q[7:0]| Mic_data
						   |					     |
	     tx_clock		|>rdclk		        | 
						   |		 rdusedw[11:0]| mic_rdused* (0 to 2047 bytes)
						   +-------------------+
			            |                   |
	         !run  	|aclr               |
				         +-------------------+
							
		* additional bit added so not zero when full.
		LSByte of input data is output first
	
*/

wire [11:0]	mic_rdused; 
							  
Mic_fifo Mic_fifo_inst(.wrclk (CBCLK),.rdreq (mic_fifo_rdreq),.rdclk (tx_clock),.wrreq (mic_data_ready), 
							  .data ({mic_data[7:0], mic_data[15:8]}), .q (Mic_data), .wrfull(),
                       .rdusedw(mic_rdused), .aclr(!run)); 

wire mic_fifo_ready = mic_rdused > 12'd131 ? 1'b1 : 1'b0;		// used to indicate that fifo has enough data to send to PC.					  
							  
//----------------------------------------------
//		Get mic data from  TLV320 in I2S format 
//---------------------------------------------- 

wire [15:0] mic_data;
wire mic_data_ready;

mic_I2S mic_I2S_inst (.clock(CBCLK), .CLRCLK(CLRCLK), .in(CDOUT), .mic_data(mic_data), .ready(mic_data_ready));


//------------------------------------------------
//   SP_fifo  (16384 words) dual clock FIFO
//------------------------------------------------

/*
        The spectrum data FIFO is 16 by 16384 words long on the input.
        Output is in Bytes for easy interface to the PHY code
        NB: The output flags are only valid after a read/write clock has taken place

       
							   SP_fifo
						+--------------------+
  Wideband_source |data[15:0]	   wrfull| sp_fifo_wrfull
						|				         |
	sp_fifo_wrreq	|wrreq	     wrempty| sp_fifo_wrempty
						|				         |
			C122_clk	|>wrclk              | 
						+--------------------+
	sp_fifo_rdreq	|rdreq		   q[7:0]| sp_fifo_rddata
						|                    | 
						|				         |
		 tx_clock	|>rdclk		         | 
						|		               | 
						+--------------------+
						|                    |
	   !wideband   |aclr                |
		      	   |                    |
	    				+--------------------+
		
*/

wire  sp_fifo_rdreq;
wire [7:0]sp_fifo_rddata;
wire sp_fifo_wrempty;
wire sp_fifo_wrfull;
wire sp_fifo_wrreq;


//-----------------------------------------------------------------------------
//   Wideband Spectrum Data 
//-----------------------------------------------------------------------------

//	When sp_fifo_wrempty fill fifo with 'user selected' # words of consecutive ADC samples.
// Pass sp_data_ready to sdr_send to indicate that data is available.
// Reset fifo when !wideband so the data always starts at a known state.
// The time between fifo fills is set by the user (0-255mS). . The number of  samples sent per UDP frame is set by the user
// (default to 1024) as is the sample size (defaults to 16 bits).
// The number of frames sent, per fifo fill, is set by the user - currently set at 8 i.e. 4,096 samples. 


wire have_sp_data;

wire wideband = (Wideband_enable[0] | Wideband_enable[1]);  							// enable Wideband data if either selected
wire [15:0] Wideband_source = temp_ADC;	// select Wideband data source ADC0

SP_fifo  SPF (.aclr(!wideband), .wrclk (C122_div2_clk), .rdclk(tx_clock), 
             .wrreq (sp_fifo_wrreq), .data ({Wideband_source[7:0], Wideband_source[15:8]}), .rdreq (sp_fifo_rdreq),
             .q(sp_fifo_rddata), .wrfull(sp_fifo_wrfull), .wrempty(sp_fifo_wrempty)); 	
				 
sp_rcv_ctrl SPC (.clk(C122_div2_clk), .reset(0), .sp_fifo_wrempty(sp_fifo_wrempty),
                 .sp_fifo_wrfull(sp_fifo_wrfull), .write(sp_fifo_wrreq), .have_sp_data(have_sp_data));	
				 
// **** TODO: change number of samples in FIFO (presently 16k) based on user selection **** 


// wire [:0] update_rate = 100T ?  12500 : 125000; // **** TODO: need to change counter target when run at 100T.
wire [17:0] update_rate = 125000;

reg  sp_data_ready;
reg [24:0]wb_counter;
wire WB_ack;

always @ (posedge tx_clock)	
begin
	if (wb_counter == (Wideband_update_rate * update_rate)) begin	  // max delay 255mS
		wb_counter <= 25'd0;
		if (have_sp_data & wideband) sp_data_ready <= 1'b1;	  
	end
	else begin 
			wb_counter <= wb_counter + 25'd1;
			if (WB_ack) sp_data_ready <= 0;  // wait for confirmation that request has been seen
	end
end	


//----------------------------------------------------
//   					Rx_Audio_fifo
//----------------------------------------------------

/*
							  Rx_Audio_fifo (4k) 
							
								+--------------------+
				 audio_data |data[31:0]	  wrfull | Audio_full
								|				         |
	Rx_Audio_fifo_wrreq	|wrreq				   |
								|					      |									    
				 rx_clock	|>wrclk	 		      |
								+--------------------+								
	  get_audio_samples  |rdreq		  q[31:0]| LR_data 
								|					      |					  			
								|   		            | 
								|            rdempty | Audio_empty 							
				    CBCLK	|>rdclk              |    
								+--------------------+								
								|                    |
		  !run OR IF_rst  |aclr                |								
								+--------------------+	
								
	Only request audio samples if fifo not empty 						
*/

wire Rx_Audio_fifo_wrreq;
wire  [31:0] temp_LR_data;
wire  [31:0] LR_data;
wire get_audio_samples;  // request audio samples at 48ksps
wire Audio_full;
wire Audio_empty;
wire get_samples;
wire [31:0]audio_data;
reg [11:0]Rx_Audio_Used;

Rx_Audio_fifo Rx_Audio_fifo_inst(.wrclk (rx_clock),.rdreq (get_audio_samples),.rdclk (CBCLK),.wrreq(Rx_Audio_fifo_wrreq), 
			.rdusedw(Rx_Audio_Used), .data (audio_data),.q (LR_data), .aclr(IF_rst | !run), .wrfull(Audio_full), .rdempty(Audio_empty));
					 
// Manage Rx Audio data to feed to Audio FIFO  - parameter is port #
byte_to_32bits #(1028) Audio_byte_to_32bits_inst
			(.clock(rx_clock), .run(run), .udp_rx_active(udp_rx_active), .udp_rx_data(udp_rx_data), .to_port(to_port),
			 .fifo_wrreq(Rx_Audio_fifo_wrreq), .data_out(audio_data), .sequence_errors(Audio_sequence_errors), .full(Audio_full));

// send receiver audio to TLV320 in I2S format, swap L&R
audio_I2S audio_I2S_inst (.run(run), .empty(Audio_empty), .BCLK(CBCLK), .rdusedw(Rx_Audio_Used), .LRCLK(CLRCLK), .data_in({LR_data[15:0], LR_data[31:16]}), .data_out(CDIN), .get_data(get_audio_samples)); 

//----------------------------------------------------
//   					Tx1_IQ_fifo
//----------------------------------------------------

/*
							   Tx1_IQ_fifo (4k) 
							
								+--------------------+
			 Tx1_IQ_data   |data[47:0]	         | 
								|				         |
			Tx1_fifo_wrreq |wrreq  wrusedw[11:0]|	write_used[11:0]	
								|					      |									    
				 rx_clock	|>wrclk	 		      |
								+--------------------+								
	               req1  |rdreq		  q[47:0]| C122_IQ1_data
								|					      |					  			
								|   		            | 
								|                    | 							
				  _122MHz	|>rdclk              | 	    
								+--------------------+								
								|                    |
		  !run | IF_rst   |aclr                |								
								+--------------------+	
								
*/

wire Tx1_fifo_wrreq;
wire [47:0]C122_IQ1_data;
wire [47:0]Tx1_IQ_data;
wire [12:0]write_used;

Tx1_IQ_fifo Tx1_IQ_fifo_inst(.wrclk (rx_clock),.rdreq (req1),.rdclk (C122_clk),.wrreq(Tx1_fifo_wrreq), 
					 .data (Tx1_IQ_data), .q(C122_IQ1_data), .aclr(!run | IF_rst), .wrusedw(write_used));
					 
// Manage Tx I&Q data to feed to Tx  - parameter is port #
byte_to_48bits #(1029) IQ_byte_to_48bits_inst
			(.clock(rx_clock), .run(run), .udp_rx_active(udp_rx_active), .udp_rx_data(udp_rx_data), .to_port(to_port),
			 .fifo_wrreq(Tx1_fifo_wrreq), .data_out(Tx1_IQ_data), .full(1'b0), .sequence_error());					 

// Ensure I&Q data is zero if not trasmitting
wire [47:0] IQ_Tx_data = FPGA_PTT ? C122_IQ1_data : 48'b0; 													

// indicate how full or empty the FIFO is - was required by Simon G4ELI code but no longer required. 
//wire almost_full 	= (write_used > 13'd3584) ? 1'b1 : 1'b0; //(write_used[11:8] == 4'b1111) ? 1'b1 : 1'b0;  // >= 3,840 samples
//wire almost_empty = (write_used < 13'd512)  ? 1'b1 : 1'b0; //(write_used[11:9] == 4'b0001) ? 1'b1 : 1'b0;  // <= 511 samples

													
//--------------------------------------------------------------------------
//			EPCS16 Erase and Program code 
//--------------------------------------------------------------------------

/*
					    EPCS_fifo (1k bytes) 
					
					    +-------------------+
	  udp_rx_data   |data[7:0]	         | 
					    |				         |
 EPCS_FIFO_enable  |wrreq		         | 
					    |					      |									    
	    rx_clock	 |>wrclk wrusedw[9:0]| EPCS_wrused
					    +-------------------+								
	   EPCS_rdreq   |rdreq		  q[7:0] | EPCS_data
					    |					      |					  			
			     	    |   		            |  
			          |                   | 							
     clock_12_5MHz |>rdclk rdusedw[9:0]| EPCS_Rx_used	    
					    +-------------------+								
					    |                   |
			  IF_rst  |aclr               |								
					    +-------------------+						
*/

wire [7:0]EPCS_data;
wire [9:0]EPCS_Rx_used;
wire  EPCS_rdreq;
wire [31:0] num_blocks;  
wire EPCS_full;
wire [9:0] EPCS_wrused;


EPCS_fifo EPCS_fifo_inst(.wrclk (rx_clock),.rdreq (EPCS_rdreq),.rdclk (clock_12_5MHz),.wrreq(EPCS_FIFO_enable),  
                .data (udp_rx_data),.q (EPCS_data), .rdusedw(EPCS_Rx_used), .aclr(IF_rst), .wrusedw(EPCS_wrused));

//----------------------------
// 			ASMI Interface
//----------------------------
wire busy;				 // drives LED
wire erase_done;
wire erase_done_ACK;
wire reset_FPGA;

ASMI_interface  ASMI_int_inst(.clock(clock_12_5MHz), .busy(busy), .erase(erase), .erase_ACK(erase_ACK), .IF_PHY_data(EPCS_data),
					 .IF_Rx_used(EPCS_Rx_used), .rdreq(EPCS_rdreq), .erase_done(erase_done), .num_blocks(num_blocks), .checksum(checksum),
					 .send_more(send_more), .send_more_ACK(send_more_ACK), .erase_done_ACK(erase_done_ACK), .NCONFIG(reset_FPGA)); 

//-------------------------------------------------------
//		De-ramdomizer
//--------------------------------------------------------- 

/*

 A Digital Output Randomizer is fitted to the LTC2208. This complements bits 15 to 1 if 
 bit 0 is 1. This helps to reduce any pickup by the A/D input of the digital outputs. 
 We need to de-ramdomize the LTC2208 data if this is turned on. 
 
*/

reg [15:0]temp_ADC;

always @ (posedge C122_div2_clk) 
begin 
//  temp_DACD <= {DACD, 2'b00}; // make DACD 16-bits, use high bits for DACD
   if (RAND) begin	// RAND set so de-ramdomize
		if (INA[0]) temp_ADC <= {~INA[15:1],INA[0]};
		else temp_ADC <= INA;
	end
   else temp_ADC <= INA;  // not set so just copy data	 
end 



//------------------------------------------------------------------------------
//                 All DSP code is in the Receiver module
//------------------------------------------------------------------------------

wire      [31:0] C122_frequency_HZ [0:9];   // frequency control bits for CORDIC
wire      [23:0] rx_I [0:NR];
wire      [23:0] rx_Q [0:NR];
wire             strobe [0:NR];
wire      [15:0] C122_SampleRate[0:NR-1]; 
//wire       [7:0] C122_SyncRx[0:NR-1];

localparam CICRATE = 6'd10;
localparam GBITS = 30;
localparam RRRR = 160;

// Decimation rates
localparam RATE48  =  6'd16;
localparam RATE96  =  RATE48  >> 1;
localparam RATE192 =  RATE96  >> 1;
localparam RATE384 =  RATE192 >> 1;

localparam CALCTYPE = (NR > 5) ? 0 : 3;

logic signed [17:0]   mixdata_i [0:9];
logic signed [17:0]   mixdata_q [0:9];

wire [5:0] memrate[0:NR-1];
generate
genvar c;
  for (c = 0; c < NR; c = c + 1) 
   begin: RATE
   assign memrate[c] = RxSampleRate[c]==16'd48 ? RATE48 : RxSampleRate[c]==16'd96 ? RATE96 : RATE192;

   // interpolate freq (phase) to 61.44 Mhz base
   always @ (posedge C122_div2_clk)    
     C122_frequency_HZ[c] = (Rx_frequency[c] << 1) + (Rx_frequency[c] >> 17) + (Rx_frequency[c] >> 18) +
                            (Rx_frequency[c] >> 20) + (Rx_frequency[c] >> 21) + (Rx_frequency[c] >> 23);
   end
endgenerate

  // One receiver minimum
  mix2 #(.CALCTYPE(CALCTYPE)) mix2_0 (
    .clk(C122_div2_clk),
    .clk_2x(C122_clk),
    .rst(C122_rst),
    .phi0(C122_frequency_HZ[0]),
    .phi1(C122_frequency_HZ[2]),
    .adc(temp_ADC),
    .mixdata0_i(mixdata_i[0]),
    .mixdata0_q(mixdata_q[0]),
    .mixdata1_i(mixdata_i[2]),
    .mixdata1_q(mixdata_q[2])
  );

  receiver_nco #(.CICRATE(CICRATE)) receiver_0 (
    .rst_all(C122_rst),
    .clock(C122_div2_clk),
    .clock_2x(C122_clk),
    .rate(memrate[0]),
    .mixdata_I(mixdata_i[0]),
    .mixdata_Q(mixdata_q[0]),
    .out_strobe(strobe[0]),
    .out_data_I(rx_I[0]),
    .out_data_Q(rx_Q[0])
  );

generate

if (NR >= 2) begin: MIX1_3
  mix2 #(.CALCTYPE(CALCTYPE)) mix2_2 (
    .clk(C122_div2_clk),
    .clk_2x(C122_clk),
    .rst(C122_rst),
    .phi0(C122_frequency_HZ[1]),
    .phi1(C122_frequency_HZ[3]),
    .adc(temp_ADC),
    .mixdata0_i(mixdata_i[1]),
    .mixdata0_q(mixdata_q[1]),
    .mixdata1_i(mixdata_i[3]),
    .mixdata1_q(mixdata_q[3])
  );
end

if (NR >= 2) begin: RECEIVER1
  receiver_nco #(.CICRATE(CICRATE)) receiver_1 (
    .rst_all(C122_rst),
    .clock(C122_div2_clk),
    .clock_2x(C122_clk),
    .rate(memrate[1]),
    .mixdata_I(mixdata_i[1]),
    .mixdata_Q(mixdata_q[1]),
    .out_strobe(strobe[1]),
    .out_data_I(rx_I[1]),
    .out_data_Q(rx_Q[1])
  );
end

if (NR >= 3) begin: RECEIVER2
  receiver_nco #(.CICRATE(CICRATE)) receiver_2 (
    .rst_all(C122_rst),
    .clock(C122_div2_clk),
    .clock_2x(C122_clk),
    .rate(memrate[2]),
    .mixdata_I(mixdata_i[2]),
    .mixdata_Q(mixdata_q[2]),
    .out_strobe(strobe[2]),
    .out_data_I(rx_I[2]),
    .out_data_Q(rx_Q[2])
  );
end

if (NR >= 4) begin: RECEIVER3
  receiver_nco #(.CICRATE(CICRATE)) receiver_3 (
    .rst_all(C122_rst),
    .clock(C122_div2_clk),
    .clock_2x(C122_clk),
    .rate(memrate[3]),
    .mixdata_I(mixdata_i[3]),
    .mixdata_Q(mixdata_q[3]),
    .out_strobe(strobe[3]),
    .out_data_I(rx_I[3]),
    .out_data_Q(rx_Q[3])
  );
end

if (NR >= 5) begin: MIX4_5
  // Build double mixer
  mix2 #(.CALCTYPE(CALCTYPE)) mix2_4 (
    .clk(C122_div2_clk),
    .clk_2x(C122_clk),
    .rst(C122_rst),
    .phi0(C122_frequency_HZ[4]),
    .phi1(C122_frequency_HZ[5]),
    .adc(temp_ADC),
    .mixdata0_i(mixdata_i[4]),
    .mixdata0_q(mixdata_q[4]),
    .mixdata1_i(mixdata_i[5]),
    .mixdata1_q(mixdata_q[5])
  );
end

if (NR >= 5) begin: RECEIVER4
  receiver_nco #(.CICRATE(CICRATE)) receiver_4 (
    .rst_all(C122_rst),
    .clock(C122_div2_clk),
    .clock_2x(C122_clk),
    .rate(memrate[4]),
    .mixdata_I(mixdata_i[4]),
    .mixdata_Q(mixdata_q[4]),
    .out_strobe(strobe[4]),
    .out_data_I(rx_I[4]),
    .out_data_Q(rx_Q[4])
  );
end

if (NR >= 6) begin: RECEIVER5
  receiver_nco #(.CICRATE(CICRATE)) receiver_5 (
    .rst_all(C122_rst),
    .clock(C122_div2_clk),
    .clock_2x(C122_clk),
    .rate(memrate[5]),
    .mixdata_I(mixdata_i[5]),
    .mixdata_Q(mixdata_q[5]),
    .out_strobe(strobe[5]),
    .out_data_I(rx_I[5]),
    .out_data_Q(rx_Q[5])
  );
end


if (NR >= 7) begin: MIX6_7
  // Build double mixer
  mix2 #(.CALCTYPE(CALCTYPE)) mix2_6 (
    .clk(C122_div2_clk),
    .clk_2x(C122_clk),
    .rst(C122_rst),
    .phi0(C122_frequency_HZ[6]),
    .phi1(C122_frequency_HZ[7]),
    .adc(temp_ADC),
    .mixdata0_i(mixdata_i[6]),
    .mixdata0_q(mixdata_q[6]),
    .mixdata1_i(mixdata_i[7]),
    .mixdata1_q(mixdata_q[7])
  );
end

if (NR >= 7) begin: RECEIVER6
  receiver_nco #(.CICRATE(CICRATE)) receiver_6 (
    .rst_all(C122_rst),
    .clock(C122_div2_clk),
    .clock_2x(C122_clk),
    .rate(memrate[6]),
    .mixdata_I(mixdata_i[6]),
    .mixdata_Q(mixdata_q[6]),
    .out_strobe(strobe[6]),
    .out_data_I(rx_I[6]),
    .out_data_Q(rx_Q[6])
  );
end

if (NR >= 8) begin: RECEIVER7
  receiver_nco #(.CICRATE(CICRATE)) receiver_7 (
    .rst_all(C122_rst),
    .clock(C122_div2_clk),
    .clock_2x(C122_clk),
    .rate(memrate[7]),
    .mixdata_I(mixdata_i[7]),
    .mixdata_Q(mixdata_q[7]),
    .out_strobe(strobe[7]),
    .out_data_I(rx_I[7]),
    .out_data_Q(rx_Q[7])
  );
end

if (NR >= 9) begin: MIX8_9
  // Build double mixer
  mix2 #(.CALCTYPE(CALCTYPE)) mix2_8 (
    .clk(C122_div2_clk),
    .clk_2x(C122_clk),
    .rst(C122_rst),
    .phi0(C122_frequency_HZ[8]),
    .phi1(C122_frequency_HZ[9]),
    .adc(temp_ADC),
    .mixdata0_i(mixdata_i[8]),
    .mixdata0_q(mixdata_q[8]),
    .mixdata1_i(mixdata_i[9]),
    .mixdata1_q(mixdata_q[9])
  );
end

if (NR >= 9) begin: RECEIVER8
  receiver_nco #(.CICRATE(CICRATE)) receiver_8 (
    .rst_all(C122_rst),
    .clock(C122_div2_clk),
    .clock_2x(C122_clk),
    .rate(memrate[8]),
    .mixdata_I(mixdata_i[8]),
    .mixdata_Q(mixdata_q[8]),
    .out_strobe(strobe[8]),
    .out_data_I(rx_I[8]),
    .out_data_Q(rx_Q[8])
  );
end

if (NR >= 10) begin: RECEIVER9
  receiver_nco #(.CICRATE(CICRATE)) receiver_9 (
    .rst_all(C122_rst),
    .clock(C122_div2_clk),
    .clock_2x(C122_clk),
    .rate(memrate[9]),
    .mixdata_I(mixdata_i[9]),
    .mixdata_Q(mixdata_q[9]),
    .out_strobe(strobe[9]),
    .out_data_I(rx_I[9]),
    .out_data_Q(rx_Q[9])
  );
end
endgenerate

// only using Rx0 and Rx1 Sync for now so can use simpler code
  // Move SyncRx[n] into C122 clock domain
//  cdc_mcp #(8) SyncRx_inst
//  (.a_rst(C122_rst), .a_clk(rx_clock), .a_data(SyncRx[0]), .a_data_rdy(Rx_data_ready), .b_rst(C122_rst), .b_clk(C122_div2_clk), .b_data(C122_SyncRx[0]));
	

wire [11:0] AIN1;  // FWD_power
wire [11:0] AIN2;  // REV_power
wire [11:0] AIN3;  // User 1
wire [11:0] AIN4;  // User 2
wire [11:0] AIN5;  // holds 12 bit ADC value of Forward Voltage detector.
wire [11:0] AIN6;  // holds 12 bit ADC of 13.8v measurement 
wire pk_detect_reset;
wire pk_detect_ack;


//---------------------------------------------------------
//              Decode Command & Control data
//---------------------------------------------------------

wire         mode;     			// normal or Class E PA operation 
wire         Attenuator;		// selects input attenuator setting, 1 = 20dB, 0 = 0dB 
wire  [31:0] frequency[0:NR-1]; 	// Tx, Rx1, Rx2, Rx3, Rx4, Rx5, Rx6, Rx7
wire         IF_duplex;
wire   [7:0] Drive_Level; 		// Tx drive level
wire         Mic_boost;			// Mic boost 0 = 0dB, 1 = 20dB
wire         Line_In;				// Selects input, mic = 0, line = 1
wire			 common_Merc_freq;		// when set forces Rx2 freq to Rx1 freq
wire   [4:0] Line_In_Gain;		// Sets Line-In Gain value (00000=-32.4 dB to 11111=+12 dB in 1.5 dB steps)
wire         Apollo;				// Selects Alex (0) or Apollo (1)
wire   [4:0] Attenuator0;			// 0-31 dB Heremes attenuator value
wire			 TR_relay_disable;		// Alex T/R relay disable option
wire         internal_CW;			// set when internal CW generation selected
wire   [7:0] sidetone_level;		// 0 - 100, sets internal sidetone level
wire 			 sidetone;				// Sidetone enable, 0 = off, 1 = on
wire   [7:0] RF_delay;				// 0 - 255, sets delay in mS from CW Key activation to RF out
wire   [9:0] hang;					// 0 - 1000, sets delay in mS from release of CW Key to dropping of PTT
wire  [11:0] tone_freq;				// 200 to 1000 Hz, sets sidetone frequency.
wire         key_reverse;		   // reverse CW keyes if set
wire   [5:0] keyer_speed; 			// CW keyer speed 0-60 WPM
wire         keyer_mode;			// 0 = Mode A, 1 = Mode B
wire 			 iambic;					// 0 = external/straight/bug  1 = iambic
wire   [7:0] keyer_weight;			// keyer weight 33-66
wire         keyer_spacing;		// 0 = off, 1 = on
wire 			 break_in;				// if set then use break in mode
wire   [4:0] atten0_on_Tx;			// ADC0 attenuation value to use when Tx is active
wire  [31:0] Rx_frequency[0:NR-1];	// Rx(n) receive frequency
wire  [31:0] Tx0_frequency;		// Tx0 transmit frequency
wire  [31:0] Alex_data;				// control data to Alex board
wire         run;						// set when run active 
wire 		    PC_PTT;					// set when PTT from PC active
wire 	 [7:0] dither;					// Dither for ADC0
wire   [7:0] random;					// Random for ADC0[
wire   [7:0] RxADC[0:NR-1];			// ADC or DAC that Rx(n) is connected to
wire 	[15:0] RxSampleRate[0:NR-1];	// Rxn Sample rate 48/96/192 etc
wire 			 Alex_data_ready;		// indicates Alex data available
wire         Rx_data_ready;		// indicates Rx_specific data available
wire 			 Tx_data_ready;		// indicated Tx_specific data available
wire   [7:0] Mux;						// Rx in mux mode when bit set, [0] = Rx0, [1] = Rx1 etc 
wire   [7:0] SyncRx[0:NR-1];			// bit set selects Rx to sync or mux with
wire 	 [15:0] EnableRx0_15;			// Rx enabled when bit set, [0] = Rx0, [1] = Rx1 etc
wire 	 [15:0] C122_EnableRx0_15;
wire  [15:0] Rx_Specific_port;	// 
wire  [15:0] Tx_Specific_port;
wire  [15:0] High_Prioirty_from_PC_port;
wire  [15:0] High_Prioirty_to_PC_port;			
wire  [15:0] Rx_Audio_port;
wire  [15:0] Tx_IQ_port;
wire  [15:0] Rx0_port;
wire  [15:0] Mic_port;
wire  [15:0] Wideband_ADC0_port;
wire   [7:0] Wideband_enable;					// [0] set enables ADC0, [1] set enables ADC1
wire  [15:0] Wideband_samples_per_packet;				
wire   [7:0] Wideband_sample_size;
wire   [7:0] Wideband_update_rate;
wire   [7:0] Wideband_packets_per_frame; 
wire  [15:0] Envelope_PWM_max;
wire  [15:0] Envelope_PWM_min;
wire   [7:0] Open_Collector;
wire   [7:0] User_Outputs;
wire   [7:0] Mercury_Attenuator;	
wire 			 CWX;						// CW keyboard from PC 
wire         Dot;						// CW dot key from PC
wire         Dash;					// CW dash key from PC]
wire freq_data_ready;


wire         VNA;									// Selects VNA mode when set. 
wire         PA_enable;
wire   [7:0] Alex_enable;			
wire         data_ready;
wire 			 HW_reset1;
wire 			 HW_reset2;	
wire 			 HW_reset3;
wire 			 HW_reset4;
wire 			 HW_timer_enable;	


`ifdef DONT
General_CC #(1024) General_CC_inst // parameter is port number  ***** this data is in rx_clock domain *****
			(
				// inputs
				.clock(rx_clock),
				.to_port(to_port),
				.udp_rx_active(udp_rx_active),
				.udp_rx_data(udp_rx_data),
				// outputs
			   .Rx_Specific_port(Rx_Specific_port),
				.Tx_Specific_port(Tx_Specific_port),
				.High_Prioirty_from_PC_port(High_Prioirty_from_PC_port),
				.High_Prioirty_to_PC_port(High_Prioirty_to_PC_port),			
				.Rx_Audio_port(Rx_Audio_port),
				.Tx_IQ_port(Tx_IQ_port),
				.Rx0_port(Rx0_port),
				.Mic_port(Mic_port),
				.Wideband_ADC0_port(Wideband_ADC0_port),
				.Wideband_enable(Wideband_enable),
				.Wideband_samples_per_packet(Wideband_samples_per_packet),				
				.Wideband_sample_size(Wideband_sample_size),
				.Wideband_update_rate(Wideband_update_rate),
				.Wideband_packets_per_frame(Wideband_packets_per_frame),
				.VNA(VNA),
				.PA_enable(PA_enable),
				.Alex_enable(Alex_enable),			
				.data_ready(data_ready),
				.HW_reset(HW_reset1),
				.HW_timer_enable(HW_timer_enable)
				);
`endif


High_Priority_CC #(1027, NR) High_Priority_CC_inst  // parameter is port number 1027  ***** this data is in rx_clock domain *****
			(
				// inputs
				.clock(rx_clock),
				.to_port(to_port),
				.udp_rx_active(udp_rx_active),
				.udp_rx_data(udp_rx_data),
				.HW_timeout(HW_timeout),					// used to clear run if HW timeout.
				// outputs
				.run(run),
				.PC_PTT(PC_PTT),
				.CWX(CWX),
				.Dot(Dot),
				.Dash(Dash),
				.Rx_frequency(Rx_frequency),
				.Tx0_frequency(Tx0_frequency),
				.Alex_data(Alex_data),
				.drive_level(Drive_Level),
				.Attenuator0(Attenuator0),
				.Open_Collector(Open_Collector),			// open collector outputs on Hermes
				.Alex_data_ready(Alex_data_ready),
				.HW_reset(HW_reset2),
				.sequence_errors(HP_sequence_errors)
			);

`ifdef DONT
Tx_specific_CC #(1026)Tx_specific_CC_inst //   // parameter is port number  ***** this data is in rx_clock domain *****
			( 	
				// inputs
				.clock (rx_clock),
				.to_port (to_port),
				.udp_rx_active (udp_rx_active),
				.udp_rx_data (udp_rx_data),
				// outputs
				.EER() ,
				.internal_CW (internal_CW),
				.key_reverse (key_reverse), 
				.iambic (iambic),					
				.sidetone (sidetone), 			
				.keyer_mode (keyer_mode), 		
				.keyer_spacing(keyer_spacing),
				.break_in(break_in), 						
				.sidetone_level(sidetone_level), 
				.tone_freq(tone_freq), 
				.keyer_speed(keyer_speed),	
				.keyer_weight(keyer_weight),
				.hang(hang), 
				.RF_delay(RF_delay),
				.Line_In(Line_In),
				.Line_In_Gain(Line_In_Gain),
				.Mic_boost(Mic_boost),
				.Angelia_atten_Tx0(atten0_on_Tx),	
				.data_ready(Tx_data_ready),
				.HW_reset(HW_reset3)
			);
`endif

Rx_specific_CC #(1025, NR) Rx_specific_CC_inst // parameter is port number  *** not all data is in correct clock domain
			( 	
				// inputs
				.clock(rx_clock),
				.to_port(to_port),
				.udp_rx_active(udp_rx_active),
				.udp_rx_data(udp_rx_data),
				// outputs
				.dither(dither),
				.random(random),
				.RxSampleRate(RxSampleRate),
				.RxADC(RxADC),	
				.SyncRx(SyncRx),
				.EnableRx0_15(EnableRx0_15),
				.Rx_data_ready(Rx_data_ready),
				.Mux(Mux),
				.HW_reset(HW_reset4)
			);			
			
assign  RAND   = random[0];        		//high turns random on
assign  DITH   = dither[0];      		//high turns LTC2208 dither on 

//------------------------------------------------------------
//  			High Priority to PC C&C Encoder 
//------------------------------------------------------------

// All input data is transfered to tx_clock domain in the encoder

wire CC_ack;
wire CC_data_ready;
wire [7:0] CC_data[0:55];
wire [15:0] Exciter_power = FPGA_PTT ? {4'b0,AIN5} : 16'b0; 
wire [15:0] FWD_power     = FPGA_PTT ? {4'b0,AIN1} : 16'b0;
wire [15:0] REV_power     = FPGA_PTT ? {4'b0,AIN2} : 16'b0;
wire [15:0] user_analog1 = {4'b0, AIN4};
wire [15:0] user_analog2 = {4'b0, AIN3};

reg frequency_change[0:NR-1];

reg [31:0] HP_sequence_errors;
reg [31:0] Audio_sequence_errors;
reg [31:0] DUC_sequence_errors;
reg [31:0] Rx_spec_sequence_errors;
reg [31:0] ALL_sequence_errors;
reg [31:0] ALL_sequence_errors_tx;

`ifdef DONT
assign ALL_sequence_errors = HP_sequence_errors + Audio_sequence_errors + DUC_sequence_errors + Rx_spec_sequence_errors;

cdc_sync #(32)cdc_sync_ALL (.siga(ALL_sequence_errors), .rstb(1'b0), .clkb(tx_clock), .sigb(ALL_sequence_errors_tx));

CC_encoder #(50, NR) CC_encoder_inst (				// 50mS update rate
					//	inputs
					.clock(tx_clock),					// tx_clock  125MHz
					.ACK (CC_ack),
					.PTT ((break_in & CW_PTT) | debounce_PTT),
					.Dot (debounce_DOT),
					.Dash(debounce_DASH),
					.frequency_change(frequency_change),
					.locked_10MHz(locked_10MHz),
					.ADC0_overload (OVERFLOW),
					.Exciter_power (Exciter_power),			
					.FWD_power (FWD_power),
					.REV_power (REV_power),
					.Supply_volts ({4'b0,AIN6}),  
					.User_ADC1 (user_analog1),
					.User_ADC2 (user_analog2),
					.User_IO ({3'b0, IO4}),
					.Debug_data(16'd0),
					//.Debug_data({6'b000000,~DEBUG_LED10,~DEBUG_LED9,~DEBUG_LED8,~DEBUG_LED7,~DEBUG_LED6,~DEBUG_LED5,~DEBUG_LED4,~DEBUG_LED3,~DEBUG_LED2,~DEBUG_LED1}),
					.sequence_errors(ALL_sequence_errors_tx),
					.pk_detect_ack(pk_detect_ack),			// from Hermes_ADC
					.FPGA_PTT(FPGA_PTT),						// when set change update rate to 1mS
							
					//	outputs
					.CC_data (CC_data),
					.ready (CC_data_ready),
					.pk_detect_reset(pk_detect_reset) 			// to Hermes_ADC
				);
 
 

//---------------------------------------------------------
//  Debounce inputs - active low
//---------------------------------------------------------

wire debounce_PTT;    // debounced button
wire debounce_DOT;
wire debounce_DASH;
wire 	clean_IO5;

debounce de_PTT	(.clean_pb(debounce_PTT),  .pb(!PTT),      .clk(CMCLK));
debounce de_DOT	(.clean_pb(debounce_DOT),  .pb(!KEY_DOT),  .clk(CMCLK));
debounce de_DASH	(.clean_pb(debounce_DASH), .pb(!KEY_DASH), .clk(CMCLK));
debounce de_IO5   (.clean_pb(clean_IO5),     .pb(~IO5),      .clk(CMCLK)); // decounced IO5 CW input
`endif

//------------------------------------------------------------
//  Hermes on-board attenuator 
//------------------------------------------------------------

// set the input attenuator
wire [4:0] atten0;

assign atten0 = FPGA_PTT ? atten0_on_Tx : Attenuator0;
Attenuator Attenuator_ADC0 (.clk(CBCLK), .data(atten0), .ATTN_CLK(ATTN_CLK),   .ATTN_DATA(ATTN_DATA),   .ATTN_LE(ATTN_LE));

//-------------------------------------------------------
//    PLLs 
//---------------------------------------------------------


/* 
	Divide the 10MHz reference and 122.88MHz clock to give 80kHz signals.
	Apply these to an EXOR phase detector. If the 10MHz reference is not
	present the EXOR output will be a 80kHz square wave. When passed through 
	the loop filter this will provide a dc level of (3.3/2)v which will
	set the 122.88MHz VCXO to its nominal frequency.
	The selection of the internal or external 10MHz reference for the PLL
	is made using a PCB jumper.

*/

wire ref_80khz; 
wire osc_80khz;
wire locked_10MHz;
 

// Use a PLL to divide 10MHz clock to 80kHz
C10_PLL PLL2_inst (.inclk0(OSC_10MHZ), .c0(ref_80khz), .locked(locked_10MHz));

// Use a PLL to divide 122.88MHz clock to 80kHz	as backup in case 10MHz source is not present							
C122_PLL PLL_inst (.inclk0(_122MHz), .c0(osc_80khz), .locked());	
	
//Apply to EXOR phase detector 
assign FPGA_PLL = ref_80khz ^ osc_80khz; 



//-----------------------------------------------------------
//  LED Control  
//-----------------------------------------------------------

/*
	LEDs:  
	
	DEBUG_LED1  	- Lights when an Ethernet broadcast is detected
	DEBUG_LED2  	- Lights when traffic to the boards MAC address is detected
	DEBUG_LED3  	- Lights when detect a received sequence error or ASMI is busy
	DEBUG_LED4 		- Displays state of PHY negotiations - fast flash if no Ethernet connection, slow flash if 100T and on if 1000T
	DEBUG_LED5		- Lights when the PHY receives Ethernet traffic
	DEBUG_LED6  	- Lights when the PHY transmits Ethernet traffic
	DEBUG_LED7  	- Displays state of DHCP negotiations or static IP - on if ACK, slow flash if NAK, fast flash if time out 
					     and long then short flash if static IP
	DEBUG_LED8  	- Lights when sync (0x7F7F7F) received from PC
	DEBUG_LED9  	- Lights when a Metis discovery packet is received
	DEBUG_LED10 	- Lights when a Metis discovery packet reply is sent	
	
	Status_LED	    - Flashes once per second
	
	A LED is flashed for the selected period on the positive edge of the signal.
	If the signal period is greater than the LED period the LED will remain on.


*/

parameter half_second = 2_500_000; // at 12.288MHz clock rate

// LED0 = fast flash if no Ethernet connection, slow flash if 100T, on if 1000T
// and swap between fast and slow flash if not full duplex

`ifdef LEDS

// flash LED1 for ~ 0.2 second whenever rgmii_rx_active
Led_flash Flash_LED1(.clock(CMCLK), .signal(network_status[2]), .LED(DEBUG_LED1), .period(half_second)); 	

// flash LED2 for ~ 0.2 second whenever the PHY transmits
Led_flash Flash_LED2(.clock(CMCLK), .signal(network_status[1]), .LED(DEBUG_LED2), .period(half_second)); 
//assign RAM_A2 = 1'b1; // turn the LED off for now. 	

// flash LED3 for ~0.2 seconds whenever ip_rx_enable
Led_flash Flash_LED3(.clock(CMCLK), .signal(network_status[1]), .LED(DEBUG_LED3), .period(half_second));
// flash LED4 for ~0.2 seconds whenever traffic to the boards MAC address is received 
Led_flash Flash_LED4(.clock(CMCLK), .signal(network_status[0]), .LED(DEBUG_LED4), .period(half_second));

// flash LED5 for ~0.2 seconds whenever udp_rx_enable
// Led_flash Flash_LED5(.clock(CMCLK), .signal(network_status[3]), .LED(DEBUG_LED5), .period(half_second));

// LED6 = on if ACK, slow flash if NAK, fast flash if time out and swap between fast and slow 
// if using a static IP address
// flash LED7 for ~0.2 seconds whenever udp_rx_active
Led_flash Flash_LED7(.clock(CMCLK), .signal(network_status[4]), .LED(DEBUG_LED7), .period(half_second));

// flash LED8 for ~0.2 seconds whenever we detect a Metis discovery request
Led_flash Flash_LED8(.clock(CMCLK), .signal(discovery_reply), .LED(DEBUG_LED8), .period(half_second));

// flash LED9 for ~0.2 seconds whenever we respond to a Metis discovery request
//Led_flash Flash_LED9(.clock(CMCLK), .signal(discovery_respond), .LED(DEBUG_LED9), .period(half_second));   // Rx_Audio_fifo_wrreq

// flash LED9 for ~0.2 seconds when
//Led_flash Flash_LED9(.clock(CMCLK), .signal(Audio_empty & run & get_audio_samples), .LED(DEBUG_LED9), .period(half_second)); 
Led_flash Flash_LED9(.clock(CMCLK), .signal(busy), .LED(DEBUG_LED9), .period(half_second)); 

// flash LED10 for ~0.2 seconds when 
//Led_flash Flash_LED10(.clock(CMCLK), .signal(Audio_full & run), .LED(DEBUG_LED10), .period(half_second));  
Led_flash Flash_LED10(.clock(CMCLK), .signal(erase | erase_done), .LED(DEBUG_LED10), .period(half_second));   //

//Led_flash Flash_LED10(.clock(CMCLK), .signal(Rx_fifo_full[0]|Rx_fifo_full[1]|Rx_fifo_full[2]|Rx_fifo_full[3]|Rx_fifo_full[4]|Rx_fifo_full[5]), .LED(DEBUG_LED10), .period(half_second));

//------------------------------------------------------------
//   Multi-state LED Control   - code in Led_control is for active LOW LEDs
//------------------------------------------------------------

parameter clock_speed = 12_288_000; // 12.288MHz clock 

// display state of PHY negotiations  - fast flash if no Ethernet connection, slow flash if 100T, on if 1000T
// and swap between fast and slow flash if not full duplex
Led_control #(clock_speed) Control_LED0(.clock(CMCLK), .on(network_status[6]), .fast_flash(~network_status[5] || ~network_status[6]),
										.slow_flash(network_status[5]), .vary(~network_status[7]), .LED(DEBUG_LED5));  
										
// display state of DHCP negotiations - on if success, slow flash if fail, fast flash if time out and swap between fast and slow 
// if using a static IP address
Led_control # (clock_speed) Control_LED1(.clock(CMCLK), .on(dhcp_success), .slow_flash(dhcp_failed & !dhcp_timeout),
										.fast_flash(dhcp_timeout), .vary(static_ip_assigned), .LED(DEBUG_LED6));	
`else
 
assign DEBUG_LED1 = 1'b1;
assign DEBUG_LED2 = 1'b1;
assign DEBUG_LED3 = 1'b1;
assign DEBUG_LED4 = 1'b1;
assign DEBUG_LED5 = 1'b1;
assign DEBUG_LED6 = 1'b1;
assign DEBUG_LED7 = 1'b1;
assign DEBUG_LED8 = 1'b1;
assign DEBUG_LED9 = 1'b1;
assign DEBUG_LED10 = 1'b1;

`endif

//Flash Heart beat LED
reg [24:0]HB_counter;
always @(posedge CLK_25MHZ) HB_counter <= HB_counter + 1'b1;
assign Status_LED = HB_counter[23];  // Blink



endmodule 



