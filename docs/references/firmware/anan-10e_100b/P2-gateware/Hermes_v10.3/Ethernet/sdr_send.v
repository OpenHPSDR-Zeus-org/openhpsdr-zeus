//-----------------------------------------------------------------------------
//                          sdr send
//-----------------------------------------------------------------------------

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


//  Metis code copyright 2010, 2011, 2012, 2013, 2014, 2015 Phil Harman VK6(A)PH


module sdr_send(
	input tx_clock,
	input udp_tx_active,
	input run,
	input wideband,
	input [47:0]local_mac,
	input [7:0] code_version,
	input discovery, 
	input [7:0]Rx_data[0:NR-1] /* ramstyle = "logic" */,			// Rx data to send to PHY
	input sp_data_ready,
   input [7:0] sp_fifo_rddata,
	input udp_tx_enable,
	input erase_done,						// set to send EPCS16 erased to PC
	input send_more,						// set to request more EPCS16 data
	input [7:0]Mic_data,
	input fifo_ready[0:NR-1],
	input mic_fifo_ready,
	input CC_data_ready,
	input [7:0]CC_data[0:56],
	input [31:0] sequence_number,		// sequence number to send when programming and requesting more data.	
   input [15:0]samples_per_frame[0:NR-1],	
	input [15:0]tx_length[0:NR-1],		// length of the UDP packet, varies with number of sync or mux receivers.
	input [7:0] Wideband_packets_per_frame,  // 
	output reg udp_tx_request,
	output [7:0] udp_tx_data,
	output reg [15:0] udp_tx_length,
	output reg fifo_rdreq[0:NR-1],	// high to indicate read from Rx fifo required
	output reg sp_fifo_rdreq,	   	// high to indicate read from wideband spectrum fifo required
	output reg mic_fifo_rdreq,			// high to indicate read from mic fifo required
	output reg send_more_ACK,
	output reg erase_done_ACK,
	output reg [7:0] port_ID,
	output reg CC_ack,
	output reg WB_ack,
	output reg phy_ready					// this set when no DDC data is being sent. Used when changing modes for PureSignal

);

parameter board_type,
			 NR,
			 master_clock,
			 protocol_version;

localparam
	IDLE 			= 12'd0,					// *** does this really have a postive effect?
	WIDEBAND    = 12'd1,
	WIDEBAND_2  = 12'd2,
	RX_SEND		= 12'd4,
	RX_SEND_2   = 12'd8,
	response    = 12'd16,
	SEND        = 12'd32,
	MIC_SEND    = 12'd64,
	MIC_SEND_2  = 12'd128,
	CC_SEND     = 12'd256,
	CC_SEND_2   = 12'd512,
	BITS_PER_SAMPLE = 16'd24,
	SAMPLES_FRAME = 16'd238;
	
localparam 
	HEADER_LENGTH  = 16'd22,					// number of bytes in response  udp header
	PC_LENGTH		= 16'd4,						// number of bytes in sequence number
	RES_H_BIT    	= HEADER_LENGTH*8 -1,  	// response header bits
	PC_H_BIT       = PC_LENGTH*8 -1,
	FRAME				= 8'h01; 					// HPSDR Frame type

	
//(* ramstyle = "logic" *) reg [31:0] Rx_sequence_number[0:7];	// use registers for speed.
reg [31:0] Rx_sequence_number[0:NR-1];	
reg [31:0] spec_seq_number = 0;
reg [31:0] mic_seq_number = 0;
reg [31:0] CC_seq_number = 0;
reg [7:0]  status;
reg [7:0]  wideband_count = 0;  // max of 255 blocks = 255 * 512 = 130,560 samples 
reg send_wb_block;
reg [7:0]  select;					// used to select data on mux and demux. *** increase when more Rx added *****
reg [7:0] tx_data;
reg [63:0] time_stamp = 0;
reg [15:0] bits_per_sample;
reg [15:0] samples_frame;
wire [15:0] UDP_lenght[0:NR-1];

// response payload followed by 50 x 0x01
reg [RES_H_BIT:0] response_tx_bits; // new protocol

//shift register
reg [RES_H_BIT:0] response_shift_reg;
reg [PC_H_BIT:0] PC_shift_reg;

reg [10:0] byte_no = 0;
reg [11:0] state = 0;
reg send_response = 0;
reg [$clog2(NR):0]i,j,k;


reg [28:0] stuck_cnt = 29'd0;
wire [7:0] number_Rx = NR;
wire [31:0] clock_frequency = master_clock;

always @(posedge tx_clock) 
  begin
  if ((state > IDLE) && !run) begin				// prevents code getting stuck when run not active
      if (stuck_cnt < 29'd250000000)
          stuck_cnt <= stuck_cnt + 29'b1;
      else begin
          state <= IDLE;
          stuck_cnt <= 29'd0;
      end
  end

  case (state)
		IDLE: 
			begin
				udp_tx_request <= 1'b0;
				send_response <= 1'b0;
				byte_no <= 11'd0; 
				port_ID <= 8'd0;										// set base to 1024
				if (!send_more)  send_more_ACK  <= 1'b0;		// clear ACK when sdr_receiver has seen our ACK
				if (!erase_done) erase_done_ACK <= 1'b0;		// clear ACK when ASMI_interface has seen our ACK
				//if (discovery & !run) begin						// *** don't reply to Discovery if all ready running *****
				if (discovery) begin
					response_tx_bits <= {32'd0, (8'd02 + {7'b0,run}), local_mac, board_type, protocol_version, code_version,  48'd0, number_Rx, 8'd1}; 	// sequence number set to zero for future use
					state <= response;			// **** change HEADER_LENGTH when making changes here ****
				end 
				else if (erase_done) begin
					response_tx_bits <= {32'd0, 8'd03, local_mac, code_version, board_type, 72'd0}; 	// sequence number set to zero for future use
					state <= response;
				end 
				else if (send_more) begin
					response_tx_bits <= {sequence_number, 8'd04, local_mac, code_version, board_type, 72'd0}; 
					state <= response;
				end 
						
				else if (run && CC_data_ready) state <= CC_SEND;    // highest priority data 
				
				else if (run && mic_fifo_ready) state <= MIC_SEND;
				
				else if (run && wideband && sp_data_ready) state <= WIDEBAND;  // do for first WB packet
				
				else if (run)				
				begin 				
						for (j = 0; j < NR + 1 ; j++)
						begin 
								if (j == NR) begin									// // send wideband data only when all  DDC data has been sent 
										if (run && wideband & (send_wb_block))
										begin 
											state <= WIDEBAND;
											break;
										end 
								end 
						
								else if (fifo_ready[j])
								begin
								//	udp_tx_length <= tx_length[j]; //PC_LENGTH + (samples_per_frame[k] * 16'd6) + 12;	16'd1444	
									udp_tx_length <= 16'd1444;		
									port_ID <= 8'd11 + j;									// set from_port to base + j i.e. 1035 + j
									Rx_sequence_number[j] <= Rx_sequence_number[j] + 32'b1;
									select <= j;
								//	samples_frame <= samples_per_frame[j];  // 16'd238
								//	bits_per_sample <= 16'd24;
								//	samples_frame <= 16'd238;
									state <= RX_SEND;
									break;
								end
						end					
				end 
					
		//		else if (run && wideband & (send_wb_block | sp_data_ready)) state <= WIDEBAND;	
						
				else  if (!run) begin 						// not running so reset all sequence numbers.
					for (i = 0 ; i < NR; i++)
					begin 
						Rx_sequence_number[i] <= -32'd1;
					end
					
   					mic_seq_number <= 32'd0;
						CC_seq_number <= 32'd0;
						spec_seq_number <= 32'd0;
						wideband_count <= 8'd0;
						send_wb_block <= 1'b0;
				end 
				if (!wideband) spec_seq_number <= 32'd0;
			end
			
		CC_SEND:
			begin 
				udp_tx_length <= 16'd56 + PC_LENGTH;				
				udp_tx_request <= 1'b1;	
				port_ID <= 8'd1;								// set from_port to base + 1 i.e. 1025
				CC_ack <= 1'b1;								// ACK we have seen the request 
				if (udp_tx_enable) begin
					tx_data <= CC_seq_number[31:24]; 	// ***** first byte gets loaded here as soon as udp_tx_enable is set !!!!! 
					state <= CC_SEND_2; 
				end 		
			end 

		CC_SEND_2:
			begin	
			  CC_ack <= 1'b0;
			  if (byte_no < udp_tx_length) begin    	
				if (udp_tx_active) begin
					case (byte_no)
					 0: tx_data <= CC_seq_number[23:16];					
					 1: tx_data <= CC_seq_number[15:8]; 
					 2: tx_data <= CC_seq_number[7:0];
					endcase
				
				if (byte_no > 2) tx_data <=  CC_data[byte_no - 3];
				byte_no <= byte_no + 11'd1;
			  end 
		   end
			else  begin  // all data sent so increment sequence number 
					CC_ack <= 1'b0;
					CC_seq_number <= CC_seq_number + 32'd1;
					state <= IDLE; 		// all data sent so return to start
					end
			end			

	
		WIDEBAND:
			begin
				udp_tx_length <= 16'd1024 + PC_LENGTH;				
				udp_tx_request <= 1'b1;	
				port_ID <= 8'd3;								// set from_port to base + 3 i.e. 1027
				WB_ack <= 1'b1; 								// ACK that we have seen the request
				if (udp_tx_enable) begin
					if (wideband_count == (Wideband_packets_per_frame - 8'd1)) begin 
						wideband_count <= 0;
						send_wb_block <= 0;    				// send last frame of wideband data 
					end
					else if (wideband_count == 0) begin
						spec_seq_number <= 0;   			// starting new set of wideband samples
						send_wb_block <= 1'b1;
						wideband_count <= 8'b1;
					end 
					else wideband_count <= wideband_count + 8'd1;
				tx_data <= spec_seq_number[31:24]; 	// ***** first byte gets loaded here as soon as udp_tx_enable is set !!!!! 
				state <= WIDEBAND_2; 
				end 
			end
			
		WIDEBAND_2:
			begin
			  WB_ack <= 1'b0; 
			  if (byte_no < udp_tx_length) begin    // spec_seq_number	
				if (udp_tx_active) begin
					case (byte_no)
						0: tx_data <= spec_seq_number[23:16];					
						1: begin
							tx_data <= spec_seq_number[15:8];
							sp_fifo_rdreq <= 1'b1;
							end 						
						2: tx_data <= spec_seq_number[7:0];	
  udp_tx_length - 3: sp_fifo_rdreq <= 1'b0;
					endcase
					
				if (byte_no > 2) tx_data <= sp_fifo_rddata;
				byte_no <= byte_no + 11'd1;
			  end 
		   end
			else  begin  // all data sent so increment sequence number and block count 
					spec_seq_number <= spec_seq_number + 32'b1; 
					state <= IDLE; 		// all data sent so return to start
					end
		end			
			
		RX_SEND:
			begin
			//	port_ID <= 8'd11 + select;
				udp_tx_request <= 1'b1;
				if (udp_tx_enable) begin
				//	tx_data <= temp_sequence_number[31:24];	// ***** first byte gets loaded here as soon as udp_tx_enable is set !!!!! 
					tx_data <= Rx_sequence_number[select][31:24];
					state <= RX_SEND_2; 
				end 
			end	
			
		RX_SEND_2:
			begin	
			  if (byte_no < udp_tx_length) begin 	
				if (udp_tx_active) begin
					case (byte_no)
						 0: tx_data <= Rx_sequence_number[select][23:16];					
						 1: tx_data <= Rx_sequence_number[select][15:8]; 
						 2: tx_data <= Rx_sequence_number[select][7:0];
						 3: tx_data <= time_stamp[63:56];				// Add time stamp
						 4: tx_data <= time_stamp[55:48];
						 5: tx_data <= time_stamp[47:40];
						 6: tx_data <= time_stamp[39:32];
						 7: tx_data <= time_stamp[31:24];
						 8: tx_data <= time_stamp[23:16];
						 9: tx_data <= time_stamp[15:8];
						10: tx_data <= time_stamp[7:0];
						11: tx_data <= BITS_PER_SAMPLE[15:8];			// Add bits per sample
						12: tx_data <= BITS_PER_SAMPLE[7:0];
						13: begin
							   tx_data <= SAMPLES_FRAME[15:8];			// Add number of samples  16'd238;
								fifo_rdreq[select] <= 1'b1;				// set fifo read true one clock before needed 
							 end 							
						14: tx_data <= SAMPLES_FRAME[7:0];
						
   udp_tx_length - 3: fifo_rdreq[select] <= 1'b0;					// set fifo read false one clock before needed 		   				 
					endcase
				
				if (byte_no > 14) tx_data <=  Rx_data[select];
				byte_no <= byte_no + 11'd1;
			  end 
		   end
			else  state <= IDLE; 		// all data sent so return to start
			end			
				
			
		MIC_SEND:
			begin 
				udp_tx_length <= 16'd128 + PC_LENGTH;	// was 1440 			
				udp_tx_request <= 1'b1;	
				port_ID <= 8'd2;								// set from_port to base + 2 i.e. 1026
				if (udp_tx_enable) begin
					tx_data <= mic_seq_number[23:16]; 	// ***** first byte gets loaded here as soon as udp_tx_enable is set !!!!! 
					state <= MIC_SEND_2; 
				end 
			end
			
		MIC_SEND_2:
			begin	
			  if (byte_no < udp_tx_length) begin    // spec_seq_number	
				if (udp_tx_active) begin
					case (byte_no)
						0: tx_data <= mic_seq_number[23:16];					
						1: begin
							tx_data <= mic_seq_number[15:8];
							mic_fifo_rdreq <= 1'b1;
							end 						
						2: tx_data <= mic_seq_number[7:0];	
  udp_tx_length - 3: mic_fifo_rdreq <= 1'b0;
					endcase
					
				if (byte_no > 2) tx_data <= Mic_data;
				byte_no <= byte_no + 11'd1;
			  end 
		   end
			else  begin  // all data sent so increment sequence number 
					mic_seq_number <= mic_seq_number + 32'b1; 
					state <= IDLE; 		// all data sent so return to start
					end
		end			
						
		response:  
			begin 
				udp_tx_length <= 16'd38 + HEADER_LENGTH;	// Total of 60 bytes (was 16'd50)
				response_shift_reg <= response_tx_bits;
				udp_tx_request <= 1'b1;	
				if (udp_tx_enable) begin
					if (send_more)	 send_more_ACK  <= 1'b1;  // only send ACKs when Tx is not busy
					if (erase_done) erase_done_ACK <= 1'b1;
					send_response <= 1'b1;
					state <= SEND;	
				end
			end
							
		SEND:
			begin
				if (byte_no < udp_tx_length) begin
					if (udp_tx_active) begin
						response_shift_reg <=  {response_shift_reg[RES_H_BIT-8:0], 8'b0};  // was board_type
						byte_no <= byte_no + 11'd1;
					end 
				end
				else state <= IDLE; 		// all data sent so return to start
			end 

		default: state <= IDLE;

					
		endcase
	end 	

	
	// tx_data needs to be available before udp_tx_active received
	assign udp_tx_data = send_response  ? response_shift_reg[RES_H_BIT -: 8] :  tx_data;
	
	// need to know when we can switch from normal to multiplexed mode for PS and Diversity modes.
	// Phy should not be ready sending or have data pending. 
	
	assign phy_ready = (state != RX_SEND && state != RX_SEND_2  && !fifo_ready[0]);
	
endmodule

	