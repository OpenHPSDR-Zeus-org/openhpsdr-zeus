/*

	The C&C encoder in Ozy broadcasts data over the Atlas bus (C20) for
	use by other cards e.g. Mercury and Penelope.  The data is in 
	I2S format with the clock being CBLCK and the start of each frame
	being indicated using the negative edge of CLRCLK.
	
	The data format is as follows:
	
	<[58]PTT><[57:54]address><[53:22]frequency><[21:18]clock_select><[17:11]OC><[10]Mode>
	<[9]PGA><[8]DITHER><[7]RAND><[6:5]ATTEN><[4:3]TX_relay><[2]Rout><[1:0]RX_relay> 
	
	for a total of 59 bits. Frequency is in Hz and 32 bit binary format and 
	OC is the open collector data on Penelope. Mode is for a future Class E PA,
	PGA, DITHER and RAND are ADC settings and ATTEN the attenuator on Alex
	
	The clock source (clock_select) decodes as follows:
	
	0x00  = 10MHz reference from Atlas bus ie Gibraltar
	0x01  = 10MHz reference from Penelope
	0x10  = 10MHz reference from Mercury
	00xx  = 122.88MHz source from Penelope 
	01xx  = 122.88MHz source from Mercury 
	
*/

module command_control( CBCLK, CLRCLK, CC, PTT_out, ATTEN, TX_relay, Rout, RX_relay,
						frequency_HZ, ATTRLY, RAND, DITHER, ref_ext, source_122MHZ, ADDRESS);
						  
input CBCLK;
input CLRCLK;
input CC; 							// Command & Control data in
output reg  PTT_out;
output reg   [1:0] ATTEN;          	// attenuator setting on Alex
output reg   [1:0] TX_relay;       	// Tx relay setting on Alex
output reg   Rout;           		// Rx1 out on Alex
output reg   [1:0] RX_relay;       	// Rx relay setting on Alex

output reg   [31:0]frequency_HZ;
output reg   ATTRLY;   				// 1 = Attenuator on, 0 = Preamp on 
output reg   DITHER;    		    // 1 = dither on
output reg   RAND;      		    // 1 = randomizer on 
output ref_ext;       				// if set use on board 10MHz TCXO internally and send to C16; else get from C16
output source_122MHZ; 				// if set use on board 122.88MHz internally and send to LVDS; else get from LVDS
input  wire  [3:0] ADDRESS;			// Mercury C&C address to match 


reg  [5:0] bits;     				// how many bits clocked 
reg  [1:0] CC_state;
reg [58:0] CCdata;					// 59 bits of C&C data

reg   [3:0] clock_select;   		// 10MHz and 122.88MHz clock selection

always @(posedge CBCLK)  			// use CBCLK  from Atlas C8 
begin
  case(CC_state)
  0:
  begin
    if (CLRCLK == 0)
      CC_state  <= 0;     // loop until CLRLCK is high   
    else
      CC_state  <= 1;
  end

  1:
  begin
    if (CLRCLK)
      CC_state  <= 1;     // loop until CLRCLK is low  
    else
    begin
      bits      <= 6'd58;						
      CC_state  <= 2;
    end
  end

  2:
  begin
    CCdata[bits] <= CC;   // this is the second CBCLK after negedge of CLRCLK
    if (bits == 0)
      CC_state  <= 0;      // done so restart
    else
    begin
      bits      <= bits - 1'b1;
      CC_state  <= 2;  
    end
  end

  default:
    CC_state <= 0;
  endcase
end

// decode C & C data into variables and sync to 48kHz LR clock

always @(posedge CBCLK) // stay in same time domain as CCdata so timing analyzer can evaluate correctly
begin
  if ((CC_state == 1) && !CLRCLK) // "negedge CLRCLK" - 1st rising edge of CBCLK after CLRCLK goes low
  begin                           // grab CCdata 1 CBCLK before it starts changing again
    PTT_out     <= CCdata[58];    // PTT from PC via USB 
    if (CCdata[57:54] == ADDRESS) // check that the C&C data is for Mercury
    begin
      frequency_HZ <= CCdata[53:22];
      clock_select <= CCdata[21:18];     
      //OC        <= CCdata[17:11]; // Penelope Open Collectors, not used by Mercury
      ATTRLY    <=  ~CCdata[9];   // 1 = Attenuator on, 0 = Preamp on 
      DITHER    <= CCdata[8];     // 1 = dither on
      RAND      <= CCdata[7];     // 1 = randomizer on 
      ATTEN     <= CCdata[6:5];   // Attenuator setting on Alex
      TX_relay  <= CCdata[4:3];   // Tx relay selection on Alex
      Rout      <= CCdata[2];     // Rx_1_out on Alex
      RX_relay  <= CCdata[1:0];   // Rx relay selection on Alex
    end
  end
end

assign ref_ext = clock_select[1];       // if set use internally and send to C16 else get from C16
assign source_122MHZ = clock_select[2]; // if set use internally and send to LVDS else get from LVDS

endmodule
