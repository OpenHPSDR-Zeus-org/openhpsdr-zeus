//-----------------------------------------------------------------------------
//                          Rx_fifo_ctrl.v
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


//  copyright 2010, 2011, 2012, 2013, 2014, 2015 Phil Harman VK6PH


/* Convert 48 bits to 8 for new Ethernet protocol
   NOTE:  At power on the FIFO will fill since no data is being requested by the PC.
   In which case need to check that the fifo is full and if so clear it. 

	The module works as follows:  When spd_rdy (Rx FIFO has data) then the I&Q data from 
	the receiver specified by Rx_number is sent to the PHY.
	
	The module then checks to see if data from another receiver(s) is required for synchronus or
	multiplex requirements.  This is done by checking the bits in Sync.  A set bit indicates 
	that the I&Q data relating to the position of the bit needs to be sent e.g.
	
		bit[0] = 1 sends Rx0 data
		bit[1] = 1 sends Rx1 data
		bit[2] = 1 sends Rx2 data etc
		
	If no bits are set then the code loops to the start.



*/

			

module Rx_fifo_ctrl(
	input clock,
	input reset,
	input [15:0] SampleRate,
	input [23:0] data_in_I,
	input [23:0] data_in_Q,
	input [23:0] data_in_IDAC,
	input [23:0] data_in_QDAC,
	input [23:0] Sync_data_in_I,			
	input [23:0] Sync_data_in_Q,
	input spd_rdy,
	input spd_rdy2,
	input spd_rdy3,
	input fifo_full,
	input Sync,			// set if Sync active
	input ps,			// set if puresignal active
	
	output reg wrenable,
	output reg [8:0] data_out,
	output reg fifo_clear
	);
	
parameter NR;
	
reg [4:0]state;
reg prevSync;
reg [23:0] tmp_Sync_data_in_I;
reg [23:0] tmp_Sync_data_in_Q;
reg [23:0] tmp_data_in_I;
reg [23:0] tmp_data_in_Q;
reg [23:0] tmp_data_in_IDAC;
reg [23:0] tmp_data_in_QDAC;
reg data_avail1, data_avail2, data_avail3;

always @ (posedge clock)
begin 

if (reset) begin
	fifo_clear <= 1'b1;
	wrenable <= 1'b0;
	state <= 0;
end

else begin 
	if(spd_rdy && !data_avail1) begin
		tmp_Sync_data_in_I <= Sync_data_in_I;
		tmp_Sync_data_in_Q <= Sync_data_in_Q;
		data_avail1 <= 1'b1;
	end

	if(spd_rdy2 && !data_avail2) begin
		tmp_data_in_I <= data_in_I;
		tmp_data_in_Q <= data_in_Q;
		data_avail2 <= 1'b1;
	end

	if(spd_rdy3 && !data_avail3) begin 													
		tmp_data_in_IDAC <= data_in_IDAC;
		tmp_data_in_QDAC <= data_in_QDAC;
		data_avail3 <= 1'b1;
	end

	case(state)
	
	0:	begin
			fifo_clear <= 1'b0;
			state <= 1;
		end 
	
	1:	begin
			if (prevSync != Sync) begin
				prevSync <= Sync;
				fifo_clear <= 1'b1;
				wrenable <= 1'b0;
				state <= 0;
			end
			else if(spd_rdy) begin 													
				wrenable <= 1'b1;
				data_out <= {1'b1, Sync_data_in_I[23:16]};
				state <= 2;
			end
		end 
		
	2:	begin
			data_out <= {1'b0, tmp_Sync_data_in_I[15:8]};
			state <= 3;
		end		
		
	3:	begin
			data_out <= {1'b0, tmp_Sync_data_in_I[7:0]};
			state <= 4;
		end
	
	4:	begin
			data_out <= {1'b0, tmp_Sync_data_in_Q[23:16]};
			state <= 5;
		end

	5:	begin
			data_out <= {1'b0, tmp_Sync_data_in_Q[15:8]};
			data_avail1 <= 1'b0;
			state <= 6;
		end	
		
	6:	begin
			data_out <= {1'b0, tmp_Sync_data_in_Q[7:0]};
			state <= 7;
		end	

	// base receiver 	data sent so stop sending to FIFO until we see if sync or mux data required.
	7:	begin 
			if (!Sync) begin
				wrenable <= 1'b0; 
				if (!spd_rdy) state <= 1;	// wait for spd_rdy to drop then continue
			end 
			else if (ps) begin
				data_out <= {1'b1, tmp_data_in_IDAC[23:16]};
				state <= 14;
			end 
			else begin
				data_out <= {1'b1, tmp_data_in_I[23:16]};
				state <= 8;
			end 
		end	

	8:	begin
			data_out <= {1'b0, tmp_data_in_I[15:8]};
			state <= 9;
		end		
		
	9:	begin
			data_out <= {1'b0, tmp_data_in_I[7:0]};
			state <= 10;
		end
		
	10:	begin
			data_out <= {1'b0, tmp_data_in_Q[23:16]};
			state <= 11;
		end

	11:	begin
			data_out <= {1'b0, tmp_data_in_Q[15:8]};
			data_avail2 <= 1'b0;
			state <= 12;
		end	
		
	12:	begin
			data_out <= {1'b0, tmp_data_in_Q[7:0]};
			state <= 13;
		end
		
	13: 	begin 		
			wrenable <= 1'b0; 
			state <= 1;	// wait for spd_rdy to drop then continue
		end  

	14:	begin
			data_out <= {1'b0, tmp_data_in_IDAC[15:8]};
			state <= 16;
		end		
		
	16:	begin
			data_out <= {1'b0, tmp_data_in_IDAC[7:0]};
			state <= 17;
		end
		
	17:	begin
			data_out <= {1'b0, tmp_data_in_QDAC[23:16]};
			state <= 18;
		end

	18:	begin
			data_out <= {1'b0, tmp_data_in_QDAC[15:8]};
			data_avail3 <= 1'b0;
			state <= 19;
		end	
		
	19:	begin
			data_out <= {1'b0, tmp_data_in_QDAC[7:0]};
			state <= 20;
		end
		
	20: 	begin 		
			wrenable <= 1'b0; 
			state <= 1;	// wait for spd_rdy to drop then continue
		end  

	default: state <= 0;
	endcase
	end	
end

//assign convert_state = (!spd_rdy && state == 5'd1);   // code is waiting for new data

	
endmodule

