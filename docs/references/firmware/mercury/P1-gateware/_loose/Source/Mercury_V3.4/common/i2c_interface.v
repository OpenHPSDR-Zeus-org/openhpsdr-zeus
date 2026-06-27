/////////////////////////////////////////////////////////////////////
////                                                             ////
//// Copyright (C) 2006 Phil Harman VK6APH                       ////
////                    pvharman@arach.net.au                 	  ////
//// Copyright (C) 2013 Joe Martin K5SO								  ////
////                    k5so@valornet.com								  ////
//
//  This program is free software; you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published
//  by the Free Software Foundation; version 1 
//
//  This program is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//  GNU General Public License for more details.
//
//  You should have received a copy of the GNU General Public License
//  along with this program; if not, write to the Free Software
//  Foundation, Inc., 59 Temple Place, Suite 330, Boston, MA  02111-1307  USA
//
//  This design is LGPL software written for the HPSDR project
//  by Joe Martin K5SO and Phil Harman VK6APH.  The authors assume no libability on how the
//  code might be used or warranty that the code is fit for any 
//  particular application. 
//
//  The purpose of the code module is to serve as a sequencer/interface to the I2C slave module. 
//
//  Sends data to/from the I2C slave module.


module i2c_interface (CLK, sda, scl, version_no, ADC_overload, address);

input 		CLK;  		//12.288 MHz
inout 		sda;
input 		scl;
input 	  [7:0] version_no;
input 		ADC_overload;
input 	  [6:0] address;	// i2c address for this board

reg 		reset;
wire	  [7:0] int_reg0; 	// received data [15:8] and when single data byte sent from PC
wire 	  [7:0] int_reg1; 	// received data [7:0]
reg 	  [7:0] rcv_reg0;	// send data [15:8]
reg 	  [7:0] rcv_reg1; 	// send data [7:0]
wire 		sda_out;	// internal sda, for sda output
wire		sda_oe;		// sda output enable
wire 		dg_scl;		// de-glitched scl 
wire 		dg_sda;		// de-glitched sda	
reg 	  [6:0] slave_addr;	// address that Metis/Ozy master module is targeting


// data to send to Metis/Ozy 
//
always @ (posedge CLK) begin

	if (slave_addr == address) begin
		rcv_reg0 <= {7'b0, !ADC_overload};		// high byte, ...invert ADC overload bit for transmission
		rcv_reg1 <= version_no;						// low byte
	end
	
end


// reset logic 
//
reg [5:0]reset_count;

always @ (posedge CLK)
begin
	if (reset_count[5]) begin
		reset <= 1'b1;		// reset high to run
	end
	else begin
		reset <= 0;
		reset_count <= reset_count + 1'b1;
	end
end 



// de-glitch sda and scl using CLK
// sda and scl have to be stable for 4 consecutive CLK samples before a transition is considered valid
//
deglitch deglitch_scl(.clock(CLK), .in(scl),.out(dg_scl));
deglitch deglitch_sda(.clock(CLK), .in(sda),.out(dg_sda));


//I2C slave module	
//
i2c_slave(.rst_n(reset), .slave_addr(slave_addr), .address(address), 
		.sda_i(dg_sda), .sda_out(sda_out),.sda_oe(sda_oe),.scl_in(dg_scl), 
		.int_reg0(LED),.int_reg1(), .rcv_reg0(rcv_reg0), .rcv_reg1(rcv_reg1)); 
			
			
// sda can pull to 0v but must go high Z to signal a logic 1 
//
assign sda = sda_oe ? (sda_out ? 1'bz : 1'b0) : 1'bz;	
		

endmodule