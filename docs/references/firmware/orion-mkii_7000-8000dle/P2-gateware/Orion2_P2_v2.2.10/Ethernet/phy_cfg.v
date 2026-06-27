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


//  Metis code copyright 2010, 2011, 2012, 2013 Alex Shovkoplyas, VE3NEA.


// 2021 Updated to support KSZ9021RN and KSZ9031RN on Hermes-Lite 2.0, KF7O.

//-----------------------------------------------------------------------------
// initialize the PHY device on startup
// by writing config data to its MDIO registers; 
// continuously read PHY status from the MDIO registers
//-----------------------------------------------------------------------------

module phy_cfg(
  //input
  input clock,        //2.5 MHZ
  input init_request,
  
  //output
  output reg [1:0] speed,
  output reg duplex,
  output reg is_9031,
  
  //hardware pins
  inout mdio_pin,
  output mdc_pin  
);



//mdio register values
logic [15:0] values [19:0];

//mdio register addresses 
logic [4:0] addresses [19:0];

reg [4:0] word_no;
reg [1:0] phychip;

//-----------------------------------------------------------------------------
//                            state machine
//-----------------------------------------------------------------------------

reg init_required;
wire ready;
wire [15:0] rd_data;
reg rd_request, wr_request;


//state machine  
localparam READING = 1'b0, WRITING = 1'b1;  
reg state = READING;  


always @(posedge clock) begin
  // find out if we're a 9021 or a 9031
  if (!phychip[1])  begin
    word_no <= 5'd19;
    addresses[19] <= 3; 
    values[19] <= 16'hxxxx;

    if (ready) begin
      rd_request <= 1'b1;
      if (rd_data[9:4] == 6'b100001) begin // 9021
        phychip <= 2'b11;
        is_9031 <= 1'b0;
        values[8] <= 16'h0200; // Allow 1GB but don't advertise half duplex in 1000BASET
        values[7] <= 16'h8104;
        values[6] <= 16'h5599; // RGMII Clock and Control Pad Skew
        values[5] <= 16'h8105;
        values[4] <= 16'h6666; // RGMII RX Data Pad Skew
        values[3] <= 16'h8106;
        values[2] <= 16'h8888; // RGMII TX Data Pad Skew
        values[1] <= 16'h1300; // Restart autonegotiation
        values[0] <= 16'hxxxx;
        addresses[8] <= 9;
        addresses[7] <= 11;
        addresses[6] <= 12;
        addresses[5] <= 11;
        addresses[4] <= 12;
        addresses[3] <= 11;
        addresses[2] <= 12;
        addresses[1] <= 0;
        addresses[0] <= 31; 
      end
      else if (rd_data[9:4] == 6'b100010) begin // 9031
        phychip <= 2'b10;
        is_9031 <= 1'b1;
        values[18] <= 16'h0200; // Allow 1GB but don't advertise half duplex in 1000BASET
        values[17] <= 16'h0002;
        values[16] <= 16'h0004;
        values[15] <= 16'h4002;
        values[14] <= 16'h0059; // RGMII Control Signal Pad Skew
        values[13] <= 16'h0002;
        values[12] <= 16'h0005;
        values[11] <= 16'h4002;
        values[10] <= 16'h5555; // RGMII RX Data Pad Skew
        values[9] <= 16'h0002;
        values[8] <= 16'h0006;
        values[7] <= 16'h4002;
        values[6] <= 16'h9999; // RGMII TX Data Pad Skew
        values[5] <= 16'h0002;
        values[4] <= 16'h0008;
        values[3] <= 16'h4002;
        values[2] <= 16'b0000_00_01111_01110; // RGMII Clock Pad Skew TX 5bits RX 5bits
        values[1] <= 16'h1300; // Restart autonegotiation
        values[0] <= 16'hxxxx;
        addresses[18] <= 9;
        addresses[17] <= 5'h0d;
        addresses[16] <= 5'h0e;
        addresses[15] <= 5'h0d;
        addresses[14] <= 5'h0e;
        addresses[13] <= 5'h0d;
        addresses[12] <= 5'h0e;
        addresses[11] <= 5'h0d;
        addresses[10] <= 5'h0e;
        addresses[9] <= 5'h0d;
        addresses[8] <= 5'h0e;
        addresses[7] <= 5'h0d;
        addresses[6] <= 5'h0e;
        addresses[5] <= 5'h0d;
        addresses[4] <= 5'h0e;
        addresses[3] <= 5'h0d;
        addresses[2] <= 5'h0e;
        addresses[1] <= 0;
        addresses[0] <= 31; 
      end
      word_no <= 5'd0;
    end
    else //!ready
      rd_request <= 0;
  end

  else if (init_request) begin
    init_required <= 1'b1;
  end
  
  else if (ready) begin
    case (state)
      READING: begin
        speed <= rd_data[6:5];
        duplex <= rd_data[3];
        
        if (init_required) begin
          wr_request <= 1'b1;
          word_no <= (phychip[0]) ? 5'd8 : 5'd18;
          state <= WRITING;
          init_required <= 1'b0;
        end else begin
          word_no <= 5'd0;
          rd_request <= 1'b1;
          state <= READING;
        end
      end

      WRITING: begin
        if (word_no == 5'd1) state <= READING;
        else wr_request <= 1'b1;
        word_no <= word_no - 5'd1;		  
      end
    endcase
		
  end
  else begin //!ready
    rd_request <= 1'b0;
    wr_request <= 1'b0;
  end
end

        
        
        
        
//-----------------------------------------------------------------------------
//                        MDIO interface to PHY
//-----------------------------------------------------------------------------


mdio mdio_inst (
  .clock(clock), 
  .addr(addresses[word_no]), 
  .rd_request(rd_request),
  .wr_request(wr_request),
  .ready(ready),
  .rd_data(rd_data),
  .wr_data(values[word_no]),
  .mdio_pin(mdio_pin),
  .mdc_pin(mdc_pin)
  );  
  



  
  
endmodule
