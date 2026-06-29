/***********************************************************
*
*	Mercury DDC receiver 
*
************************************************************/
// 
// Copyright (C) 2009, 2011, 2012, 2013 Phil Harman VK6APH 
// V2.8 20 August 2009 Copyright (C) 2009 Kirk Weedman KD7IRS
// Copyright (C) 2013 Joe Martin K5SO
//
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


/* 	This program ...
	
	Change log:
	
     28 Jan 2024 - new rewite

*/
	
module Mercury(OSC_10MHZ, ext_10MHZ,AUX_CLK, C122_clk,INA,CC,ATTRLY,A6,A12,C19,C21,C22,C23,C24,MDOUT_I,MDOUT_Q,CDIN,
			   TLV320_BCLK,TLV320_LRCIN,TLV320_LRCOUT,TLV320_MCLK,CMODE,MOSI,SCLK,nCS,SPI_data,SPI_clock,
			   Tx_load_strobe,Rx_load_strobe,FPGA_PLL,LVDS_TXE,LVDS_RXE_N,OVERFLOW,DITHER,SHDN,PGA,RAND,INIT_DONE,
			   TEST0,TEST1,TEST2,TEST3,DEBUG_LED0,DEBUG_LED1,DEBUG_LED2,DEBUG_LED3,DEBUG_LED4,DEBUG_LED5,
			   DEBUG_LED6,DEBUG_LED7, Merc_ID,Merc_ID_drv, MULTIPLE_MERC, SDA, SCL); 

input  wire        OSC_10MHZ;      // 10MHz TCXO input 
inout  tri         ext_10MHZ;      // 10MHz reference to/from Atlas pin C16
input  wire 	   AUX_CLK;		   // 10MHz reference from Excalibur 
input  wire        C122_clk;       // 122.88MHz clock from LT2208
input  wire [15:0] INA;            // samples from LT2208
input  wire        CC;             // Command & Control from Atlas C20
output wire        ATTRLY;         // Antenna relay control
output wire        A12;            // Mercury spectrum data
input  wire        A6;
input  wire        C19;
input  wire        C21;            // trigger signal from Ozy to get spectrum data
inout  wire        C22;            // trigger signal from Penny @ 192k
output wire        C23;            // M_LR_sync - see LRAudio NWire_xmit interface in Ozy
input  wire        C24;            // M_LR_data (Rx audio) from Atlas bus
output reg   [5:0] MDOUT_I;        // I data out to Atlas bus on A16, A17, A7, A8, A9, A10
output reg   [5:0] MDOUT_Q;        // Q data out to Atlas bus on A14, A15, A2, A3, A4, A5
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
input  wire  [2:0] Merc_ID;        // GPIO 9,7,5
output wire  [3:0] Merc_ID_drv;    // GPIO 8,6,4,2
input  wire        MULTIPLE_MERC;  // GPIO 3...Merc board hardware jumper input to specify multiple-Mercury-board mode
inout  wire        SDA;				  // i2c SDA bus line, Atlas A21, FPGA pin 65
input  wire        SCL;				  // i2c SCL bus line, Atlas A20, FPGA pin 69

parameter C122_TPD = 2.1;

localparam SERIAL = 8'd40; // software version serial number

reg  [15:0] temp_ADC;
reg         data_ready;     // set at end of decimation
reg         Rx_preamp;      // 1 = preamp ON (no on-board attenuator), 0 = preamp OFF (20 dB attenuator inserted) 

assign Merc_ID_drv = 4'b1111;          // drive these pins high

assign SHDN       = 1'b0;	// 0 = normal operation
assign INIT_DONE  = 1'b0;	// turn INIT_DONE LED on
assign ATTRLY     = ~Rx_preamp;	
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

NOTE: TLV320 is set up via SPI rather than I2C since with
a complete system i.e. Mercury, Penelope and Janus, then 
there will be 3 TLV320s and only two options for TLV320 addresses.

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

reg  [1:0] C122_DFS [0:NR];
wire       C122_cbrise, C122_cbfall;
wire       source_122MHZ;  // Set when internal 122.88MHz source is used and sent to LVDS
reg        C122_cgen_rst;
reg  [1:0] C122_SPEED;

// create a slower system clock = 122.88Mhz / 10 = 12.288Mhz
clk_div TLVCLK (.reset(C122_rst), .clk_in(C122_clk), .clk_out(TLV320_MCLK));

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

always @(posedge C122_cbclk)
begin
  if (C122_rst)
    spc <= 2'b00;
  else  spc <= spc + 2'b01;
end

assign SPI_clk = spc[1];


//////////////////////////////////////////////////////////////
//
//		Get LROUT (L/R Audio) data and then synchronize it to CBCLK/CLRCLK 
//
//////////////////////////////////////////////////////////////
wire        C122_LR_rdy;
wire [31:0] C122_LR_data;

NWire_rcv #(.OSL(32), .OSW(1), .ICLK_FREQ(122880000), .XCLK_FREQ(122880000), .SLOWEST_FREQ(10000))
        LRAudio (.irst(C122_rst), .iclk(C122_clk), .xrst(C122_rst), .xclk(C122_clk),
                 .xrcv_rdy(C122_LR_rdy), .xrcv_ack(C122_LR_rdy), .xrcv_data(C122_LR_data),
                 .din(C24));

//assign C23 = CLRCLK; // M_LR_sync -> so Ozy knows when to send M_LR_data
assign C23 = (Merc_ID == 3'b000) ? CLRCLK : 1'bz; // M_LR_sync -> so Ozy knows when to send M_LR_data

I2S_xmit #(.DATA_BITS(32))  // CLRCLK running at 48KHz
  LR (.rst(C122_rst), .lrclk(CLRCLK), .clk(C122_clk), .CBrise(C122_cbrise),
      .CBfall(C122_cbfall), .sample(C122_LR_data), .outbit(CDIN));

		
		
////////////////////////////////////////////////////////////////////////////////////////
//
//       i2c bus interface, used to send firmware version number and ADC overflow status
//       over an asynchronous two-line format using the Atlas SDA/SCL lines
//
////////////////////////////////////////////////////////////////////////////////////////
wire [6:0] i2c_address;

// determine the appropriate 7-bit i2c address for this Mercury board
//
// arbitararily assigned i2c addresses are: 
// 	Merc1 board i2c address = 7'h10
// 	Merc2 board i2c address = 7'h11
// 	Merc3 board i2c address = 7'h12
// 	Merc4 board i2c address = 7'h13
//
// i2c address for this board is set by using an offset (7'h1x) and the GPIO 9, 7, 5 jumpers (Merc_ID) to
// obtain one of the addresses shown above	
//	
assign i2c_address = {4'b0010, Merc_ID};	

i2c_interface i2c_interface(.CLK(TLV320_MCLK), .sda(SDA), .scl(SCL), .version_no(SERIAL), 
                                         .ADC_overload(OVERFLOW), .address(i2c_address));
		
localparam NR = 2; // number of receivers to implement, plus 2 more for 2nd mercury

reg       [31:0] C122_sync_phase_word [0:NR+2];
wire      [23:0] rx_I [0:NR-1];
wire      [23:0] rx_Q [0:NR-1];
wire             strobe [0:NR-1];
wire    [NR-1:0] MDO_I;
wire    [NR-1:0] MDO_Q;

//------------------------------------------------------------------------------
//                 IQ TX data for pure signal (same as Penny)
//------------------------------------------------------------------------------

wire signed [47:0] IQ_sync_data;
reg signed [47:0] C122_cic_iq;

// get TX IQ data the same way Penny does
always @ (posedge C122_clk)
begin 
        if (IQ_rdy & req1) begin 
          C122_cic_iq <= IQ_sync_data;
          do_ack <= 1'b1;         
          IQ_ack <= 1'b1;
        end
        else if (do_ack) begin
          do_ack <= 1'b0;
        end
        else if (IQ_ack) IQ_ack <= 1'b0;
end 

wire       IQ_rdy;
reg        IQ_ack, do_ack;

NWire_rcv #(.OSL(48), .OSW(1), .ICLK_FREQ(122880000), .XCLK_FREQ(192000), .SLOWEST_FREQ(10000))
     IPWM (.irst(C122_rst), .iclk(C122_clk), .xrst(C122_rst), .xclk(clk192),
            .xrcv_rdy(IQ_rdy), .xrcv_ack(IQ_rdy), .xrcv_data(IQ_sync_data),
            .din(C19));

assign C22 = clock_select[1] ? pll192: 1'bz; // P_IQ_sync
wire clk192 = clock_select[1] ? pll192 : C22;

wire req1;
wire [16:0] y2_r, y2_i;

CicInterpM5 #(.RRRR(640), .IBITS(24), .OBITS(17), .GBITS(38)) in2 (C122_clk, 1'd1, req1, C122_cic_iq[47:24], C122_cic_iq[23:0], y2_r, y2_i);

// Code rotates input at set frequency and produces I & Q
cpl_cordic # (.IN_WIDTH(17))
                cordic_inst (.clock(C122_clk), .frequency(C122_sync_phase_word[0]), .in_data_I(y2_i), 
                .in_data_Q(y2_r), .out_data_I(C122_cordic_i_out), .out_data_Q());    

wire signed [21:0] C122_cordic_i_out;
reg [15:0] temp_DACD; // for pure signal Tx
reg [5:0] sampling_rate [0:NR+2];

generate
  genvar c;
  for (c = 0; c < NR; c = c + 1) // NR Mercury Data Channels/Receivers plus DAC data
   begin: MDC 
// send I and Q data to MDO_I and MDO_Q then to MDOUT_I and MDOUT_Q via the assigns below
NWire_xmit #(.SEND_FREQ(384000), .OSL(24), .OSW(1), .ICLK_FREQ(122880000),
             .XCLK_FREQ(122880000), .LOW_TIME(1'b0))
       M_I (.irst(C122_rst), .iclk(C122_clk), .xrst(C122_rst), .xclk(C122_clk),
             .xdata(rx_I[c]), .xreq(strobe[c]), .xrdy(), .xack(), .dout(MDO_I[c]));

NWire_xmit #(.SEND_FREQ(384000), .OSL(24), .OSW(1), .ICLK_FREQ(122880000),
             .XCLK_FREQ(122880000), .LOW_TIME(1'b0))
       M_Q (.irst(C122_rst), .iclk(C122_clk), .xrst(C122_rst), .xclk(C122_clk),
             .xdata(rx_Q[c]), .xreq(strobe[c]), .xrdy(), .xack(), .dout(MDO_Q[c]));
  end
endgenerate


`ifdef DONT
//assign  MDOUT_I = {ATLAS_A16,ATLAS_A17,ATLAS_A7,ATLAS_A8,ATLAS_A9,ATLAS_A10};
assign MDOUT_I[0] = pll192;
assign MDOUT_I[1] = pll192;
assign MDOUT_I[2] = pll192;
assign MDOUT_I[3] = pll192;
assign MDOUT_I[4] = pll192; BAD
assign MDOUT_I[5] = pll192;
//assign  MDOUT_Q = {ATLAS_A14,ATLAS_A15,ATLAS_A2,ATLAS_A3,ATLAS_A4,ATLAS_A5};
assign MDOUT_Q[0] = pll192;
assign MDOUT_Q[1] = pll192;
assign MDOUT_Q[2] = pll192; BAD
assign MDOUT_Q[3] = pll192; BAD
assign MDOUT_Q[4] = pll192;
assign MDOUT_Q[5] = pll192;
//Propose:
//assign  MDOUT_I = {ATLAS_A16,ATLAS_A9,ATLAS_A7,ATLAS_A8,ATLAS_A17,ATLAS_A10};
//assign  MDOUT_Q = {ATLAS_A2,ATLAS_A3,ATLAS_A14,ATLAS_A15,ATLAS_A4,ATLAS_A5};
`endif

// merc1 has DDC 0&1 ADC0
// if 2 merc boards present, merc2 can supply DDC 0&1 ADC1
reg [2:0] f0, f1;
reg [2:0] sr0, sr1;
reg [15:0] in_data;
always @ (posedge C122_clk) begin
    temp_DACD <= {C122_cordic_i_out[21:8], 2'b00}; //PS

    if (merc1_2_pres) begin
      if (Merc_ID == 3'b000) begin
        MDOUT_I[0] <= adc2[0] ? 1'bz : MDO_I[0]; MDOUT_Q[0] <= adc2[0] ? 1'bz : MDO_Q[0];
        MDOUT_I[1] <= adc2[1] ? 1'bz : MDO_I[1]; MDOUT_Q[1] <= adc2[1] ? 1'bz : MDO_Q[1];
      end
      else if (Merc_ID == 3'b001) begin
        MDOUT_I[0] <= adc2[0] ? MDO_I[0] : 1'bz; MDOUT_Q[0] <= adc2[0] ? MDO_Q[0] : 1'bz;
        MDOUT_I[1] <= adc2[1] ? MDO_I[1] : 1'bz; MDOUT_Q[1] <= adc2[1] ? MDO_Q[1] : 1'bz;
      end
    end
    else if (Merc_ID == 3'b000) begin
      MDOUT_I[0] <= MDO_I[0]; MDOUT_Q[0] <= MDO_Q[0];
      MDOUT_I[1] <= MDO_I[1]; MDOUT_Q[1] <= MDO_Q[1];
    end
    else begin
      MDOUT_I[0] <= 1'bz;
      MDOUT_I[1] <= 1'bz;
    end

    // below currently unused
    MDOUT_I[2] <= 1'bz; MDOUT_Q[2] <= 1'bz;
    MDOUT_I[3] <= 1'bz; MDOUT_Q[3] <= 1'bz;
    MDOUT_I[4] <= 1'bz; MDOUT_Q[4] <= 1'bz;
    MDOUT_I[5] <= 1'bz; MDOUT_Q[5] <= 1'bz;
end

//------------------------------------------------------------------------------
//                 All DSP code is in the Receiver module
//------------------------------------------------------------------------------

receiver receiver_inst0(
	//control
	.clock(C122_clk),
	.rate(sampling_rate[0]), 
        .frequency(C122_sync_phase_word[1]),
	.out_strobe(strobe[0]),
	//input
	.in_data(temp_ADC),		
	//output
	.out_data_I(rx_I[0]),
	.out_data_Q(rx_Q[0])
	);

receiver receiver_inst1(
	//control
	.clock(C122_clk),
	.rate(C122_PTT_out?sampling_rate[0]:sampling_rate[1]), 
        .frequency((sync0 && C122_PTT_out)?C122_sync_phase_word[0]:C122_sync_phase_word[2]),
	.out_strobe(strobe[1]),
	//input
	.in_data((sync0 && C122_PTT_out)?temp_DACD:temp_ADC),
	//output
	.out_data_I(rx_I[1]),
	.out_data_Q(rx_Q[1])
	);


///////////////////////////////////////////////////////////
//
//    Spectrum Data over NWire to Ozy
//
///////////////////////////////////////////////////////////
wire [15:0] spd_data;
wire        spd_req, spd_rdy, spd_ack;
wire        spf_wreq, spf_rreq, spf_full, spf_empty;
wire        trigger;
wire        spectrum_out;

assign trigger = C21;
assign A12 = (Merc_ID[0] == adc2[0]) ? spectrum_out : 1'bz; //Merc with ADC selected should send wideband spectrum

NWire_xmit #(.SEND_FREQ(768000), .OSL(16), .OSW(1), .ICLK_FREQ(122880000), .XCLK_FREQ(122880000))
        SPD (.irst(C122_rst), .iclk(C122_clk), .xrst(C122_rst), .xclk(C122_clk),
             .xdata(spd_data), .xreq(spd_req), .xrdy(spd_rdy), .xack(spd_ack), .dout(spectrum_out));

SP_fifo SPF (.sclr(C122_rst), .clock (C122_clk), .full(spf_full), .empty(spf_empty), 
             .wrreq (spf_wreq), .data (temp_ADC), .rdreq (spf_rreq), .q(spd_data) );

sp_xmit_ctrl
        SPC (.rst(C122_rst), .clk(C122_clk), .trigger(trigger), .fifo_full(spf_full),
             .fifo_empty(spf_empty), .fifo_wreq(spf_wreq), .fifo_rreq(spf_rreq),
             .xfer_req(spd_req), .xfer_rdy(spd_rdy), .xfer_ack(spd_ack) );

///////////////////////////////////////////////////////////
//
//    Command and Control Decoder 
//
///////////////////////////////////////////////////////////
/*

	The C&C encoder in Ozy broadcasts data over the Atlas bus (C20) for
	use by other cards e.g. Mercury and Penelope.
	
	The data format is as follows:
	
        <[93:92]sampling_rate><[91]PTT><[90:88]address><[87:56]frequency><[55:53]clock_source><[52:46]OC>
        <[45:14]SPI_Alex_data><[13]DITHER><[12]RAND><[11:4]Drive_Level><[3:1]RxN_preamps><[0]common_merc_freq> 
	
	for a total of 94 bits. Frequency is really Phase and 32 bit binary format and 
	OC is the open collector data on Penelope.  PGA, DITHER and RAND are ADC settings
	
	The clock source (clock_select) decodes as follows:
	
	x00  = 10MHz reference from Atlas bus ie Gibraltar
	x01  = 10MHz reference from Penelope
	x10  = 10MHz reference from Mercury
	0xx  = 122.88MHz source from Penelope 
	1xx  = 122.88MHz source from Mercury 
	
*/
wire   [96:0] C122_rcv_data;
reg    [31:0] C122_Alex_data;
wire          C122_rcv_rdy;
reg           C122_PTT_out;
reg     [2:0] clock_select;   	// 10MHz and 122.88MHz clock selection
reg           C122_new_data;
reg     [1:0] adc2;
wire    [2:0] fn;

assign fn = C122_rcv_data[91:89]; // which Mercury frequency 1 - 2, penny is 0

generate
  genvar j;
  for (j = 0; j <= NR; j = j + 1) // capture TX & NR RX frequencies upto 2 Mercs w/ 2RX each
  begin: Fsave
    always @ (posedge C122_clk)
    begin
      if (C122_rst) begin
        C122_sync_phase_word[j] <= 32'b0;
      end
      else if (C122_rcv_rdy) begin
        if (fn == j) begin
          C122_sync_phase_word[j] <= C122_rcv_data[88:57];
          case (C122_rcv_data[94:93])
            2'b00:  sampling_rate[j] <= 6'd40;              //  48ksps 
            2'b01:  sampling_rate[j] <= 6'd20;              //  96ksps
            2'b10:  sampling_rate[j] <= 6'd10;              //  192ksps
            2'b11:  sampling_rate[j] <= 6'd5;               //  384ksps
            default: sampling_rate[j] <= 6'd40;
          endcase
        end
      end

    end
  end
endgenerate

reg [3:0] RxN_preamps;    // status of Mercury preamps (0000=all preamps OFF, 0001=Merc1 preamp ON, 0010=Merc2 preamp ON, etc)
reg C122_Alex_enable;
reg merc1_2_pres;
reg sync0;
reg [6:0] OC;

always @ (posedge C122_clk)
begin
  if (C122_rst)
  begin
    C122_new_data     <= 1'b0;
    C122_Alex_enable  <= 1'b0;
    C122_Alex_data    <= 32'd0;
    C122_PTT_out      <= 1'b0;   // PTT from PC
    clock_select      <= 3'b000;     
    adc2              <= 2'b00;     
    DITHER            <= 1'b0;   // 1 = dither on
    RAND              <= 1'b0;   // 1 = randomizer on 
    merc1_2_pres      <= 1'b0;
    sync0             <= 1'b0;
    OC                <= 7'd0;
  end
  else if (C122_rcv_rdy)
  begin
    C122_new_data    <= 1'b1;            // only 1 C122_clk wide
    C122_Alex_enable <= C122_rcv_data[4];
    C122_Alex_data   <= C122_rcv_data[46:15];
    C122_PTT_out     <= C122_rcv_data[92];    // PTT from PC via USB 
    RxN_preamps      <= C122_rcv_data[3:2];   // decode preamp settings for all Mercury boards
    adc2             <= C122_rcv_data[1:0];
    OC               <= C122_rcv_data[53:47];
    merc1_2_pres     <= C122_rcv_data[95];
    sync0            <= C122_rcv_data[96]; // sync DDC1 -> DDC0 for PS
	 
    if (C122_rcv_data[91:89] == 3'b0)   //all boards update with Merc1
    begin
      clock_select      <= C122_rcv_data[56:54];     
      DITHER            <= C122_rcv_data[14];     // 1 = dither on
      RAND              <= C122_rcv_data[13];     // 1 = randomizer on 
    end
	 
    // set on-board input-attenuator (the so-called "pre-amp")
    Rx_preamp = RxN_preamps[Merc_ID];
  end
  else
    C122_new_data <= 1'b0;
end

/*
        <[96]sync0><[95]merc1_2_pres><[94:93]sampling_rate><[92]PTT><[91:89]address><[88:57]frequency><[56:54]clock_source>
        <[53:47]OC><[46:15]Alex_data><[14]DITHER><[13]RAND><[12:5]Drive_Level><[4]Alex_enable><[3:2]RxN_preamps><[1:0]adc2>

*/

NWire_rcv  #(.OSL(97), .OSW(1), .ICLK_FREQ(122880000), .XCLK_FREQ(122880000), .SLOWEST_FREQ(10000)) 
      CCrcv (.irst(C122_rst), .iclk(C122_clk), .xrst(C122_rst), .xclk(C122_clk),
             .xrcv_data(C122_rcv_data), .xrcv_rdy(C122_rcv_rdy), .xrcv_ack(C122_rcv_rdy), .din(CC));

//reg  [31:0] SPI_Alex_data;

//cdc_mcp #(32) cdc_sync_Alex
//   (.a_rst(C122_rst), .a_clk(C122_clk), .a_data(C122_Alex_data), .a_data_rdy(C122_rcv_rdy), .b_rst(C122_rst), .b_clk(SPI_clk), .b_data(SPI_Alex_data));

SPI Alex_SPI_Tx (.enable(C122_Alex_enable), .Alex_data(C122_Alex_data), .SPI_data(SPI_data),
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
	The clock division is made using PLLs to provide the highest performance.
*/

wire ref_80khz; 
wire osc_80khz; 
wire exc_80khz;
wire ref_clock;
wire C10_locked;
wire C122_locked;

assign ref_ext = (Merc_ID > 3'd0) ? 1'b0 : clock_select[1]; // if set & merc0 use internally and send to C16 else get from C16
//assign ref_ext = 1'b0;
//assign source_122MHZ = (clock_select[3:2] == 2'b01); // if set use internally and send to LVDS else get from LVDS
assign source_122MHZ = (Merc_ID > 3'd0) ? 1'b0 : (clock_select[2] == 1'b1); // if set use internally and send to LVDS else get from LVDS

// Select 122.88MHz source. If source_122MHZ == 0 then use Penelope's 122.88MHz clock and send to LVDS
// Otherwise get external clock from LVDS

assign LVDS_RXE_N = source_122MHZ ? 1'b1 : 1'b0;  // enable LVDS receiver if clock is external
assign LVDS_TXE   = source_122MHZ ? 1'b1 : 1'b0;  // enable LVDS transmitter if Mercury is the source 
//assign LVDS_TXE   = !LVDS_RXE_N;  // chip is half duplex; enable LVDS transmitter if Mercury is the source 

// select 10MHz reference source. If ref_ext is set use Mercury's 10MHz ref and send to Atlas C16
wire pll192;
wire CP122_clk;
wire reference;
wire ref_ext;			// Set when internal 10MHz reference sent to Atlas C16

assign reference = ref_ext ? OSC_10MHZ : ext_10MHZ; 
assign ext_10MHZ = ref_ext ? OSC_10MHZ : 1'bz; 		// C16 is bidirectional so set high Z if input. 

// div 10 MHz ref clock on Atlas C16  by 125 to get 80 khz 
oddClockDivider refClockDivider(reference, ref_80khz); 

// Use a PLL to divide 10MHz clock from AUX_CLK Excalibur) to 80kHz
C10_PLL PLL2_inst (.inclk0(AUX_CLK), .c0(exc_80khz), .locked(C10_locked));

// Use a PLL to divide 122.88MHz clock to 80kHz
C122_PLL PLL_inst (.inclk0(C122_clk), .c0(osc_80khz), .c1(pll192), .c2(CP122_clk), .locked(C122_locked));
//C122_PLL PLL_inst (.inclk0(C122_clk), .c0(osc_80khz), .c1(pll192), .c2(CP122_clk), .locked(C122_locked), .phasecounterselect(3'b100), .phaseupdown(OC[1]), .scanclk(C122_clk), .phasestep(OC[0]));

// If C10_PLL is locked then use its output, else use C16
assign ref_clock = C10_locked ? exc_80khz : ref_80khz;


// NOTE: If external reference is not available then phase detector 
// will be fed with 80kHz from 122.88MHz clock. Loop filter will 
// set VCXO control volts to 3.3v/2

// Apply to EXOR phase detector 
assign FPGA_PLL = ref_clock ^ osc_80khz; 


//------------------------------------------------------------------------------
//                          LEDs, active high
//------------------------------------------------------------------------------

// LEDs for testing 0 = off, 1 = on
assign DEBUG_LED0 = OVERFLOW; 		// LED 0 on when ADC Overflow

`ifdef DONT
// Test pins
assign TEST0 = osc_80khz; // 80kHz from 122.88MHz
assign TEST1 = ref_clock; // 80kHz from 10MHz
assign TEST2 = FPGA_PLL;  // phase detector output
assign TEST3 = 1'b0; 
`endif

assign DEBUG_LED3 = OC[0];
assign DEBUG_LED4 = OC[1];

//------------------------------------------------------------------------------
//                          blink!
//------------------------------------------------------------------------------
reg [26:0]counter;
always @(posedge C122_clk) counter = counter + 1'b1;
assign {DEBUG_LED2,DEBUG_LED1} = counter[24:23];  // even faster flash for this version!

endmodule 

