/***********************************************************
*
*	Mercury DDC receiver 
*
************************************************************/
// 
// Copyright 2009 Phil Harman VK6APH 
// V2.8 20 August 2009 Copyright 2009 Kirk Weedman KD7IRS


//  HPSDR - High Performance Software Defined Radio
//
//  Mercury to Atlas bus interface.
//
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


/* 	This program interfaces the LT2208 to a PC over USB.
	The data from the LT2208 is in 16 bit parallel format and 
	is valid at the positive edge of the LT2208 122.88MHz clock.
	
	The data is processed by a CORDIC NCO to produce I and Q
	outputs.  These are decimated by 320/640/1280 in CIC filters then by 2 
	in an  CFIR filter to give output data at 192/96/48kHz to feed
	the Atlas bus in nWire format for tranfer via Ozy to a suitable PC program.
	
	Change log:
	
     15 Feb 2009 - new rewite
     18 May 2009 - release as V2.7 - uses cordic rather than kordic since fewer spurs
	 20 Aug 2009 - Added C&C Address = 1, changed C&C min speed to 5000
	             - release as V2.8			
	 
*/
	
module Mercury(OSC_10MHZ, ext_10MHZ,C122_clk,INA,CC,ATTRLY,A6,A12,C21,C23,C24,MDOUT,CDIN,TLV320_BCLK,TLV320_LRCIN,
               TLV320_LRCOUT,TLV320_MCLK,CMODE,MOSI,SCLK,nCS,SPI_data,SPI_clock,Tx_load_strobe,Rx_load_strobe,
               FPGA_PLL,LVDS_TXE,LVDS_RXE_N,OVERFLOW,DITHER,SHDN,PGA,RAND,INIT_DONE,TEST0,TEST1,TEST2,TEST3,
               DEBUG_LED0,DEBUG_LED1,DEBUG_LED2,DEBUG_LED3,DEBUG_LED4,DEBUG_LED5,DEBUG_LED6,DEBUG_LED7 ); 

input  wire        OSC_10MHZ;      // 10MHz TCXO input 
inout  tri         ext_10MHZ;      // 10MHz reference to/from Atlas pin C16
input  wire        C122_clk;       // 122.88MHz clock from LT2208
input  wire [15:0] INA;            // samples from LT2208
input  wire        CC;             // Command & Control from Atlas C20
output reg         ATTRLY;         // Antenna relay control
output wire        A6;             // Mercury serial number/ADC overload
output wire        A12;            // Mercury spectrum data
input  wire        C21;            // trigger signal from Ozy to get spectrum data
output wire        C23;            // M_LR_sync - see LRAudio NWire_xmit interface in Ozy
input  wire        C24;            // M_LR_data (Rx audio) from Atlas bus
output wire        MDOUT;          // I/Q out to Atlas bus on A10  
output wire        CDIN;           // Rx audio out to TLV320
output wire        TLV320_BCLK;    // 3.072MHz BCLK for TLV320
output wire        TLV320_LRCIN;   // 48KHz L/R clock for TLV320
output wire        TLV320_LRCOUT;  // ditto 
output wire        TLV320_MCLK;    // 12.288MHz master clock
output wire        CMODE;          // SPI interface to TLV320
output reg         MOSI;           // SPI interface to TLV320
output reg         SCLK;           // SPI interface to TLV320
output reg         nCS;            // SPI interface to TLV320
output wire        SPI_data;       // SPI data to Alex
output wire        SPI_clock;      // SPI clock to Alex
output wire        Tx_load_strobe; // SPI Tx data load strobe to Alex
output wire        Rx_load_strobe; // SPI Rx data load strobe to Alex
output wire        FPGA_PLL;       // PLL control volts to loop filter 
output wire        LVDS_TXE;       // LVDS Tx enable
output wire        LVDS_RXE_N;     // LVDS Rx enable
input  wire        OVERFLOW;       // ADC overflow bit
output reg         DITHER;         // ADC dither control bit
output wire        SHDN;           // ADC shutdown bit
output wire        PGA;            // ADC preamp gain
output reg         RAND;           // ADC ramdonizer bit
output wire        INIT_DONE;      // INIT_DONE LED 
output wire        TEST0;          // Test point 
output wire        TEST1;          // Test point
output wire        TEST2;          // Test point
output wire        TEST3;          // Test point
output wire        DEBUG_LED0;     // Debug LED
output wire        DEBUG_LED1;     // Debug LED
output wire        DEBUG_LED2;     // Debug LED
output wire        DEBUG_LED3;     // Debug LED
output wire        DEBUG_LED4;     // Debug LED
output wire        DEBUG_LED5;     // Debug LED
output wire        DEBUG_LED6;     // Debug LED
output wire        DEBUG_LED7;     // Debug LED

parameter C122_TPD = 2.1;

localparam SERIAL = 8'd28;  // software version serial number - 2.8

reg  [15:0] temp_ADC;
reg         data_ready;     // set at end of decimation

assign SHDN       = 1'b0;	// 0 = normal operation
assign INIT_DONE  = 1'b0;	// turn INIT_DONE LED on
assign PGA        = 1'b0; 	// 1 = gain of 1.5(3dB), 0 = gain of 1

//////////////////////////////////////////////////////////////
//
//		Reset
//
//////////////////////////////////////////////////////////////

reg C122_rst;
reg [10:0] C122_rst_cnt;

always @(posedge C122_clk)
begin
  if (!C122_rst_cnt[10])
    C122_rst_cnt <= #C122_TPD C122_rst_cnt + 1'b1;

  C122_rst <= #C122_TPD C122_rst_cnt[10] ? 1'b0 : 1'b1;
end

//////////////////////////////////////////////////////////////
// A Digital Output Randomizer is fitted to the LT2208. This complements bits 15 to 1 if 
// bit 0 is 1. This helps to reduce any pickup by the A/D input of the digital outputs. 
// We need to de-ramdomize the LT2208 data if this is turned on. 
//////////////////////////////////////////////////////////////

always @ (posedge C122_clk) 
begin 
  if (RAND)
  begin	// RAND set so de-ramdomize
    if (INA[0])
      temp_ADC <= {~INA[15:1],INA[0]};
    else
      temp_ADC <= INA;
  end
  else
    temp_ADC <= INA;  // not set so just copy data
end 

//////////////////////////////////////////////////////////////
//
// 		Set up TLV320 using SPI 
//
/////////////////////////////////////////////////////////////

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

//always @ (posedge CBCLK)		// CBCLK for SPI
always @ (posedge BCLK)		// BCLK for SPI
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

//////////////////////////////////////////////////////////////
//
//		CLOCKS
//
//////////////////////////////////////////////////////////////

localparam SPEED_48K = 2'b00;

reg        C122_DFS1, C122_DFS0;
wire       C122_cbrise, C122_cbfall;
wire       source_122MHZ;  // Set when internal 122.88MHz source is used and sent to LVDS
reg        C122_cgen_rst;
reg  [1:0] C122_SPEED;

// create a slower system clock = 122.88Mhz / 10 = 12.288Mhz
clk_div TLVCLK (.reset(C122_rst), .clk_in(C122_clk), .clk_out(TLV320_MCLK));

// make sure CBCLK/CLRCLK and BCLK/LRCLK stay synced when speed changes
always @(posedge C122_clk)
begin
  if (C122_rst)
    C122_SPEED <= 2'b00;
  else if (C122_SPEED != {C122_DFS1,C122_DFS0})
    C122_SPEED <= {C122_DFS1,C122_DFS0};

  if (C122_rst)
    C122_cgen_rst <= 1'b1;
  else if (C122_SPEED != {C122_DFS1,C122_DFS0})
    C122_cgen_rst <= 1'b1;
  else
    C122_cgen_rst <= 1'b0;
end

wire C122_cbclk, CLRCLK;
clk_lrclk_gen clrgen (.reset(C122_cgen_rst), .CLK_IN(C122_clk), .BCLK(C122_cbclk),
                      .Brise(C122_cbrise), .Bfall(C122_cbfall), .LRCLK(CLRCLK), .Speed(SPEED_48K));

assign TLV320_BCLK   = C122_cbclk;
assign TLV320_LRCIN  = CLRCLK;
assign TLV320_LRCOUT = CLRCLK;

wire BCLK;
clk_lrclk_gen lrgen (.reset(C122_cgen_rst), .CLK_IN(C122_clk), .BCLK(BCLK),  .Speed(C122_SPEED));

	
// Generate C122_cbclk/4 for SPI interface
wire      SPI_clk;
reg       [1:0] spc;

always @(posedge C122_clk)
begin
  if (C122_rst)
    spc <= 2'b00;
  else if (C122_cbclk)
    spc <= spc + 2'b01;
end

assign SPI_clk = spc[1];

// Select 122.88MHz source. If source_122MHZ == 0 then use Penelope's 122.88MHz clock and send to LVDS
// Otherwise get external clock from LVDS

assign LVDS_RXE_N = source_122MHZ ? 1'b1 : 1'b0;  // enable LVDS receiver if clock is external
assign LVDS_TXE   = source_122MHZ ? 1'b1 : 1'b0;  // enable LVDS transmitter if Mercury is the source 

// select 10MHz reference source. If ref_ext is set use Mercury's 10MHz ref and send to Atlas C16
wire reference;
wire ref_ext;			// Set when internal 10MHz reference sent to Atlas C16

assign reference = ref_ext ? OSC_10MHZ : ext_10MHZ; 
assign ext_10MHZ = ref_ext ? OSC_10MHZ : 1'bz; 		// C16 is bidirectional so set high Z if input. 


//////////////////////////////////////////////////////////////
//
//		Get LROUT (L/R Audio) data and then synchronize it to CBCLK/CLRCLK 
//
//////////////////////////////////////////////////////////////
wire        C122_LR_rdy;
wire [31:0] C122_LR_data;

NWire_rcv #(.DATA_BITS(32), .ICLK_FREQ(122880000), .XCLK_FREQ(122880000), .SLOWEST_FREQ(10000))
        LRAudio (.irst(C122_rst), .iclk(C122_clk), .xrst(C122_rst), .xclk(C122_clk),
                 .xrcv_rdy(C122_LR_rdy), .xrcv_ack(C122_LR_rdy), .xrcv_data(C122_LR_data),
                 .din(C24));

TestModeChecker tmc (C122_rst, C122_clk, C122_LR_data, C122_LR_rdy);
                
assign C23 = CLRCLK; // M_LR_sync -> so Ozy knows when to send M_LR_data

I2S_xmit #(.DATA_BITS(32))  // CLRCLK running at 48KHz
  LR (.rst(C122_rst), .lrclk(CLRCLK), .clk(C122_clk), .CBrise(C122_cbrise),
      .CBfall(C122_cbfall), .sample(C122_LR_data), .outbit(CDIN));
//////////////////////////////////////////////////////////////
//
//		Convert frequency to phase word 
//
//////////////////////////////////////////////////////////////

/*	
	Calculates  ratio = fo/fs = frequency/122.88Mhz where frequency is in MHz
	Each calculation should take no more than 1 CBCLK
*/

reg   [31:0] C122_frequency_HZ;   // frequency control bits for CORDIC
reg   [31:0] C122_last_freq;
reg   [31:0] C122_sync_phase_word;
wire  [63:0] C122_ratio;

localparam M2 = 32'd1172812403;  // B57 = 2^57.   M2 = B57/122880000

assign C122_ratio = C122_frequency_HZ * M2; // B0 * B57 number = B57 number

always @ (posedge C122_clk)
begin
  if (C122_cbrise) // time between C122_cbrise is enough for ratio calculation to settle
  begin
    C122_last_freq <= C122_frequency_HZ;
    if (C122_last_freq != C122_frequency_HZ) // frequency changed)
      C122_sync_phase_word <= C122_ratio[56:25]; // B57 -> B32 number since R is always >= 0  
  end
end

//------------------------------------------------------------------------------
//                 All DSP code is in the Receiver module
//------------------------------------------------------------------------------
wire  [23:0] rx_I;
wire  [23:0] rx_Q;
wire         strobe;

receiver receiver_inst(
  //control
  .clock(C122_clk),
  .rate({C122_DFS1, C122_DFS0}), //00=48, 01=96, 10=192 kHz
  .frequency(C122_sync_phase_word),
  .out_strobe(strobe),
  //input
  .in_data(temp_ADC),
  //output
  .out_data_I(rx_I),
  .out_data_Q(rx_Q)
  );

// send I and Q data to MDOUT (Atlas C10)
NWire_xmit #(.SEND_FREQ(192000), .DATA_BITS(48), .ICLK_FREQ(122880000), .XCLK_FREQ(122880000), .LOW_TIME(1'b0))
       M_IQ (.irst(C122_rst), .iclk(C122_clk), .xrst(C122_rst), .xclk(C122_clk),
             .xdata({rx_I,rx_Q}), .xreq(strobe), .xrdy(), .xack(), .dout(MDOUT));

///////////////////////////////////////////////////////////
//
//    Spectrum Data over NWire to Ozy
//
///////////////////////////////////////////////////////////
wire [15:0] spd_data;
wire        spd_req, spd_rdy, spd_ack;
wire        spf_wreq, spf_rreq, spf_full, spf_empty;
wire        trigger;

assign trigger = C21;

NWire_xmit #(.SEND_FREQ(48000), .DATA_BITS(16), .ICLK_FREQ(122880000), .XCLK_FREQ(122880000))
        SPD (.irst(C122_rst), .iclk(C122_clk), .xrst(C122_rst), .xclk(C122_clk),
             .xdata(spd_data), .xreq(spd_req), .xrdy(spd_rdy), .xack(spd_ack), .dout(A12));

SP_fifo SPF (.sclr(C122_rst), .clock (C122_clk), .full(spf_full), .empty(spf_empty), 
             .wrreq (spf_wreq), .data (temp_ADC), .rdreq (spf_rreq), .q(spd_data) );

sp_xmit_ctrl
        SPC (.rst(C122_rst), .clk(C122_clk), .trigger(trigger), .fifo_full(spf_full),
             .fifo_empty(spf_empty), .fifo_wreq(spf_wreq), .fifo_rreq(spf_rreq),
             .xfer_req(spd_req), .xfer_rdy(spd_rdy), .xfer_ack(spd_ack) );

///////////////////////////////////////////////////////////
//
//    Serial Number & ADC Overload 
//
///////////////////////////////////////////////////////////

// Sends current software serial # as an 8 bit value.

wire [8:0] xmit_data;

assign xmit_data = {SERIAL,OVERFLOW}; // serial number, ADC overload (overflow)

NWire_xmit  #(.DATA_BITS(9), .ICLK_FREQ(122880000), .XCLK_FREQ(122880000), .SEND_FREQ(1000)) 
      ser_no (.irst(C122_rst), .iclk(C122_clk), .xrst(C122_rst), .xclk(C122_clk),
              .xdata(xmit_data), .xreq(1'b1), .xrdy(), .xack(), .dout(A6));
///////////////////////////////////////////////////////////
//
//    Command and Control Decoder 
//
///////////////////////////////////////////////////////////
/*

	The C&C encoder in Ozy broadcasts data over the Atlas bus (C20) for
	use by other cards e.g. Mercury and Penelope.
	
	The data format is as follows:
	
	<[60:59]DFS1,DFS0><[58]PTT><[57:54]address><[53:22]frequency><[21:18]clock_select>
	<[17:11]OC><[10]Mode><[9]PGA><[8]DITHER><[7]RAND><[6:5]ATTEN><[4:3]TX_relay>
	<[2]Rout><[1:0]RX_relay> 
	
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
wire   [60:0] C122_rcv_data;
wire          C122_rcv_rdy;

reg           C122_PTT_out;
reg     [3:0] clock_select;   // 10MHz and 122.88MHz clock selection
//reg     [6:0] OC;             // Only Penelope uses this
reg     [1:0] C122_ATTEN;     // attenuator setting on Alex
reg     [1:0] C122_TX_relay;  // Tx relay setting on Alex
reg           C122_Rout;      // Rx1 out on Alex
reg     [1:0] C122_RX_relay;  // Rx relay setting on Alex
reg           C122_new_data;

localparam MERCURY_ADDR = 4'b1; // Address for data


always @ (posedge C122_clk)
begin
  if (C122_rst)
  begin
    C122_new_data     <= 1'b0;
    C122_DFS1         <= 1'b0;   // I/Q sampling rate selection
    C122_DFS0         <= 1'b0;
    C122_PTT_out      <= 1'b0;   // PTT from PC via USB 
    C122_frequency_HZ <= 32'b0;
    clock_select      <= 4'b0000;     
    ATTRLY            <= 1'b0;   // 1 = Attenuator on, 0 = Preamp on 
    DITHER            <= 1'b0;   // 1 = dither on
    RAND              <= 1'b0;   // 1 = randomizer on 
    C122_ATTEN        <= 2'b00;  // Attenuator setting on Alex
    C122_TX_relay     <= 2'b00;  // Tx relay selection on Alex
    C122_Rout         <= 1'b0;   // Rx_1_out on Alex
    C122_RX_relay     <= 2'b00;  // Rx relay selection on Alex
  end
  else if (C122_rcv_rdy)
  begin
    C122_new_data <= 1'b1;            // only 1 C122_clk wide
    C122_DFS1     <= C122_rcv_data[60];
    C122_DFS0     <= C122_rcv_data[59];    // I/Q sampling rate selection
    C122_PTT_out  <= C122_rcv_data[58];    // PTT from PC via USB 
    if (C122_rcv_data[57:54] == MERCURY_ADDR) // check that the C&C data is for this board
    begin
      C122_frequency_HZ <= C122_rcv_data[53:22];
      clock_select      <= C122_rcv_data[21:18];     
      //OC               <= C122_rcv_data[17:11]; // Penelope Open Collectors, not used by Mercury
      ATTRLY            <= ~C122_rcv_data[9];    // 1 = Attenuator on, 0 = Preamp on 
      DITHER            <= C122_rcv_data[8];     // 1 = dither on
      RAND              <= C122_rcv_data[7];     // 1 = randomizer on 
      C122_ATTEN        <= C122_rcv_data[6:5];   // Attenuator setting on Alex
      C122_TX_relay     <= C122_rcv_data[4:3];   // Tx relay selection on Alex
      C122_Rout         <= C122_rcv_data[2];     // Rx_1_out on Alex
      C122_RX_relay     <= C122_rcv_data[1:0];   // Rx relay selection on Alex
    end
  end
  else
    C122_new_data <= 1'b0;
end

NWire_rcv  #(.DATA_BITS(61), .ICLK_FREQ(122880000), .XCLK_FREQ(122880000), .SLOWEST_FREQ(5000)) 
      CCrcv (.irst(C122_rst), .iclk(C122_clk), .xrst(C122_rst), .xclk(C122_clk),
             .xrcv_data(C122_rcv_data), .xrcv_rdy(C122_rcv_rdy), .xrcv_ack(C122_rcv_rdy), .din(CC));



assign ref_ext = clock_select[1];       // if set use internally and send to C16 else get from C16
assign source_122MHZ = (clock_select[3:2] == 2'b01); // if set use internally and send to LVDS else get from LVDS

//////////////////////////////////////////////////////////////
//
//		Alex Filter selection
//
//	The frequency sent by PowerSDR is the indicated frequency
//  less the 9kHz IF. In order to select filters at the correct
//  frequency we need to add the IF offset to the current frequency.
//
//////////////////////////////////////////////////////////////

wire  [6:0] C122_LPF;
wire  [5:0] C122_select_HPF;
wire [31:0] C122_frequency_plus_IF; // add PowerSDR IF frequecy (9kHz) to current frequency

assign C122_frequency_plus_IF = C122_frequency_HZ + 32'd9000; // add 9kHz IF offset 

LPF_select Alex_LPF_select(.clock(C122_clk), .frequency(C122_frequency_plus_IF), .LPF(C122_LPF));
HPF_select Alex_HPF_select(.clock(C122_clk), .frequency(C122_frequency_plus_IF), .HPF(C122_select_HPF));

//////////////////////////////////////////////////////////////
//
//		Alex Antenna relay selection
//
//		Antenna relays decode as follows
//
//		TX_relay[1:0]	Antenna selected
//			00			Tx 1
//			01			Tx 2
//			10			Tx 3
//
//		RX_relay[1:0]	Antenna selected
//			00			None
//			01			Rx 1
//			10			Rx 2
//			11			Transverter
//
//		Rout			Rx_1_out
//			0			Not selected
//			1			Selected
//
//////////////////////////////////////////////////////////////

wire C122_ANT1;			
wire C122_ANT2;
wire C122_ANT3;
wire C122_Rx_1_out;
wire C122_Transverter;
wire C122_Rx_2_in;
wire C122_Rx_1_in;

assign C122_Rx_1_out = C122_Rout;

assign C122_ANT1 = (C122_TX_relay == 2'b00) ? 1'b1 : 1'b0; // select Tx antenna 1
assign C122_ANT2 = (C122_TX_relay == 2'b01) ? 1'b1 : 1'b0; // select Tx antenna 2
assign C122_ANT3 = (C122_TX_relay == 2'b10) ? 1'b1 : 1'b0; // select Tx antenna 3

assign C122_Rx_1_in     = (C122_RX_relay == 2'b01) ? 1'b1 : 1'b0; // select Rx antenna 1
assign C122_Rx_2_in     = (C122_RX_relay == 2'b10) ? 1'b1 : 1'b0; // select Rx antenna 2
assign C122_Transverter = (C122_RX_relay == 2'b11) ? 1'b1 : 1'b0; // select Transverter input 


//////////////////////////////////////////////////////////////
//
//		Alex SPI interface
//
//////////////////////////////////////////////////////////////

localparam  TX_YELLOW_LED = 1'b1;
localparam  RX_YELLOW_LED = 1'b1;

wire        C122_6m_preamp;
wire        C122_Tx_red_led;
wire        C122_Rx_red_led;
wire        C122_TR_relay;
wire [15:0] C122_Alex_Tx_data;
wire [15:0] C122_Alex_Rx_data;

// assign attenuators
wire C122_10dB_atten = C122_ATTEN[0];
wire C122_20dB_atten = C122_ATTEN[1];

// define and concatinate the Tx data to send to Alex via SPI
assign C122_Tx_red_led = ~C122_PTT_out; // turn red led on when we Tx
assign C122_TR_relay   = C122_PTT_out; // turn on TR relay when PTT active

assign C122_Alex_Tx_data = {C122_LPF[6:4], C122_Tx_red_led, C122_TR_relay, C122_ANT3, C122_ANT2,
                       C122_ANT1, C122_LPF[3:0], TX_YELLOW_LED, 3'b000};

// define and concatinate the Rx data to send to Alex via SPI
assign C122_Rx_red_led = C122_PTT_out;	// turn red led on when we Rx

// turn 6m preamp on if frequency > 50MHz 
assign C122_6m_preamp = (C122_frequency_plus_IF > 50000000) ? 1'b1 : 1'b0;

// if 6m preamp selected disconnect all filters 
wire [5:0] C122_HPF;
assign C122_HPF = C122_6m_preamp ? 6'd0 : C122_select_HPF; 

// V3 Alex hardware
assign C122_Alex_Rx_data = {C122_Rx_red_led, C122_10dB_atten, C122_20dB_atten, C122_HPF[5],
                       C122_Rx_1_out, C122_Rx_1_in, C122_Rx_2_in, C122_Transverter, 1'b0,
                       C122_HPF[4:2], C122_6m_preamp, C122_HPF[1:0], RX_YELLOW_LED};
					   
// concatinate Tx and Rx data and send to SPI interface. SPI interface only sends on a change of Alex_data.
// All data is sent in about 120uS.
wire [31:0] C122_Alex_data;
reg  [31:0] SPI_Alex_data;

assign C122_Alex_data = {C122_Alex_Tx_data[15:0], C122_Alex_Rx_data[15:0]};

// we are changing signals from the C122_ domain into the slower SPI_domain
// We'll use a handshake double buffered method since time between changes is many clocks
// Note that C122_HPF and C_122_LPF change one C122_clk later than the other Alex_data
// (i.e. C122_TR_relay ... C122_10db_atten, ... C122_ANT1, etc)  see LPF_select and HPF_select
reg C122_Alex_req;
reg SPI_Alex_ack;

always @ (posedge C122_clk)
begin: C122_ALEX_HANDSHAKE
  reg ack;
  reg C122_Alex_ack;
  
  if (C122_rst)
    C122_Alex_req <= 1'b0;
  else if (C122_new_data)
    C122_Alex_req <= 1'b1;
  else if (C122_Alex_ack)
    C122_Alex_req <= 1'b0;

  if (C122_rst)
    {C122_Alex_ack, ack} <= 2'b00;
  else
    {C122_Alex_ack, ack} <= {ack, SPI_Alex_ack};
end

always @ (posedge SPI_clk)
begin: SPI_ALEX_HANDSHAKE
  reg a2, a1, a0;
  reg [31:0] ad0, ad1;

  {ad1, ad0} <= {ad0, C122_Alex_data}; // double buffer Alex data

  // triple buffer since this MUST come right at or after the data change
  {a2, SPI_Alex_ack, a1, a0} <= {SPI_Alex_ack, a1, a0, C122_Alex_req};

  if (SPI_Alex_ack & !a2)
    SPI_Alex_data <= ad1;
// since data changes slowly, and is stable at this point, grabbing C122_Alex_data
// here instead of ad1 is OK.  It would also eliminate the double buffer of ad1, ad0
end

SPI Alex_SPI_Tx (.Alex_data(SPI_Alex_data), .SPI_data(SPI_data),
                 .SPI_clock(SPI_clock), .Tx_load_strobe(Tx_load_strobe),
                 .Rx_load_strobe(Rx_load_strobe), .spi_clock(SPI_clk));					
													
///////////////////////////////////////////////////////////
//
//    PLL 
//
///////////////////////////////////////////////////////////

/* 
	Divide the 10MHz reference and 122.88MHz clock to give 80kHz signals.
	Apply these to an EXOR phase detector. If the 10MHz reference is not
	present the EXOR output will be a 80kHz square wave. When passed through 
	the loop filter this will provide a dc level of (3.3/2)v which will
	set the 122.88MHz VCXO to its nominal frequency.
	The selection of the internal or external 10MHz reference for the PLL
	is made using ref_ext.
*/

// div 10 MHz ref clock by 125 to get 80 khz 

wire ref_80khz; 
reg osc_80khz; 

oddClockDivider refClockDivider(reference, ref_80khz); 

// Divide  122.88 MHz by 1536 to get 80 khz 
reg [9:0] count_12288; 

always @ (posedge C122_clk)
begin
  if (count_12288 == 767)
  begin
    count_12288 <= 0;
    osc_80khz   <= ~osc_80khz; 
  end
  else
    count_12288 <= count_12288 + 1'b1;
end

// NOTE: If external reference is not available then phase detector 
// will be fed with 80kHz from 122.88MHz clock. Loop filter will 
// set VCXO control volts to 3.3v/2

// Apply to EXOR phase detector 
assign FPGA_PLL = ref_80khz ^ osc_80khz; 


// LEDs for testing 0 = off, 1 = on
assign DEBUG_LED0 = OVERFLOW; 		// LED 0 on when ADC Overflow

// check for correct Alex relay selection

assign DEBUG_LED3 = C122_TX_relay[0]; 
assign DEBUG_LED4 = C122_TX_relay[1];
assign DEBUG_LED5 = C122_RX_relay[0];
assign DEBUG_LED6 = C122_RX_relay[1];
assign DEBUG_LED7 = C122_Rout;


// Test pins
assign TEST0 = osc_80khz; // 80kHz from 122.88MHz
assign TEST1 = ref_80khz; // 80kHz from 10MHz
assign TEST2 = FPGA_PLL;  // phase detector output
assign TEST3 = 1'b0; 

//------------------------------------------------------------------------------
//                          blink!
//------------------------------------------------------------------------------
reg [26:0]counter;
always @(posedge C122_clk) counter = counter + 1'b1;
assign {DEBUG_LED2,DEBUG_LED1} = counter[25:24];  // faster flash for this version!


endmodule 



