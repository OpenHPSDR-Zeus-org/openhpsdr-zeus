///////////////////////////////////////////////////////////
//
//    Command and Control Decoder 
//
///////////////////////////////////////////////////////////
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
	
*/

module CC_decoder(CBCLK, CLRCLK, CC_IN, PTT_out, clock_select, OC, ATTEN, TX_relay,
                  Rout, RX_relay, frequency_HZ, ATTRLY, RAND, DITHER);
                  
parameter ADDRESS = 4'b0; // Address for data

input  wire         CBCLK;
input  wire         CLRCLK;
input  wire         CC_IN;
output reg          PTT_out;
output reg    [3:0] clock_select;   // 10MHz and 122.88MHz clock selection
output reg    [6:0] OC;             // Only Penelope uses this
output reg    [1:0] ATTEN;          // attenuator setting on Alex
output reg    [1:0] TX_relay;       // Tx relay setting on Alex
output reg          Rout;           // Rx1 out on Alex
output reg    [1:0] RX_relay;       // Rx relay setting on Alex
output reg   [31:0] frequency_HZ;
output reg          ATTRLY;
output reg          RAND;
output reg          DITHER;


// internal variable
reg  [5:0] bits;      // how many bits clocked 
reg [58:0] CCdata;	  // 59 bits of C&C data
reg  [1:0] CC_state;

localparam CC_IDLE  = 0,
           CC_LR_HI = 1,
           CC_LR_LO = 2;
always @(posedge CBCLK)
begin
  case(CC_state)
  CC_IDLE:
  begin
    if (CLRCLK)
      CC_state  <= CC_LR_HI;
    else
      CC_state  <= CC_IDLE;     // loop until CLRLCK is high   
  end

  CC_LR_HI:
  begin
    if (!CLRCLK)
    begin
      bits      <= 6'd58;						
      CC_state  <= CC_LR_LO;
    end
    else
      CC_state  <= CC_LR_HI;    // loop until CLRCLK is low  
  end

  CC_LR_LO:
  begin
    CCdata <= {CCdata[57:0], CC_IN};  // this is the second CBCLK after negedge of CLRCLK

    if (bits == 0)
      CC_state  <= CC_IDLE;     // done so restart
    else
    begin
      bits      <= bits - 1'b1;
      CC_state  <= CC_LR_LO;  
    end
  end

  default:
    CC_state <= CC_IDLE;
  endcase
end

// decode C & C data into variables
always @(posedge CBCLK)
begin
  if ((CC_state == CC_LR_HI) && !CLRCLK) // 1st rising edge of CBCLK after CLRCLK goes low
  begin                           // grab CCdata 1 CBCLK before it starts changing again
    PTT_out     <= CCdata[58];    // PTT from PC via USB 
    if (CCdata[57:54] == ADDRESS) // check that the C&C data is for this board
    begin
      frequency_HZ <= CCdata[53:22];
      clock_select <= CCdata[21:18];     
      OC        <= CCdata[17:11]; // Penelope Open Collectors, not used by Mercury
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

endmodule