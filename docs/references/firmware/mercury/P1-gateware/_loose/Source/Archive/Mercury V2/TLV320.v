//Set up TLV320 using SPI 

/* 

NOTE: TLV320 is set up via SPI rather then I2C since with
a complete system i.e. Mercury, Penelope and Janus, then 
there will be 3 TLV320s and only two options for I2C addresses.

Data to send to TLV320 is 

 	1E 00 - Reset chip
 	12 01 - set digital interface active
 	08 14 - D/A on
 	0C 00 - All chip power on
 	0E 02 - Slave, 16 bit, I2S
 	10 00 - 48k, Normal mode
 	0A 00 - turn D/A mute off

*/

module TLV320(
	input clock,
	output reg nCS,
	output reg MOSI,
	output reg SCLK,
	output CMODE
	);
	
reg   [2:0] load;
reg   [3:0] TLV;
reg  [15:0] TLV_data;
reg   [3:0] bit_cnt;

// Set up TLV320 data to send 
always @*	
begin
  case (load)
  //3'd0: TLV_data = 16'h8889; // simulation test case
  3'd0: TLV_data = 16'h1E00;  // data to load into TLV320
  3'd1: TLV_data = 16'h1201;
  3'd2: TLV_data = 16'h0814;		  // D/A on 
  3'd3: TLV_data = 16'h0C00;
  3'd4: TLV_data = 16'h0E02;
  3'd5: TLV_data = 16'h1000;
  3'd6: TLV_data = 16'h0A00;
  default: TLV_data = 0;
  endcase
end

// State machine to send data to TLV320 via SPI interface

assign CMODE = 1'b1;		// Set to 1 for SPI mode

reg [23:0] tlv_timeout;

always @ (posedge clock)		// use 12.288MHz BCLK for SPI
begin
  if (tlv_timeout != (200*12288))        // 200mS @BCLK = 12.288Mhz
    tlv_timeout <= tlv_timeout + 1'd1;

  case (TLV)
  4'd0:
  begin
    nCS <= 1'b1;        	// set TLV320 CS high
    bit_cnt <= 4'd15;   	// set starting bit count to 15
    if (tlv_timeout == (200*12288)) // wait for 200mS timeout
      TLV <= 4'd1;
    else
      TLV <= 4'd0;
  end

  4'd1:
  begin
    nCS  <= 1'b0;                // start data transfer with nCS low
    MOSI <= TLV_data[bit_cnt];  // set data up
    TLV  <= 4'd2;
  end

  4'd2:
  begin
    SCLK <= 1'b1;               // clock data into TLV320
    TLV  <= 4'd3;
  end

  4'd3:
  begin
    SCLK <= 1'b0;               // reset clock
    TLV  <= 4'd4;
  end

  4'd4:
  begin
    if (bit_cnt == 0) // word transfer is complete, check for any more
      TLV <= 4'd5;
    else
    begin
      bit_cnt <= bit_cnt - 1'b1;
      TLV <= 4'd1;    // go round again
    end
  end

  4'd5:
  begin
    if (load == 6)
    begin                 // stop when all data sent
      TLV <= 4'd5;        // hang out here forever
      nCS <= 1'b1;        // set CS high
    end
    else
    begin                 // else get next data	
      TLV  <= 4'd0;           
      load <= load + 3'b1;  // select next data word to send
    end
  end
  
  default: TLV <= 4'd0;
  endcase
end

endmodule
