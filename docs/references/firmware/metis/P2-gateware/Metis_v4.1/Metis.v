//
//  HPSDR - High Performance Software Defined Radio
//
//  Metis code. 
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


/*  27 January 2024 - Metis code V4.0 - Merged from V1.8 Protocol 1 Metis code.


	Change Log:
		2024 Jan 27, N1GP - Adding Protocol 2, Puresignal.
			    
	NOTES:  - Under Settings > Analysis & Synthesis Settings > Verilig HDL Input, select SystemVerilog-2005 so can pass RAM to a module

					
	LEDs  - LED[0] is located at the top of the board
	
	LED[0]  	- Displays state of PHY negotiations - fast flash if no Ethernet connection, slow flash if 100T, on if 1000T
				  and swap between fast and slow flash if not full duplex
	LED[1]		- Lights when the PHY receives Ethernet traffic
	LED[2]  	- Lights when the PHY transmits Ethernet traffic
	LED[3]  	- Lights when an Ethernet broadcast is detected
	LED[4]  	- Lights when traffic to the boards MAC address is detected
	LED[5]  	- Lights when an ARP request is received
	LED[6]  	- Displays state of DHCP negotiations or static IP - off if not full duplex, on if DHCP ACK, slow flash if DHCP NAK,
	              fast flash if DHCP time out and long then short flash if static IP. If NAK or time out an IPIPA IP address will be used.
	LED[7]  	- Lights when a ping request is received
	LED[8]  	- Lights when a Metis discovery packet is received
	LED[9]  	- Lights when a Metis discovery packet reply is sent
	LED[10]  	- Lights when 0x7F7F7F received from PC
	LED[11]     - Lights when we detect a receive sequence error or EPCS16 is being erased or programmed
	LED[12]     - HEART_BEAT  - Flashes once per second
					
*/

module Metis (
			  ATLAS_A2,ATLAS_A3,ATLAS_A4,ATLAS_A5,ATLAS_A7,ATLAS_A8,ATLAS_A9, ATLAS_A10,ATLAS_A11,
			  ATLAS_A12,ATLAS_A13,ATLAS_A14,ATLAS_A15,ATLAS_A16,ATLAS_A17,ATLAS_A20,ATLAS_A21,ATLAS_A6,
			  ATLAS_C2,ATLAS_C15,ATLAS_C19,ATLAS_C20,ATLAS_C21,ATLAS_C22,ATLAS_C23,ATLAS_C24,
			  RAM_A0,RAM_A1,RAM_A2,RAM_A3,RAM_A4,RAM_A5,RAM_A6,RAM_A7,RAM_A8,RAM_A9,RAM_A10,RAM_A11,RAM_A12,RAM_A13,HEART_BEAT,
			  PHY_TX,PHY_RX,PHY_DV,PHY_TX_CLOCK,PHY_TX_EN,
			  PHY_RX_CLOCK,PHY_CLK125,PHY_MDIO,PHY_MDC,PHY_INT_N,PHY_RESET_N,CLK_25MHZ,
			  MODE2, IN2, IN1, IN0, SCK, SI, CONFIG, NODE_ADDR_CS, NCONFIG, USEROUT0,USEROUT1,USEROUT2,USEROUT3
			  );

		   
// Atlas Bus


input	wire	 ATLAS_A16; // MDOUT_I[5], pin 76,	I from Mercury - Multiple Rx-5
input	wire	 ATLAS_A17; // MDOUT_I[4], pin 70,	I from Mercury - Multiple Rx-4
input	wire	 ATLAS_A7;  // MDOUT_I[3], pin 110,	I from Mercury - Multiple Rx-3
input	wire	 ATLAS_A8;  // MDOUT_I[2], pin 106,	I from Mercury - Multiple Rx-2
input	wire	 ATLAS_A9;  // MDOUT_I[1], pin 100,	I from Mercury - Multiple Rx-1
input	wire	 ATLAS_A10; // MDOUT_I[0], pin 98,	I from Mercury - Multiple Rx-0
input	wire	 ATLAS_A14; // MDOUT_Q[5], pin 82,	Q from Mercury - Multiple Rx-5
input	wire	 ATLAS_A15; // MDOUT_Q[4], pin 80,	Q from Mercury - Multiple Rx-4
input	wire	 ATLAS_A2;  // MDOUT_Q[3], pin 131,	Q from Mercury - Multiple Rx-3
input	wire	 ATLAS_A3;  // MDOUT_Q[2], pin 127,	Q from Mercury - Multiple Rx-2
input	wire	 ATLAS_A4;  // MDOUT_Q[1], pin 118,	Q from Mercury - Multiple Rx-1
input	wire	 ATLAS_A5;  // MDOUT_Q[0], pin 114,	Q from Mercury - Multiple Rx-0

input	wire	 ATLAS_A11; // CDOUT_P		Mic from TLV320 on Penelope

input	wire	 ATLAS_A12; 	// SP_DATA		NWire spectrum data from Mercury
input wire   ATLAS_A13;    // 1PPS from GPS receiver to time stamp data for Chirp signals
output wire  ATLAS_A20; 	// I2C SCL
inout wire   ATLAS_A21; 	// I2C SDA
output wire  ATLAS_C2;     // reset Mercury boards
input	wire	 ATLAS_C15; 	// PTT_in		PTT input from Atlas bus - active high
// ATLAS_A19 is not connected since POR chip 
output wire	 ATLAS_C19; 	// I_data to Penelope
output wire      ATLAS_A6; 	// Q_data to Penelope
output wire	 ATLAS_C20; 	// CC			Command & Control
output wire	 ATLAS_C21; 	// trigger		Spectrum data Trigger signal to Mercury
input	wire	 ATLAS_C22; 	// P_IQ_sync	P_IQ_sync from Penelope
input	wire	 ATLAS_C23; 	// M_LR_sync	M_LR_sync from Mercury
output wire	 ATLAS_C24; 	// M_LR_data	M_LR_data to Mercury and Penny in nWire format

// user IO
input wire        IN0;
input wire        IN1;
input wire 	      IN2;


// MAC EEPROM
output wire		SCK; 						// clock on MAC EEPROM
output wire		SI;						// serial in on MAC EEPROM
input  wire 	CONFIG; 					// SO on MAC EEPROM
output wire 	NODE_ADDR_CS;			// CS on MAC EEPROM
wire SO;	assign SO = CONFIG;			// serial out on MAC EEPROM
wire CS;	assign NODE_ADDR_CS = CS; 	// Chip select on MAC EEPROM

// CW key and PTT
wire dash_n;	assign dash_n 	= IN0; 	// CW dash key, DP9 pin 6, active low
wire dot_n;		assign dot_n 	= IN1; 	// CW dot key, DB9 pin 7, active low
wire PTT_n; 	assign PTT_n  	= IN2; 	// PTT from DB9 pin 8, active low

// Reload FPGA 
output wire 	NCONFIG;				// when high causes FPGA to reload from EPCS16 
assign NCONFIG = IP_write_done || reset_FPGA;

//DB-9 connector output connections
output wire		USEROUT0;				// DB-9 pin 1, FPGA pin 134 open drain output
output wire		USEROUT1;				// DB-9 pin 2, FPGA pin 135 open drain output
output wire		USEROUT2;				// DB-9 pin 3, FPGA pin 137 3.3V TTL output
output wire		USEROUT3;				// DB-9 pin 4, FPGA pin 173 3.3V TTL output

// Assign Atlas pins to signals
wire PTT_in;	assign PTT_in   	= ATLAS_C15;  	// PTT from Atlas, Active high
wire CC;	    assign ATLAS_C20	= CC;			// Command & Control data
wire CDOUT_P;	assign CDOUT_P  	= ATLAS_A11;	// Mic data from Penelope


// RAM
output	wire     RAM_A0;		
output	wire     RAM_A1;
output	wire     RAM_A2;
output	wire     RAM_A3;
output	wire     RAM_A4;
output	wire     RAM_A5;
output	wire     RAM_A6;
output	wire     RAM_A7;
output	wire     RAM_A8;
output	wire     RAM_A9;
output	wire     RAM_A10;
output	wire     RAM_A11;
output	wire     RAM_A12;
output	wire     RAM_A13;



// PHY
output  wire [3:0]PHY_TX;
input   wire [3:0]PHY_RX;		   
input	wire     PHY_DV;					// PHY has data flag
output	wire     PHY_TX_CLOCK;		// PHY Tx data clock
output	wire	 PHY_TX_EN;				// PHY Tx enable
input	wire	 PHY_RX_CLOCK;      		// PHY Rx data clock
input	wire     PHY_CLK125;				// 125MHz clock from PHY PLL
inout	wire     PHY_MDIO;				// data line to PHY MDIO
output	wire 	 PHY_MDC;				// 2.5MHz clock to PHY MDIO
input	wire 	 PHY_INT_N;	
output	wire     PHY_RESET_N;
input	wire 	 CLK_25MHZ;					// 25MHz clock 

// Heart beat LED
output wire 	  HEART_BEAT;			// LED, flashes, runs off PHY 125MHz clock.

// speed select via JP2
input wire MODE2;							// high with jumper off

parameter M_TPD   = 4;
parameter IF_TPD  = 2;

localparam board_type = 8'h00;		  	// 00 for Metis, 01 for Hermes, 02 for ANAN-10E, 03 for Angelia, and 05 for Orion
parameter  Metis_version = 8'd41;	// FPGA code version
parameter  beta_version = 8'd0;	// Should be 0 for official release
parameter  protocol_version = 8'd43;	// openHPSDR protocol version implemented

localparam NR = 2;                  //Maximum number of receiver channels to report
localparam master_clock = 122880000; 	// DSP  master clock in Hz.

localparam TX_FIFO_SZ  = 1024; 		// 16 by 1024 deep TX FIFO  
localparam SP_FIFO_SZ  = 1024; 		// 16 by 1024 deep SP FIFO

wire [5:0] MDOUT_I;       			// I data from Mercury 
wire [5:0] MDOUT_Q;       			// Q data from Mercury 
assign  MDOUT_I = {ATLAS_A16,ATLAS_A9,ATLAS_A7,ATLAS_A8,ATLAS_A17,ATLAS_A10};
assign  MDOUT_Q = {ATLAS_A2,ATLAS_A3,ATLAS_A14,ATLAS_A15,ATLAS_A4,ATLAS_A5};

wire PPS;									// 1PPS from GPS receiver to time stamp data
assign PPS = ATLAS_A13;


//------------------------------------------------------------
//		Clocks
//------------------------------------------------------------

wire C125_clk;
assign C125_clk = PHY_CLK125;	// use PHY 125MHz clock for system clock

wire C125_locked; // high when PLL locked
wire IF_clk;
wire I2C_clock;
wire clk192, CLRCLK;	

//   IF clock     CW clocks
// C0 = 48MHz, C1 = profile clock 192kHz, C2 = CLRCLK = 48kHz
PLL_clocks PLL_inst(.areset(), .inclk0(C125_clk), .c0(IF_clk), .c1(clk192), .c2(CLRCLK), .locked(C125_locked));
PLL_2 PLL_2_inst(.areset(), .inclk0(C125_clk), .c0(I2C_clock));

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

//--------------------------------------------------------------
// Reset Lines - C125_rst, IF_rst
//--------------------------------------------------------------

wire  C125_rst;
wire  IF_rst;

assign PHY_RESET_N = 1'bz;
assign C125_rst  = !C125_locked;
assign IF_rst = (C125_rst || network_state);

//-----------------------------------------------------------------------------
//                           network module
//-----------------------------------------------------------------------------
wire network_state;
wire speed_1Gbit;

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
	

//----------------------------------------------------------------------------------
//  I2C Master to setup TLV320 DAC on Penny(Lane), select mic gain and input source, line-in gain and line-in source, obtain
//  firmware version numbers from Penny and multiple Mercury boards, update Penny output power level, FWD power level, REV
//  power level and Mercury ADC overload status values.
//----------------------------------------------------------------------------------

i2c_interface interface_inst(.Tx_clock_2(clock_12_5MHz), 
    .I2C_clock(I2C_clock), 
    .reset_n(IF_rst),
    .sda(ATLAS_A21), 
    .scl(ATLAS_A20), 
    .probe(probe), 
    .mic_boost(IF_Mic_boost), 
    .line_in(IF_Line_in), 
    .line_gain(IF_line_boost), 
    .Penny_ALC(Penny_ALC),
    .FWD(FWD), 
    .REV(REV), 
    .Merc1_ver(Merc_serialno), .Merc1_overload(ADC_OVERLOAD), 
    .Merc2_ver(Merc2_version), .Merc2_overload(ADC_OVERLOAD2),
    .Merc3_ver(Merc3_version), .Merc3_overload(ADC_OVERLOAD3), 
    .Merc4_ver(Merc4_version), .Merc4_overload(ADC_OVERLOAD4),
    .Penny_version(Penny_serialno)
);

wire  sp_fifo_rdreq;
wire [7:0]sp_fifo_rddata;
wire sp_fifo_wrempty;
wire sp_fifo_wrfull;
wire sp_fifo_wrreq;


//-----------------------------------------------------------------------------
//   Wideband Spectrum Data 
//-----------------------------------------------------------------------------

// When sp_fifo_wrempty fill fifo with 'user selected' # words of consecutive ADC samples.
// Pass sp_data_ready to sdr_send to indicate that data is available.
// Reset fifo when !wideband so the data always starts at a known state.
// The time between fifo fills is set by the user (0-255mS). . The number of  samples sent per UDP frame is set by the user
// (default to 1024) as is the sample size (defaults to 16 bits).
// The number of frames sent, per fifo fill, is set by the user - currently set at 8 i.e. 4,096 samples. 


wire wideband = (Wideband_enable[0] | Wideband_enable[1]);
reg [15:0]sp_fifo_wdata;
wire have_sp_data;
wire  spd_rdy, spd_ack;

SP_fifo  SPF (.aclr(!wideband), .wrclk (IF_clk), .rdclk(tx_clock), 
             .wrreq (sp_fifo_wrreq), .data ({sp_fifo_wdata[7:0], sp_fifo_wdata[15:8]}), .rdreq (sp_fifo_rdreq),
             .q(sp_fifo_rddata), .wrfull(sp_fifo_wrfull), .wrempty(sp_fifo_wrempty)); 	
				 
sp_rcv_ctrl SPC (.clk(IF_clk), .reset(IF_rst), .sp_fifo_wrempty(sp_fifo_wrempty), .spd_rdy(spd_rdy), .spd_ack(spd_ack),
                 .sp_fifo_wrfull(sp_fifo_wrfull), .write(sp_fifo_wrreq), .have_sp_data(have_sp_data));	

assign ATLAS_C21 = sp_fifo_wrempty & wideband;  // get wideband data when SP fifo is empty and wide spectrum is selected

NWire_rcv #(.OSL(16), .OSW(1), .ICLK_FREQ(125000000), .XCLK_FREQ(48000000), .SLOWEST_FREQ(80000))  
       SPD (.irst(C125_rst), .iclk(C125_clk), .xrst(IF_rst), .xclk(IF_clk),
            .xrcv_rdy(spd_rdy), .xrcv_ack(spd_ack), .xrcv_data(sp_fifo_wdata), .din(ATLAS_A12) );

reg  sp_data_ready;
reg [24:0] wb_counter;
wire WB_ack;
wire [17:0] update_rate = 125000;

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


wire merc1_2_pres = ((Merc_serialno > 8'h00 && Merc_serialno < 8'h7f) &&
                     (Merc2_version > 8'h00 && Merc2_version < 8'h7f)) | !MODE2;
wire wide_spectrum;
wire Tx_fifo_rdreq;
wire [10:0] PHY_Tx_rdused;
wire Rx_enable;
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

wire probe;

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
  .PHY_TX(PHY_TX),
  .PHY_TX_EN(PHY_TX_EN),            
  .PHY_TX_CLOCK(PHY_TX_CLOCK),         
  .PHY_RX(PHY_RX),     
  .PHY_DV(PHY_DV),    					// use PHY_DV to be consistent with Metis            
  .PHY_RX_CLOCK(PHY_RX_CLOCK),         
  .PHY_CLK125(PHY_CLK125),           
  .PHY_MDIO(PHY_MDIO),             
  .PHY_MDC(PHY_MDC),


    // MAC eeprom
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
 
wire [7:0] port_ID;
wire [7:0]Mic_data;
wire mic_fifo_rdreq;
wire [8:0]Rx_data[0:NR-1];
reg fifo_ready[0:NR-1];
wire fifo_rdreq[0:NR-1];
logic [15:0] checksum;
wire PA_enable;
wire CC_ack;
wire CC_data_ready;
wire [7:0] CC_data[0:55];


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
	.code_version(Metis_version),
	.beta_version(beta_version),
	.merc1_ver(Merc_serialno),
	.merc2_ver(Merc2_version),
	.merc3_ver(Merc3_version),
	.merc4_ver(Merc4_version),
	.penny_ver(Penny_serialno),
	.metis_ver(Metis_version),
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

wire locked_10MHz = 1'b1; // for now
CC_encoder #(50, NR) CC_encoder_inst (				// 50mS update rate
					//	inputs
					.clock(tx_clock),					// tx_clock  125MHz
					.ACK (CC_ack),
					.PTT ((break_in & CW_PTT) | clean_PTT_in),
					.Dot (debounce_DOT),
					.Dash(debounce_DASH),
					.locked_10MHz(locked_10MHz),
					.ADC_overload ({4'b0000,ADC_OVERLOAD4,ADC_OVERLOAD3,ADC_OVERLOAD2,ADC_OVERLOAD}),
					.Exciter_power (Penny_ALC),
					.FWD_power (FWD),
					.REV_power (REV),
					.Supply_volts ({4'b0,AIN6}),  
					.User_ADC1 (user_analog1),
					.User_ADC2 (user_analog2),
					.User_IO ({3'b0, IO4}),
					.Debug_data(16'd0),
					//.Debug_data({6'b000000,~DEBUG_LED10,~DEBUG_LED9,~DEBUG_LED8,~DEBUG_LED7,~DEBUG_LED6,~DEBUG_LED5,~DEBUG_LED4,~DEBUG_LED3,~DEBUG_LED2,~DEBUG_LED1}),
					.sequence_errors(ALL_sequence_errors_tx),
					.pk_detect_ack(pk_detect_ack),			// from Hermes_ADC
					.FPGA_PTT(FPGA_PTT),				// when set change update rate to 1mS

					//	outputs
					.CC_data (CC_data),
					.ready (CC_data_ready),
					.pk_detect_reset(pk_detect_reset) 			// to Hermes_ADC
				);
        
        
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
				._10MHz_reference(_10MHz_reference),
				._128MHz_reference(_128MHz_reference),
				.common_merc_freq(common_merc_freq),
				.HW_reset(HW_reset1),
				.HW_timer_enable(HW_timer_enable)
				);
				
        

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
				.Rx_phase_word(Rx_phase_word),
				.Tx0_phase_word(Tx0_phase_word),
				.Alex_data(Alex_data),
				.Alex_Tx_data(Alex_Tx_data),
				.drive_level(Drive_Level),
				.Attenuator0(Attenuator0),
				.Attenuator1(Attenuator1),
				.Open_Collector(Open_Collector),			// open collector outputs on Hermes
				.User_Outputs(IF_OD),
				.HP_data_ready(HP_data_ready),
				.Mercury_Attenuator(Mercury_Attenuator),
				.HW_reset(HW_reset2),
				.sequence_errors(HP_sequence_errors)
			);

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
				.EnableRx0_7(EnableRx0_7),
				.Rx_data_ready(Rx_data_ready),
				.Mux(Mux),
				.HW_reset(HW_reset4)
			);			



///////////////////////////////////////////////////////////////////////////////
//
// Left/Right Audio data transfers to Mercury(C24)
// I/Q Audio data transfer to Penelope(C19)
//
///////////////////////////////////////////////////////////////////////////////
wire IF_C23, IF_C22;
wire IF_m_pulse, IF_p_pulse;

cdc_sync cdc_c23 (.siga(ATLAS_C23), .rstb(IF_rst), .clkb(IF_clk), .sigb(IF_C23)); // C23 = M_LR_sync
pulsegen cdc_m   (.sig(IF_C23), .rst(IF_rst), .clk(IF_clk), .pulse(IF_m_pulse));

cdc_sync cdc_c22 (.siga(ATLAS_C22), .rstb(IF_rst), .clkb(IF_clk), .sigb(IF_C22)); // C22 = P_IQ_sync
pulsegen cdc_p   (.sig(IF_C22), .rst(IF_rst), .clk(IF_clk), .pulse(IF_p_pulse));

wire Rx_Audio_fifo_wrreq;
wire  [31:0] LR_data;
wire get_audio_samples;  // request audio samples at 48ksps
wire Audio_full;
wire Audio_empty;
wire [31:0]audio_data;
reg [11:0]Rx_Audio_Used;

Rx_Audio_fifo Rx_Audio_fifo_inst(.wrclk (rx_clock),.rdreq (get_audio_samples),.rdclk (IF_C23),.wrreq(Rx_Audio_fifo_wrreq), 
			.rdusedw(Rx_Audio_Used), .data (audio_data),.q (LR_data), .aclr(IF_rst | !run), .wrfull(Audio_full), .rdempty(Audio_empty));
					 
// Manage Rx Audio data to feed to Audio FIFO  - parameter is port #
byte_to_32bits #(1028) Audio_byte_to_32bits_inst
			(.clock(rx_clock), .run(run), .udp_rx_active(udp_rx_active), .udp_rx_data(udp_rx_data), .to_port(to_port),
			 .fifo_wrreq(Rx_Audio_fifo_wrreq), .data_out(audio_data), .sequence_errors(), .full(Audio_full));
			
// 16 bits, Audio, two channels for TLV320 on Mercury or Penelope
NWire_xmit #(.SEND_FREQ(50000),.OSL(32), .OSW(1), .ICLK_FREQ(125000000), .XCLK_FREQ(48000000))
  M_LRAudio (.irst(C125_rst), .iclk(C125_clk), .xrst(IF_rst), .xclk(IF_clk),
             .xdata(/*Rx_audio CW*/ LR_data), .xreq(IF_m_pulse), .xrdy(get_audio_samples), .xack(), .dout(ATLAS_C24));

// 48 bits, I & Q channels for Penelope and Mercury(PS) - always at 192kHz
NWire_xmit #(.SEND_FREQ(192000), .OSL(48), .OSW(1), .ICLK_FREQ(125000000),
             .XCLK_FREQ(125000000), .LOW_TIME(1'b0))
       OUT_I (.irst(IF_rst), .iclk(C125_clk), .xrst(C125_rst), .xclk(C125_clk),
             .xdata(IQ_Tx_data), .xreq(IF_p_pulse), .xrdy(), .xack(), .dout(ATLAS_C19));

wire Tx1_fifo_wrreq;
wire [47:0]IF_IQ1_data;
wire [47:0]Tx1_IQ_data;
wire [12:0]write_used;
wire IF_m_rdy, IF_m_ack, IF_p_rdy, IF_p_ack;


Tx1_IQ_fifo Tx1_IQ_fifo_inst(.wrclk (rx_clock),.rdreq (1'b1),.rdclk (IF_C22),.wrreq(Tx1_fifo_wrreq), 
					 .data (Tx1_IQ_data), .q(IF_IQ1_data), .aclr(!run | IF_rst), .wrusedw(write_used));
					 
// Manage Tx I&Q data to feed to Tx  - parameter is port #
byte_to_48bits #(1029) IQ_byte_to_48bits_inst
			(.clock(rx_clock), .run(run), .udp_rx_active(udp_rx_active), .udp_rx_data(udp_rx_data), .to_port(to_port),
			 .fifo_wrreq(Tx1_fifo_wrreq), .data_out(Tx1_IQ_data), .full(1'b0), .sequence_errors());

// Ensure I&Q data is zero if not transmitting
wire [47:0] IQ_Tx_data = FPGA_PTT ? IF_IQ1_data : 48'b0; 													

assign FPGA_PTT = PC_PTT | clean_PTT_in | CW_PTT;

///////////////////////////////////////////////////////////////////////////////////////////////////////
//
//  Receive MDOUT and CDOUT_P data to put in TX FIFO
//
///////////////////////////////////////////////////////////////////////////////////////////////////////

// Mic_fifo gets whacky if mic_rdy and mic_ack aren't strictly controlled
reg  mic_ack, mic_rdy, do_mic_cnt;
reg [9:0] mic_samples_count;
always @(posedge IF_clk) begin
    if (mic_rdy) begin
        mic_rdy <= 1'b0;
        mic_ack <= 1'b1;
    end
    else if (mic_samples_count < 10'd500 && do_mic_cnt) begin
        mic_ack <= 1'b0;
        mic_samples_count <= mic_samples_count + 1'b1;
    end
    else if (mic_samples_count == 10'd500) begin
        do_mic_cnt <= 1'b0;
        mic_samples_count <= 10'd0;
    end
    else if (IF_P_mic_Data_rdy && !do_mic_cnt) begin
        mic_rdy <= 1'b1;
        do_mic_cnt <= 1'b1;
    end
end

wire [11:0] mic_rdused; 
							  
Mic_fifo Mic_fifo_inst(.wrclk (IF_clk),.rdreq (mic_fifo_rdreq),.rdclk (tx_clock),.wrreq (mic_rdy), 
                       .data ({IF_mic_Data[7:0], IF_mic_Data[15:8]}), .q (Mic_data), .wrfull(),
                       .rdusedw(mic_rdused), .aclr(!run)); 

reg mic_fifo_ready;
assign mic_fifo_ready = mic_rdused > 12'd131 ? 1'b1 : 1'b0; // used to indicate that fifo has enough data to send to PC.					  

NWire_rcv #(.OSL(16), .OSW(1), .ICLK_FREQ(125000000), .XCLK_FREQ(48000000), .SLOWEST_FREQ(20000))
    P_MIC (.irst(C125_rst), .iclk(C125_clk), .xrst(IF_rst), .xclk(IF_clk),
           .xrcv_rdy(IF_P_mic_Data_rdy), .xrcv_ack(mic_ack),
           .xrcv_data(IF_mic_Data), .din(CDOUT_P) );

`ifdef FAKEMIC
reg [9:0] mic_samples_count;
always @ (posedge IF_clk) begin
  if (run) begin
    if (mic_samples_count == 10'd998) begin // 48k
      //IF_P_mic_Data_rdy <= 1'b1;
      mic_rdy <= 1'b1;
      //IF_mic_Data <= 16'd0;
      mic_samples_count <= mic_samples_count + 1'b1;
    end
    else if (mic_samples_count == 10'd999) begin
      //IF_P_mic_Data_rdy <= 1'b0;
      mic_rdy <= 1'b0;
      mic_samples_count <= 10'd0;
    end
    else  mic_samples_count <= mic_samples_count + 1'b1;
  end
end
`endif

wire   [23:0] IF_M_I_Data [0:NR-1];
wire [NR-1:0] IF_M_I_Data_rdy;
wire   [23:0] IF_M_Q_Data [0:NR-1];
wire [NR-1:0] IF_M_Q_Data_rdy;
wire          IF_P_mic_Data_rdy;
reg     [2:0] IF_clock_s;
wire   [63:0] IF_tx_IQ_mic_data;
reg           IF_tx_IQ_mic_rdy;
wire          IF_tx_IQ_Data_ack[0:NR-1];
wire   [47:0] IF_IQ_Data;
wire   [15:0] IF_mic_Data;
reg [1:0] sampling_rate[0:NR-1];


reg       [7:0] Penny_serialno;
reg       [7:0] Merc_serialno;
reg       [7:0] Merc2_version;
reg       [7:0] Merc3_version;
reg       [7:0] Merc4_version;

reg      [11:0] Penny_ALC;	// Output power from Penny(Lane)
wire 		[11:0] FWD;	// FWD power from AIN4 on Penny(Lane)
wire 		[11:0] REV;	// REV power from AIN3 on Penny(Lane)

reg             ADC_OVERLOAD;    // up to 4 Mercury boards
reg             ADC_OVERLOAD2;
reg             ADC_OVERLOAD3;
reg             ADC_OVERLOAD4;

wire        Rx_fifo_wreq[0:NR-1];
wire [8:0]  Rx_fifo_data[0:NR-1];
wire        Rx_fifo_full[0:NR-1];
wire [11:0] Rx_used[0:NR-1];
wire        Rx_fifo_clr[0:NR-1];
wire        Rx_fifo_empty[0:NR-1];
wire        phy_ready;
wire        IF_run;
wire        IF_RxADC0, IF_RxADC1;

// move flags into correct clock domains
cdc_sync #(1) IF_run_sync  (.siga(run), .rstb(IF_rst), .clkb(IF_clk), .sigb(IF_run));
cdc_sync #(8) IF_EnableRx0_7_sync  (.siga(EnableRx0_7), .rstb(IF_rst), .clkb(IF_clk), .sigb(IF_EnableRx0_7));
cdc_sync #(1) IF_RxADC0_sync  (.siga(RxADC[0][0]), .rstb(IF_rst), .clkb(IF_clk), .sigb(IF_RxADC0));
cdc_sync #(1) IF_RxADC1_sync  (.siga(RxADC[1][0]), .rstb(IF_rst), .clkb(IF_clk), .sigb(IF_RxADC1));

generate
genvar d;
for (d = 0 ; d < NR; d++)
  begin:p
	Rx_fifo Rx1_fifo_inst (.wrclk (IF_clk),.rdreq (fifo_rdreq[d]),.rdclk (tx_clock),.wrreq (Rx_fifo_wreq[d]), .rdempty(Rx_fifo_empty[d]),
			 .data (Rx_fifo_data[d]), .q (Rx_data[d]), .wrfull(Rx_fifo_full[d]),
			 .rdusedw(Rx_used[d]), .aclr (IF_rst | Rx_fifo_clr[d] | !IF_run));

	Rx_fifo_ctrl #(NR) Rx1_fifo_ctrl_inst ( .reset(!IF_run || !IF_EnableRx0_7[d]), .clock(IF_clk), .Sync_data_in_I(IF_M_I_Data[1]), .Sync_data_in_Q(IF_M_Q_Data[1]),
			.i_rdy(IF_M_I_Data_rdy[d]), /* .q_rdy(IF_M_Q_Data_rdy[d]),*/ .fifo_full(Rx_fifo_full[d]), .ack(IF_tx_IQ_Data_ack[d]),
			.wrenable(Rx_fifo_wreq[d]), .data_out(Rx_fifo_data[d]), .fifo_clear(Rx_fifo_clr[d]), .adc2(d==0?IF_RxADC0:IF_RxADC1),
			.data_in_I(IF_M_I_Data[d]), .data_in_Q(IF_M_Q_Data[d]), .Sync(d==0?IF_SyncRx[0][1]:1'b0));

        NWire_rcv #(.OSL(24), .OSW(1), .ICLK_FREQ(125000000), .XCLK_FREQ(48000000), .SLOWEST_FREQ(20000))
          M_I (.irst(C125_rst), .iclk(C125_clk), .xrst(IF_rst), .xclk(IF_clk),
                .xrcv_rdy(IF_M_I_Data_rdy[d]), .xrcv_ack(IF_tx_IQ_Data_ack[d]),
                .xrcv_data(IF_M_I_Data[d]), .din(MDOUT_I[d]) );

        NWire_rcv #(.OSL(24), .OSW(1), .ICLK_FREQ(125000000), .XCLK_FREQ(48000000), .SLOWEST_FREQ(20000))
          M_Q (.irst(C125_rst), .iclk(C125_clk), .xrst(IF_rst), .xclk(IF_clk),
                .xrcv_rdy(IF_M_Q_Data_rdy[d]), .xrcv_ack(IF_tx_IQ_Data_ack[d]),
                .xrcv_data(IF_M_Q_Data[d]), .din(MDOUT_Q[d]) );

        always @ (posedge tx_clock)
		fifo_ready[d] <= (Rx_used[d] > 12'd1427) ? 1'b1 : 1'b0;

        cdc_mcp #(32) cdc_sync_PHASE 
          (.a_rst(C125_rst), .a_clk(rx_clock), .a_data(Rx_phase_word[d]), .a_data_rdy(HP_data_ready), .b_rst(IF_rst), .b_clk(IF_clk), .b_data(IF_phase_word[d+1]));

        cdc_mcp #(16) IF_SampleRate_sync 
          (.a_rst(C125_rst), .a_clk(rx_clock), .a_data(RxSampleRate[d]), .a_data_rdy(Rx_data_ready), .b_rst(IF_rst), .b_clk(IF_clk), .b_data(IFSampleRate[d]));

        always @ (posedge IF_clk) begin
          case (IFSampleRate[d])
            16'd48:  sampling_rate[d] <= 2'b00;
            16'd96:  sampling_rate[d] <= 2'b01;
            16'd192: sampling_rate[d] <= 2'b10;
            16'd384: sampling_rate[d] <= 2'b11;
            default: sampling_rate[d] <= 2'b00;
          endcase
        end
  end
endgenerate

// only using Rx0 and Rx1 Sync for now so can use simpler code
// Move SyncRx[n] into IF clock domain
cdc_mcp #(8) SyncRx_inst
(.a_rst(C125_rst), .a_clk(rx_clock), .a_data(SyncRx[0]), .a_data_rdy(Rx_data_ready), .b_rst(IF_rst), .b_clk(IF_clk), .b_data(IF_SyncRx[0]));
	
cdc_mcp #(32) cdc_sync_TX 
  (.a_rst(C125_rst), .a_clk(rx_clock), .a_data(Tx0_phase_word), .a_data_rdy(HP_data_ready), .b_rst(IF_rst), .b_clk(IF_clk), .b_data(IF_phase_word[0]));

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

//---------------------------------------------------------
//              Decode Command & Control data
//---------------------------------------------------------

wire         mode;     			// normal or Class E PA operation 
wire         Attenuator;		// selects input attenuator setting, 1 = 20dB, 0 = 0dB 
wire         IF_duplex;
wire   [7:0] Drive_Level; 		// Tx drive level
wire         Mic_boost;			// Mic boost 0 = 0dB, 1 = 20dB
wire         Line_In;				// Selects input, mic = 0, line = 1
wire         common_merc_freq;		// when set forces Rx2 freq to Rx1 freq
wire   [4:0] Line_In_Gain;		// Sets Line-In Gain value (00000=-32.4 dB to 11111=+12 dB in 1.5 dB steps)
wire         Apollo;				// Selects Alex (0) or Apollo (1)
wire   [4:0] Attenuator0;			// 0-31 dB Heremes attenuator value
wire   [4:0] Attenuator1;
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
wire  [31:0] Rx_phase_word[0:NR-1];	// Rx(n) receive frequency
wire  [31:0] Tx0_phase_word;		// Tx0 transmit frequency
wire  [31:0] Alex_data;				// control data to Alex board
wire  [15:0] Alex_Tx_data;
wire         run;						// set when run active 
wire 		    PC_PTT;					// set when PTT from PC active
wire 	 [7:0] dither;					// Dither for ADC0
wire   [7:0] random;					// Random for ADC0[
wire   [7:0] RxADC[0:NR-1];			// ADC or DAC that Rx(n) is connected to
wire 	[15:0] IFSampleRate[0:NR-1];	// Rxn Sample rate 48/96/192 etc
wire 	[15:0] RxSampleRate[0:NR-1];	// Rxn Sample rate 48/96/192 etc
wire 			 HP_data_ready;		// indicates HP data available
wire         Rx_data_ready;		// indicates Rx_specific data available
wire 			 Tx_data_ready;		// indicated Tx_specific data available
wire   [7:0] Mux;						// Rx in mux mode when bit set, [0] = Rx0, [1] = Rx1 etc 
wire   [7:0] SyncRx[0:NR-1];			// bit set selects Rx to sync or mux with
wire   [7:0] IF_SyncRx[0:NR-1];
wire 	 [7:0] EnableRx0_7;			// Rx enabled when bit set, [0] = Rx0, [1] = Rx1 etc
wire 	 [7:0] IF_EnableRx0_7;
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
wire   [7:0] Mercury_Attenuator;	
wire         CWX;					// CW keyboard from PC 
wire         Dot;					// CW dot key from PC
wire         Dash;					// CW dash key from PC]
wire         freq_data_ready;


///////////////////////////////////////////////////////////////
//
//              Implements Command & Control  encoder 
//
///////////////////////////////////////////////////////////////
/*
	The C&C encoder broadcasts data over the Atlas bus C20 for
	use by other cards e.g. Mercury and Penelope.
	
	The data format is as follows:
	
        <[96]sync0><[95]merc1_2_pres><[94:93]sampling_rate><[92]PTT><[91:89]address><[88:57]frequency><[56:54]clock_source>
        <[53:47]OC><[46:15]Alex_data><[14]DITHER><[13]RAND><[12:5]Drive_Level><[4]Alex_enable[0]><[3:2]RxN_preamps><[1:0]adc2>
	
	Total of 94 bits. OC is the open collector data on Penelope.
	The clock source decodes as follows:
	
	x00  = 10MHz reference from Atlas bus ie Gibraltar
	x01  = 10MHz reference from Penelope
	x10  = 10MHz reference from Mercury
	0xx  = 122.88MHz source from Penelope 
	1xx  = 122.88MHz source from Mercury

		
	For future expansion the three bit address enables specific C&C data to be send to individual boards.
*/

reg  [31:0] IF_phase_word [0:NR+2];
wire [7:0] Alex_enable;
wire [1:0] _10MHz_reference;
wire _128MHz_reference;
wire [101:0] IF_xmit_data;
reg   [2:0] CC_address;     // C&C address  0 - 7 
wire        IF_CC_rdy, IF_CC_pulse;
////////////////////////////////////////////////////////////////////////////////
// Mercury preamp (attenuator) control 
// note legacy control of Merc1 preamp via IF_preamp and RxN_preamps for control of up to 4 Mercury boards
// Turn preamp(s) OFF on Tx when Merc_atten_on_Tx is set; preamp OFF = 20 dB attenuation on Mercury

reg   [7:0] IF_OD;       		// user outputs on Metis DB-9 pins 1-4
reg         IF_RAND;     		// when set randomizer in ADC on Mercury on
reg         IF_DITHER;   		// when set dither in ADC on Mercury on
reg  [1:0] RxN_preamps;		// Mercury boards preamp states (00=all preamps OFF, 01=Merc1 preamp ON, 10=Merc2 preamp ON)

assign RxN_preamps = ~Mercury_Attenuator[1:0];
//assign RxN_preamps = {Attenuator1 >= 6'd20, Attenuator0 >= 6'd20};

reg   [6:0] IF_OC;

pulsegen CC_p   (.sig(IF_CC_rdy), .rst(IF_rst), .clk(IF_clk), .pulse(IF_CC_pulse));

// change address each data transmission 
always @ (posedge IF_clk)
begin
  if (IF_rst)
    CC_address <= #IF_TPD 3'd0;
  else if (IF_CC_pulse) // occurs at each rising edge of IF_CC_rdy
  begin
    if (CC_address == NR)
      CC_address <= #IF_TPD 3'd0; // Penny = 0
    else
      CC_address <= #IF_TPD CC_address + 1'b1; // 1 <= Mercury <= NR
  end
end

//wire set_20Db_atten = FPGA_PTT ? ((atten0_on_Tx) < 5'd20 ? 1'b0 : 1'b1) : ~Merc1_preamp;

/*
        <[101:97]Attn_Ctrl><[96]sync0><[95]merc1_2_pres><[94:93]sampling_rate><[92]PTT><[91:89]address><[88:57]frequency><[56:54]clock_source>
        <[53:47]OC><[46:15]Alex_data><[14]DITHER><[13]RAND><[12:5]Drive_Level><[4]Alex_enable[0]><[3:2]RxN_preamps><[1:0]adc2>
*/
assign IF_xmit_data = {FPGA_PTT?atten0_on_Tx:Attenuator0,IF_SyncRx[0][1],merc1_2_pres,sampling_rate[CC_address],FPGA_PTT,CC_address,IF_phase_word[CC_address],_128MHz_reference,_10MHz_reference,
                       Open_Collector[7:1],IF_Alex_data,IF_DITHER, IF_RAND,Drive_Level,Alex_enable[0],RxN_preamps,IF_RxADC1,IF_RxADC0};

NWire_xmit  #(.OSL(102), .OSW(1), .ICLK_FREQ(48000000), .XCLK_FREQ(48000000), .SEND_FREQ(10000)) 
      CCxmit (.irst(IF_rst), .iclk(IF_clk), .xrst(IF_rst), .xclk(IF_clk),
              .xdata(IF_xmit_data), .xreq(1'b1), .xrdy(IF_CC_rdy), .xack(), .dout(CC));

// we may want to do more specific relay control during TX with this upper 16 bits,
// but for now just validate ANT 1-3 bits, only one should be set
wire Alex_Tx_data_ok = Alex_Tx_data[10:8] == 3'b100 || Alex_Tx_data[10:8] == 3'b010 || Alex_Tx_data[10:8] == 3'b001;

wire [15:0] Alex_upper = (FPGA_PTT && Alex_Tx_data_ok) ? Alex_Tx_data : Alex_data[31:16];

// clear TR relay and Open Collectors if run not set 
wire [31:0] C125_Alex_data = {Alex_upper[15:12], run & PA_enable & (FPGA_PTT | Alex_upper[11]),
                                                             Alex_upper[10:0], Alex_data[15:0]};

//OLD WAY wire [31:0] C125_Alex_data = {Alex_data[31:28], run ? (FPGA_PTT | Alex_data[27]) : 1'b0, Alex_data[26:0]};

wire [31:0] IF_Alex_data;
cdc_mcp #(32) Alex_cdc_inst
  (.a_rst(C125_rst), .a_clk(rx_clock), .a_data(C125_Alex_data), .a_data_rdy(HP_data_ready), .b_rst(IF_rst), .b_clk(IF_clk), .b_data(IF_Alex_data));

`ifdef DONT           
NEED TO FIGURE OUT the Drive_Level, CW_RF dilema, for now PC CW works
//--------------------------------------------------------------------------------------------
//  	Iambic CW Keyer HERMES
//--------------------------------------------------------------------------------------------

wire keyout;

// parameter is clock speed in kHz.
iambic #(48) iambic_inst (.clock(CLRCLK), .cw_speed(keyer_speed),  .iambic(iambic), .keyer_mode(keyer_mode), .weight(keyer_weight), 
                          .letter_space(keyer_spacing), .dot_key(!dot_n | Dot), .dash_key(!dash_n | Dash),
				 .CWX(CWX), .paddle_swap(key_reverse), .keyer_out(keyout));
						  
//--------------------------------------------------------------------------------------------
//  	Calculate  Raised Cosine profile for sidetone and CW envelope when internal CW selected 
//--------------------------------------------------------------------------------------------

wire CW_char;
assign CW_char = (keyout & internal_CW & run);		// set if running, internal_CW is enabled and either CW key is active
wire [15:0] CW_RF;
wire [15:0] profile;
wire CW_PTT;

profile profile_sidetone (.clock(CLRCLK), .CW_char(CW_char), .profile(profile),  .delay(8'd0));
profile profile_CW       (.clock(CLRCLK), .CW_char(CW_char), .profile(CW_RF),    .delay(RF_delay), .hang(hang), .PTT(CW_PTT));

//--------------------------------------------------------
//			Generate CW sidetone with raised cosine profile
//--------------------------------------------------------	
wire signed [15:0] prof_sidetone;
sidetone sidetone_inst( .clock(CLRCLK), .enable(sidetone), .tone_freq(tone_freq), .sidetone_level(sidetone_level), .CW_PTT(CW_PTT),
                        .prof_sidetone(prof_sidetone),  .profile(profile >>> 1));	// divide sidetone profile level by two since only 16 bits used

// select sidetone  when CW key active and sidetone_level is not zero, else Rx audio.
wire [31:0] Rx_audio;
assign Rx_audio = CW_PTT && (sidetone_level != 0) ? {prof_sidetone, prof_sidetone} : LR_data;

//--------------------------------------------------------------------------------------------
//  	Iambic CW Keyer METIS
//--------------------------------------------------------------------------------------------

// When using Penelope RF level is set by level of I&Q. When using FPGA CW I&Q is not used so 
// to adjust power level use the Drive level to vary the level of CW_RF.
wire [15:0]level;
multiply multiply_inst ({Drive_Level,Drive_Level}, CW_RF, level); // 16 x 16 multiply with 16 bit result

// select I&Q data or CW_RF if in CW mode. If Penelope selected then use Drive level to set RF output level. 
wire signed [15:0] I;
wire signed [15:0] Q;
assign  I =  CW_PTT  ? (penny ? level : CW_RF) : IF_I_PWM;   	
assign  Q =  CW_PTT  ? 16'd0 : IF_Q_PWM; 		
`endif

//-----------------------------------------------------------
//  Debounce PTT from Atlas bus OR pin 6 of DB9 (active low)
//-----------------------------------------------------------

wire clean_PTT_in;
debounce de_PTT(.clean_pb(clean_PTT_in), .pb(PTT_in || ~PTT_n), .clk(IF_clk));


//-----------------------------------------------------------
//  Debounce dot key - active low
//-----------------------------------------------------------


debounce de_dot(.clean_pb(debounce_DOT), .pb(~dot_n), .clk(IF_clk));

//-----------------------------------------------------------
//  Debounce dash key - active low
//-----------------------------------------------------------

debounce de_dash(.clean_pb(debounce_DASH), .pb(~dash_n), .clk(IF_clk));


// User outputs 
assign USEROUT0 = IF_OD[0];	// open drain user output
assign USEROUT1 = IF_OD[1];	// open drain user output
assign USEROUT2 = IF_OD[2];	// 3.3V TTL user output
assign USEROUT3 = IF_OD[3];	// 3.3V TTL user output


//-----------------------------------------------------------
//  LED Control  
//-----------------------------------------------------------

/*
	LEDs  - LED[0] is located at the top of the board
	
	LED[0]  	- Displays state of PHY negotiations - fast flash if no Ethernet connection, slow flash if 100T and on if 1000T
	LED[1]		- Lights when the PHY receives Ethernet traffic
	LED[2]  	- Lights when the PHY transmits Ethernet traffic
	LED[3]  	- Lights when an Ethernet broadcast is detected
	LED[4]  	- Lights when traffic to the boards MAC address is detected
	LED[5]  	- Lights when an ARP request is received
	LED[6]  	- Displays state of DHCP negotiations or static IP - on if ACK, slow flash if NAK, fast flash if time out 
				  and long then short flash if static IP
	LED[7]  	- Lights when a ping request is received
	LED[8]  	- Lights when a Metis discovery packet is received
	LED[9]  	- Lights when a Metis discovery packet reply is sent
	LED[10]  	- Lights when 0x7F7F7F received from PC
	
	
	HEART_BEAT  - Flashes once per second
	
	A LED is flashed for the selected period on the positive edge of the signal.
	IF the signal period is greater than the LED period the LED will remain on.
	
	LEDS are connected to the RAM address lines e.g. LED0 = RAM_A0 etc and are active low.

*/

parameter half_second = 10000000; // at 48MHz clock rate

`ifdef DONT
// flash LED1 for ~ 0.2 second whenever the PHY gets data
//Led_flash Flash_LED1(.clock(IF_clk), .signal(PHY_DV), .LED(RAM_A1), .period(half_second)); 	

// flash LED2 for ~ 0.2 second whenever the PHY sends data
//Led_flash Flash_LED2(.clock(IF_clk), .signal(PHY_TX_EN), .LED(RAM_A2), .period(half_second)); 	

// flash LED3 for ~0.2 seconds whenever we detect a broadcast
Led_flash Flash_LED3(.clock(IF_clk), .signal(broadcast), .LED(RAM_A3), .period(half_second));

// flash LED4 for ~0.2 seconds whenever we detect a packet addressed to this MAC address
Led_flash Flash_LED4(.clock(IF_clk), .signal(this_MAC), .LED(RAM_A4), .period(half_second));

// flash LED5 for ~0.2 seconds whenever we detect an ARP request
Led_flash Flash_LED5(.clock(IF_clk), .signal(ARP_request), .LED(RAM_A5), .period(half_second));

// flash LED7 for ~0.2 seconds whenever we detect a ping request
Led_flash Flash_LED7(.clock(IF_clk), .signal(ping_request), .LED(RAM_A7), .period(half_second));

// flash LED8 for ~0.2 seconds whenever we detect a Metis discovery request
Led_flash Flash_LED8(.clock(IF_clk), .signal(METIS_discovery), .LED(RAM_A10), .period(half_second));

// flash LED9 for ~0.2 seconds whenever we detect a Metis discovery reply
Led_flash Flash_LED9(.clock(IF_clk), .signal(METIS_discover_sent), .LED(RAM_A11), .period(half_second));

// flash LED10 for ~0.2 seconds when we have detected sync 
Led_flash Flash_LED10(.clock(IF_clk), .signal(IF_SYNC_state == SYNC_RX_1_2), .LED(RAM_A12), .period(half_second));

// flash LED11 for ~0.2 seconds when we have detected a received sequence error or ASMI is busy
Led_flash Flash_LED11(.clock(IF_clk), .signal(seq_error || busy), .LED(RAM_A13), .period(half_second));  


//------------------------------------------------------------
//   Multi-state LED Control   - code in Led_control is for active LOW LEDs
//------------------------------------------------------------

parameter clock_speed = 25000000; // 25MHz clock 

// display state of PHY negotiations  - fast flash if no Ethernet connection, slow flash if 100T, on if 1000T
// and swap between fast and slow flash if not full duplex
Led_control #(clock_speed) Control_LED0(.clock(clock_12_5MHz), .on(speed_1000T), .fast_flash(~speed_100T || ~speed_1000T),
										.slow_flash(speed_100T), .vary(!duplex), .LED(RAM_A0));
										
// display state of DHCP negotiations - on if ACK, slow flash if NAK, fast flash if time out and swap between fast and slow 
// if using a static IP address
Led_control # (clock_speed) Control_LED1(.clock(clock_12_5MHz), .on(DHCP_ACK), .slow_flash(DHCP_NAK),

//Flash Heart beat LED
reg [26:0]HB_counter;
always @(posedge PHY_CLK125) HB_counter = HB_counter + 1'b1;
assign HEART_BEAT = HB_counter[25];  // Blink
`endif

reg [26:0]HB_counter;
always @(posedge PHY_CLK125) HB_counter = HB_counter + 1'b1;
assign RAM_A4 = HB_counter[25];  // Blink
assign RAM_A5 = ~(IF_SyncRx[0][1]);
assign RAM_A6 = ~(SyncRx[0][1]);

assign RAM_A0 = Alex_enable[0];
assign RAM_A1 = IF_Alex_data[26];
assign RAM_A2 = IF_Alex_data[25];
assign RAM_A3 = IF_Alex_data[24];

function integer clogb2;
input [31:0] depth;
begin
  for(clogb2=0; depth>0; clogb2=clogb2+1)
  depth = depth >> 1;
end
endfunction

endmodule

