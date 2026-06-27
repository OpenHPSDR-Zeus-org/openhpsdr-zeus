/***********************************************************
*
*	Mercury DDC receiver 
*
************************************************************/
// 
// Copyright 2009, 2011 Phil Harman VK6APH 
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
	  1 Nov 2009 - Replaced Kordic with Cordic, released as V2.8a	
	 21 Nov 2009 - Added PLL dividers for 122.88MHz to 80kHz and 10MHz AUX_CLK (J8)	
	             - Added PLL lock LEDs for both PLLs
	             - release as V2.9
	 24 Dec 2009 - Fixed bug in SPI clock generation - release as V2.9a 
	 10 Sep 2011 - Removed 9kHz IF offset when selecting Alex filters.
	               Changed Alex frequency based relay switching frequencies.
				   Added support for PC to select Alex relays.
				   Added PennyLane/Hermes Drive level to C&C rather than additional nWire - release as V3.0.	
	  2 Nov 2011 - Changes by Joe, K5SO:
							 
						1)  Changed the number of receivers implemented from one to four.
						
						2) Replaced the NWire_xmit and NWire_rcv common modules to versions  
						that permit 2D dimensioning of data being passed (i.e.,  replaced the  
						the versions with .DATA_BITS() to the versions with .OSL(), .OSW()  
						references instead).
						
						3) Changed the single "receiver" instantiation to multiple  
						"memreceiver" instantiations as the "receiver" instantiation is too  
						large to fit four of them into the FPGA whereas four "memreceiver"  
						instantiations fit.  (Mercury FPGA is now 98% full)
						
						4)  Extended the length of the Atlas C&C broadcast received from 84 bits  
						to 87 bits to accommodate reception of the three multipleRx_mode bits  
						being sent from Metis/Ozy to Mercury via the Altas bus broadcast from  
						Metis/Ozy.
						
						5)  Added Alex filter switching logic to bypass Alex Tx and Rx filters  
						on receive if automatic filter selection and "multiple-receivers-on-a- 
						single-Mercury" mode are being used simultaneously.
						
						6)  Added "power management" code to disable unused receivers in the  
						FPGA in order to minimize the current drawn from the 5V rail.
						 
						Released as MERCURY_v3.1.  Built with Quartus V11.0 sp1.
						
	 21 Apr 2012 - Changes by Joe, K5SO:
							 
						1)  Implemented new USB Protocol C&C bit C4[7] when C0 = 0, "common_Merc_freq" to signal that all
						Mercury boards should use the same frequency.
						
						2) Implemented new USB Protocol C&C bits C1[3:1] when C0 = 0001_010x, "RxN_preamps" to indicate which
						preamps are to be active when multiple Mercury boards are in use (C1[3:1] = 0000 -> no preamps ON, C1[3:1] = 0001
						-> Mercury 1 preamp ON, C1[3:1] = 0011 -> Mercury 1 preamp and Mercury 2 preamp both ON, etc).
						
						3) reduced the size of clock_select from 4'b0000 to 3'b000 to be consistent with C&C bits C1[4:2] when C0 = 0.
						
						4) increased the Metis/Ozy-to-Atlas broadcast size from 87 bits to 91 bits to include the "RxN_preamps" and 
						"common_Merc_freq" variables for Mercury in the broadcast receiver section.  
						
						5) added logic selection code to allow only one Mercury board (Merc_ID = 3'b000) to send wideband spectrum
						data to Atlas bus line A12 when multiple Mercury boards are present on the Atlas bus). 
						
						Released as MERCURY_V3.2.  Bulit with Quartus V11.1 sp1.
						
	 3 Oct 2012 - Changes by Joe K5SO:
						- Reduced the number of receivers implemented to two
						- Added dual Rx, independent, manual Alex filter controls
						- Added Alex HPF/LPF switching logic to select LPF appropriate for highest Rx freq in use and
						  HPF appropriate for lowest Rx freq in use when operating with multiple receivers on a single Mercury board
						- Added additional Alex LPF filter switching logic during transmit to accommodate SPLT mode operation correctly
						- Modified Alex Tx RED LED operation to illuminate when transmitting
						- Modified HPF/LPF selection for automatic independent operation of multiple Alex sets when using multiple Mercury boards 
						- Renamed version number to V3.3.  Built using Quartus II V11.1 sp2.
						
	 7 Oct 2012    - Increased Wideband FIFO to 16k and wideband nWire speed to 768k.
	 
*/
	
module Mercury(OSC_10MHZ, ext_10MHZ,AUX_CLK, C122_clk,INA,CC,ATTRLY,/*A2,A3,*/A4,A6,A12,C21,C23,C24,MDOUT,CDIN,
			   TLV320_BCLK,TLV320_LRCIN,TLV320_LRCOUT,TLV320_MCLK,CMODE,MOSI,SCLK,nCS,SPI_data,SPI_clock,
			   Tx_load_strobe,Rx_load_strobe,FPGA_PLL,LVDS_TXE,LVDS_RXE_N,OVERFLOW,DITHER,SHDN,PGA,RAND,INIT_DONE,
			   TEST0,TEST1,TEST2,TEST3,DEBUG_LED0,DEBUG_LED1,DEBUG_LED2,DEBUG_LED3,DEBUG_LED4,DEBUG_LED5,
			   DEBUG_LED6,DEBUG_LED7, Merc_ID,Merc_ID_drv, MULTIPLE_MERC); 

input  wire        OSC_10MHZ;      // 10MHz TCXO input 
inout  tri         ext_10MHZ;      // 10MHz reference to/from Atlas pin C16
input  wire 	   AUX_CLK;		   // 10MHz reference from Excalibur 
input  wire        C122_clk;       // 122.88MHz clock from LT2208
input  wire [15:0] INA;            // samples from LT2208
input  wire        CC;             // Command & Control from Atlas C20
output wire        ATTRLY;         // Antenna relay control
//output wire        A2;			   // Mercury4 firmware version number and ADC overload status
//output wire        A3;			   // Mercury3 firmware version number and ADC overload status
output wire        A4;			   // Mercury2 firmware version number and ADC overload status
output wire        A6;             // Mercury serial number/ADC overload
output wire        A12;            // Mercury spectrum data
input  wire        C21;            // trigger signal from Ozy to get spectrum data
output wire        C23;            // M_LR_sync - see LRAudio NWire_xmit interface in Ozy
input  wire        C24;            // M_LR_data (Rx audio) from Atlas bus
output wire  [3:0] MDOUT;          // I/Q out to Atlas bus on A7, A8, A9, A10  
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

parameter C122_TPD = 2.1;

localparam SERIAL = 8'd33;  // software version serial number

reg  [15:0] temp_ADC;
reg         data_ready;     // set at end of decimation
reg         Rx_preamp;      // 1 = preamp ON (no on-board attenuator), 0 = preamp OFF (20 dB attenuator inserted) 
wire [3:0]  Mercury_Addr;

assign Mercury_Addr = Merc_ID + 1'b1;  // Merc 1 - 8 (not Merc 0 - 7)
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

always @(posedge C122_cbclk)
begin
  if (C122_rst)
    spc <= 2'b00;
  else  spc <= spc + 2'b01;
end

assign SPI_clk = spc[1];

// Select 122.88MHz source. If source_122MHZ == 0 then use Penelope's 122.88MHz clock and send to LVDS
// Otherwise get external clock from LVDS

assign LVDS_RXE_N = source_122MHZ ? 1'b1 : 1'b0;  // enable LVDS receiver if clock is external
assign LVDS_TXE   = source_122MHZ ? 1'b1 : 1'b0;  // enable LVDS transmitter if Mercury is the source 
//assign LVDS_TXE   = !LVDS_RXE_N;  // chip is half duplex; enable LVDS transmitter if Mercury is the source 

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

NWire_rcv #(.OSL(32), .OSW(1), .ICLK_FREQ(122880000), .XCLK_FREQ(122880000), .SLOWEST_FREQ(10000))
        LRAudio (.irst(C122_rst), .iclk(C122_clk), .xrst(C122_rst), .xclk(C122_clk),
                 .xrcv_rdy(C122_LR_rdy), .xrcv_ack(C122_LR_rdy), .xrcv_data(C122_LR_data),
                 .din(C24));

//TestModeChecker tmc (C122_rst, C122_clk, C122_LR_data, C122_LR_rdy);
                
//assign C23 = CLRCLK; // M_LR_sync -> so Ozy knows when to send M_LR_data
assign C23 = (Merc_ID == 3'b000) ? CLRCLK : 1'bz; // M_LR_sync -> so Ozy knows when to send M_LR_data

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

localparam NR = 2; // number of receivers to implement

reg       [31:0] C122_frequency_HZ [0:NR-1];   // frequency control bits for CORDIC
reg       [31:0] C122_frequency_HZ_Tx;
reg       [31:0] C122_last_freq [0:NR-1];
reg       [31:0] C122_sync_phase_word [0:NR-1];
wire      [63:0] C122_ratio [0:NR-1];
wire      [23:0] rx_I [0:NR-1];
wire      [23:0] rx_Q [0:NR-1];
wire             strobe [0:NR-1];
wire    [NR-1:0] MDO;
wire [NR*48-1:0] MDIN;

localparam M2 = 32'd1172812403;  // B57 = 2^57.   M2 = B57/122880000

//------------------------------------------------------------------------------
//                 All DSP code is in the Receiver module
//------------------------------------------------------------------------------

// Mercury power management; once a receiver is enabled it cannot be turned off without a C122_rst reset
wire   C122_clk_ONOFF[0:NR-1];						//clk enable switch
wire   latch[0:NR-1];
reg    gate[0:NR-1];

assign latch[0] = 1'b1; //always enable receiver 0
assign latch[1] = (multiRx_mode > 3'b000) ? 1'b1 : 1'b0; 	//enable second receiver
//assign latch[2] = (multiRx_mode > 3'b001) ? 1'b1 : 1'b0; 	//enable third receiver
//assign latch[3] = (!MULTIPLE_MERC & multiRx_mode > 3'b010) ? 1'b1 : 1'b0; 	//enable fourth receiver

always @ (posedge C122_clk)
begin
    if (C122_rst)
		begin
		gate[0] = 1'b1;  // initially make receiver 0 active
		gate[1] = 1'b0;  // initially make receiver 1 inactive
//		gate[2] = 1'b0;  // initially make receiver 2 inactive
//		gate[3] = 1'b0;  // initially make receiver 3 inactive
		end		
	else
		begin
		if (latch[0])
			gate[0] = 1'b1;	 // latch receiver 0 to "enabled"
		if (latch[1])
			gate[1] = 1'b1;	 // latch receiver 1 to "enabled"
//		if (latch[2])
//			gate[2] = 1'b1;	 // latch receiver 2 to "enabled"
//		if(latch[3])
//			gate[3] = 1'b1;    // latch receiver 3 to "enabled"
	 end 
end
  
assign C122_clk_ONOFF[0] = (gate[0]) ? C122_clk : 1'b0; 	//enable first receiver	  
assign C122_clk_ONOFF[1] = (gate[1]) ? C122_clk : 1'b0; 	//enable second receiver	  
//assign C122_clk_ONOFF[2] = (gate[2]) ? C122_clk : 1'b0; 	//enable third receiver	  
//assign C122_clk_ONOFF[3] = (gate[3]) ? C122_clk : 1'b0; 	//enable fourth receiver	  

// end of Mercury power management section

generate
  genvar c;
  for (c = 0; c < NR; c = c + 1) // 2 channels max; NR Mercury Data Channels/Receivers 
   begin: MDC 
    assign C122_ratio[c] = C122_frequency_HZ[c] * M2; // B0 * B57 number = B57 number

    always @ (posedge C122_clk)
    begin
      if (C122_cbrise) // time between C122_cbrise is enough for ratio calculation to settle
      begin
        C122_last_freq[c] <= C122_frequency_HZ[c];
        if (C122_last_freq[c] != C122_frequency_HZ[c]) // frequency changed
          C122_sync_phase_word[c] <= C122_ratio[c][56:25]; // B57 -> B32 number since R is always >= 0  
      end
    end

    //------------------------------------------------------------------------------
    //                 Receiver module
    //------------------------------------------------------------------------------

    memreceiver #(.INITIAL_STAGES(6))
      Merc_rcv (
 	  //control
      .clock(C122_clk_ONOFF[c]),
      .rate({C122_DFS1, C122_DFS0}), //00=48, 01=96, 10=192 kHz
      .frequency(C122_sync_phase_word[c]),
      .out_strobe(strobe[c]),
      //input
      .in_data(temp_ADC),
      //output
      .out_data_I(rx_I[c]),
      .out_data_Q(rx_Q[c])
      );
      
    // MDO[c] is driven onto the appropriate Atlas line according to Merc_ID for multiple-Mercury-board mode or on 
    // four Atlas lines for multi-RX-on-a-single-Merc mode
    assign MDOUT[c] = MULTIPLE_MERC ? ((Merc_ID == c) ? (common_Merc_freq ? MDO[0] : MDO[c]) : 1'bz) : MDO[c]; //1 channel or 2 channels from this Mercury
    assign MDIN[c*48 +: 48] = {rx_I[c],rx_Q[c]};
  end
endgenerate
   
// send I and Q data to MDO then to MDOUT via the assigns above
NWire_xmit #(.SEND_FREQ(192000), .OSL(48), .OSW(NR), .ICLK_FREQ(122880000),
             .XCLK_FREQ(122880000), .LOW_TIME(1'b0))
       M_IQ (.irst(C122_rst), .iclk(C122_clk), .xrst(C122_rst), .xclk(C122_clk),
             .xdata(MDIN), .xreq(strobe[0]), .xrdy(), .xack(), .dout(MDO));

///////////////////////////////////////////////////////////
//
//    Spectrum Data over NWire to Ozy
//
///////////////////////////////////////////////////////////
wire [15:0] spd_data;
wire        spd_req, spd_rdy, spd_ack;
wire        spf_wreq, spf_rreq, spf_full, spf_empty;
wire        trigger;
wire         spectrum_out;

assign trigger = C21;
assign A12 = (Merc_ID == 3'b000) ? spectrum_out : 1'bz; //only 1 Merc should send wideband spectrum

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
//    Serial Number & ADC Overload 
//
///////////////////////////////////////////////////////////

// Sends current software serial # as an 8 bit value.

wire [8:0] xmit_data;
wire [8:0] xmit_data2;
//wire [8:0] xmit_data3;
//wire [8:0] xmit_data4;

wire       ser_out;
wire	   	ser_out2;
//wire       ser_out3;
//wire       ser_out4;

assign xmit_data = {SERIAL,OVERFLOW}; // serial number, ADC overload (overflow)

NWire_xmit  #(.OSL(9), .OSW(1), .ICLK_FREQ(122880000), .XCLK_FREQ(122880000), .SEND_FREQ(1000)) 
      ser_no (.irst(C122_rst), .iclk(C122_clk), .xrst(C122_rst), .xclk(C122_clk),
              .xdata(xmit_data), .xreq(1'b1), .xrdy(), .xack(), .dout(ser_out));
              
assign A6 = (Merc_ID == 3'b000) ? ser_out : 1'bz;

assign xmit_data2 = {SERIAL,OVERFLOW}; // serial number, ADC overload (overflow)

NWire_xmit  #(.OSL(9), .OSW(1), .ICLK_FREQ(122880000), .XCLK_FREQ(122880000), .SEND_FREQ(1000)) 
      ser_no2 (.irst(C122_rst), .iclk(C122_clk), .xrst(C122_rst), .xclk(C122_clk),
              .xdata(xmit_data2), .xreq(1'b1), .xrdy(), .xack(), .dout(ser_out2));
              
assign A4 = (Merc_ID == 3'b001) ? ser_out2 : 1'bz;

//assign xmit_data3 = {SERIAL,OVERFLOW}; // serial number, ADC overload (overflow)

//NWire_xmit  #(.OSL(9), .OSW(1), .ICLK_FREQ(122880000), .XCLK_FREQ(122880000), .SEND_FREQ(1000)) 
//      ser_no3 (.irst(C122_rst), .iclk(C122_clk), .xrst(C122_rst), .xclk(C122_clk),
//              .xdata(xmit_data3), .xreq(1'b1), .xrdy(), .xack(), .dout(ser_out3));
              
//assign A3 = (Merc_ID == 3'b010) ? ser_out3 : 1'bz;

//assign xmit_data4 = {SERIAL,OVERFLOW}; // serial number, ADC overload (overflow)

//NWire_xmit  #(.OSL(9), .OSW(1), .ICLK_FREQ(122880000), .XCLK_FREQ(122880000), .SEND_FREQ(1000)) 
//      ser_no4 (.irst(C122_rst), .iclk(C122_clk), .xrst(C122_rst), .xclk(C122_clk),
//              .xdata(xmit_data4), .xreq(1'b1), .xrdy(), .xack(), .dout(ser_out4));
              
//assign A2 = (Merc_ID == 3'b011) ? ser_out4 : 1'bz;

              
///////////////////////////////////////////////////////////
//
//    Command and Control Decoder 
//
///////////////////////////////////////////////////////////
/*

	The C&C encoder in Ozy broadcasts data over the Atlas bus (C20) for
	use by other cards e.g. Mercury and Penelope.
	
	The data format is as follows:
	
	<[90:89]DFS1,DFS0><[88]PTT><[87:84]address><[83:52]frequency><[51:49]clock_source><[48:42]OC>
	<[41]Mode><[40]PREAMP><[39]DITHER><[38]RAND><[37:36]ATTEN><[35:34]TX_relay><[33]Rout><[32:31]RX_relay>
	<[30:16]Alex><[15:8]Drive_Level><[7:5]multiRx_mode><[4:1]RxN_preamps><[0]common_Merc_freq> 
	
	for a total of 91 bits. Frequency is in Hz and 32 bit binary format and 
	OC is the open collector data on Penelope. Mode is for a future Class E PA,
	PGA, DITHER and RAND are ADC settings and ATTEN the attenuator on Alex
	
	The clock source (clock_select) decodes as follows:
	
	x00  = 10MHz reference from Atlas bus ie Gibraltar
	x01  = 10MHz reference from Penelope
	x10  = 10MHz reference from Mercury
	0xx  = 122.88MHz source from Penelope 
	1xx  = 122.88MHz source from Mercury 
	
*/
wire   [90:0] C122_rcv_data;
wire          C122_rcv_rdy;

reg           C122_PTT_out;
reg     [2:0] clock_select;   	// 10MHz and 122.88MHz clock selection
//reg     [6:0] OC;             // Only Penelope uses this
reg     [1:0] C122_ATTEN;     	// attenuator setting on Alex
reg     [1:0] C122_TX_relay;  	// Tx relay setting on Alex
reg           C122_Rout;      	// Rx1 out on Alex
reg     [1:0] C122_RX_relay;  	// Rx relay setting on Alex
reg           C122_new_data;
reg    [14:0] C122_Alex;	  	// HPF & LPF manual relay settings on Alex
							  // if bit 14 is set then use remainder to control relays
							  // else select relays by frequency
							  
reg		      Alex_manual; 	  // set if manual selection of Alex relays active
reg           Alex_6m_preamp; // set if manual selection and 6m preamp selected
reg     [6:0] Alex_manual_LPF;// Alex LPF relay selection in manual mode
reg     [5:0] Alex_manual_HPF;// Alex HPF relay selection in manual mode
reg     [2:0] multiRx_mode;   //000 = single receiver, 001 = two receivers, 002 = three receivers, 003 = four receivers 							  
reg     [3:0] RxN_preamps;    // status of Mercury preamps (0000=all preamps OFF, 0001=Merc1 preamp ON, 0010=Merc2 preamp ON, etc)
reg           PREAMP;         // "pre-amp" selection for on-board input-attenuator on Mercury (0="preamp" off, 1="preamp" ON)
reg           common_Merc_freq;
wire           common_select;
wire    [3:0] fn;

assign fn = C122_rcv_data[87:84]; // which Mercury frequency 1 - 4 

assign common_select = ((common_Merc_freq & (fn == 4'b1)) | (!common_Merc_freq & (MULTIPLE_MERC & (fn == Mercury_Addr)))) ? 1'b1 : 1'b0;

generate
  genvar j;
  for (j = 0; j < NR; j = j + 1) // capture NR frequencies from Metis/Ozy/Magister
  begin: Fsave
    always @ (posedge C122_clk)
    begin
      if (C122_rst)
        C122_frequency_HZ[j] <= 32'b0;
//      else if (C122_rcv_rdy && (fn == (j+1)))
      else
		if (C122_rcv_rdy && (common_select  | (!MULTIPLE_MERC & (fn == j+1)))) //...if common-Merc_freq set, all boards update with Merc1      
		  C122_frequency_HZ[j] <= C122_rcv_data[83:52];
    end
  end
endgenerate


always @ (posedge C122_clk)
begin
  if (C122_rst)
  begin
    C122_new_data     <= 1'b0;
    C122_DFS1         <= 1'b0;   // I/Q sampling rate selection
    C122_DFS0         <= 1'b0;
    C122_PTT_out      <= 1'b0;   // PTT from PC via USB 
    clock_select      <= 3'b000;     
    DITHER            <= 1'b0;   // 1 = dither on
    RAND              <= 1'b0;   // 1 = randomizer on 
    C122_ATTEN        <= 2'b00;  // Attenuator setting on Alex
    C122_TX_relay     <= 2'b00;  // Tx relay selection on Alex
    C122_Rout         <= 1'b0;   // Rx_1_out on Alex
    C122_RX_relay     <= 2'b00;  // Rx relay selection on Alex
    Alex_manual 	  <= 1'b0;   // set if manual selection of Alex relays active
    Alex_6m_preamp    <= 1'b0;	 // set if manual selection and 6m preamp selected
    Alex_manual_LPF   <= 7'b0;	 // Alex LPF selection in manual mode
    Alex_manual_HPF   <= 6'b0;   // Alex HPF selection in manual mode

  end
  else if (C122_rcv_rdy)
  begin
	 if (fn == 0) C122_frequency_HZ_Tx <= C122_rcv_data[83:52];
    C122_new_data <= 1'b1;            // only 1 C122_clk wide
    C122_DFS1     <= C122_rcv_data[90];
    C122_DFS0     <= C122_rcv_data[89];    // I/Q sampling rate selection
    C122_PTT_out  <= C122_rcv_data[88];    // PTT from PC via USB 
    multiRx_mode  <= C122_rcv_data[7:5];   // 0=single receiver, 1=two receivers, 2=three receivers, 3=four receivers
    RxN_preamps   <= C122_rcv_data[4:1];	 // decode preamp settings for all Mercury boards
										// Note: presently there is a redundancy for Mercury #1 "pre-amp" selection because IF_PREAMP below also 
										// controls the Mercury #1 pre-amp state; this is to maintain compatability with earlier PC programs that select 
										// "preamp" via C&C byte C3[2] when C0[7:1] is zero.
	 common_Merc_freq <= C122_rcv_data[0];
	 
    if (C122_rcv_data[87:84] == 4'b0)   //all boards update with Merc1
    begin
      clock_select      <= C122_rcv_data[51:49];     
      PREAMP            <= C122_rcv_data[40];     // PREAMP = 0 => 20 dB attenuator is inserted; PREAMP = 1 => no attenuator (so-called "preamp ON") 
      DITHER            <= C122_rcv_data[39];     // 1 = dither on
      RAND              <= C122_rcv_data[38];     // 1 = randomizer on 
      C122_ATTEN        <= C122_rcv_data[37:36];  // Attenuator setting on Alex
      C122_TX_relay     <= C122_rcv_data[35:34];  // Tx relay selection on Alex
      C122_Rout         <= C122_rcv_data[33];     // Rx_1_out on Alex
      C122_RX_relay     <= C122_rcv_data[32:31];  // Rx relay selection on Alex
      Alex_manual 	   <= C122_rcv_data[30];	  // set if manual selection of Alex relays active
      if (Merc_ID == 0) 
		begin
			Alex_manual_LPF   <= C122_rcv_data[29:23];  // Alex LPF selection in manual mode
			Alex_6m_preamp    <= C122_rcv_data[22];	  // set if manual selection and 6m preamp selected
			Alex_manual_HPF   <= C122_rcv_data[21:16];  // Alex HPF selection in manual mode
		end
    end
	 
    if (C122_rcv_data[87:84] == 4'b1) 
	 begin
	 if (Merc_ID == 1) 
		begin 
			Alex_manual_LPF   <= C122_rcv_data[29:23];  // Alex 2 LPF selection in manual mode
			Alex_6m_preamp    <= C122_rcv_data[22];	  // set if manual selection and 6m preamp on Alex 2 selected
			Alex_manual_HPF   <= C122_rcv_data[21:16];  // Alex 2 HPF selection in manual mode
		end
	 end
	 
  // set on-board input-attenuator (the so-called "pre-amp")
	if (Merc_ID == 0)
	begin
	 if (PREAMP | RxN_preamps[0]) Rx_preamp = 1'b1;
	 else Rx_preamp = 1'b0; 
   end	
	else 
		Rx_preamp = RxN_preamps[Merc_ID];
	end
  else
    C122_new_data <= 1'b0;
end

NWire_rcv  #(.OSL(91), .OSW(1), .ICLK_FREQ(122880000), .XCLK_FREQ(122880000), .SLOWEST_FREQ(10000)) 
      CCrcv (.irst(C122_rst), .iclk(C122_clk), .xrst(C122_rst), .xclk(C122_clk),
             .xrcv_data(C122_rcv_data), .xrcv_rdy(C122_rcv_rdy), .xrcv_ack(C122_rcv_rdy), .din(CC));


assign ref_ext = clock_select[1];       // if set use internally and send to C16 else get from C16
//assign source_122MHZ = (clock_select[3:2] == 2'b01); // if set use internally and send to LVDS else get from LVDS
assign source_122MHZ = (Merc_ID > 0) ? 1'b0 : (clock_select[2] == 1'b1); // if set use internally and send to LVDS else get from LVDS


//////////////////////////////////////////////////////////////
//
//		Alex Filter selection
//
//////////////////////////////////////////////////////////////

wire  [6:0] C122_LPF;
wire  [6:0] C122_LPF_auto;
wire  [5:0] C122_select_HPF;
wire  [5:0] C122_select_HPF_auto;
wire  [31:0] C122_freq_max;
wire	[31:0] C122_freq_min;
wire  [31:0] C122_HPF_freq;
wire	[31:0] C122_LPF_freq;

assign C122_freq_max = ((C122_frequency_HZ[1] > C122_frequency_HZ[0]) && (C122_frequency_HZ[1] < 32'd60000000)) ? C122_frequency_HZ[1] : C122_frequency_HZ[0];
assign C122_freq_min = ((C122_frequency_HZ[1] < C122_frequency_HZ[0]) && (C122_frequency_HZ[1] > 0)) ? C122_frequency_HZ[1] : C122_frequency_HZ[0];
assign C122_HPF_freq = C122_freq_min;
assign C122_LPF_freq = (C122_PTT_out) ? C122_frequency_HZ_Tx : C122_freq_max;

// if Alex_manual selected then use HPF & LPF setting provided by user
assign C122_LPF 		= Alex_manual ? Alex_manual_LPF : C122_LPF_auto;
assign C122_select_HPF  = Alex_manual ? Alex_manual_HPF : C122_select_HPF_auto;

LPF_select Alex_LPF_select(.clock(C122_clk), .frequency(C122_LPF_freq), .LPF(C122_LPF_auto));
HPF_select Alex_HPF_select(.clock(C122_clk), .frequency(C122_HPF_freq), .HPF(C122_select_HPF_auto));



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

// define and concatenate the Tx data to send to Alex via SPI
assign C122_Tx_red_led = C122_PTT_out; // turn red led on when we Tx
assign C122_TR_relay   = C122_PTT_out; // turn on TR relay when PTT active

assign C122_Alex_Tx_data = {C122_LPF[6:4], C122_Tx_red_led, C122_TR_relay, C122_ANT3, C122_ANT2,
                       C122_ANT1, C122_LPF[3:0], TX_YELLOW_LED, 3'b000};

// define and concatenate the Rx data to send to Alex via SPI
assign C122_Rx_red_led = C122_PTT_out;	// turn red led on when we Rx

// turn 6m preamp on if (frequency > 50MHz AND Alex_manual not selected) else use manual setting
assign C122_6m_preamp = (C122_frequency_HZ[0] > 50000000 && Alex_manual == 1'b0) ? 1'b1 : Alex_6m_preamp;

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
	The clock division is made using PLLs to provide the highest performance.
*/

wire ref_80khz; 
wire osc_80khz; 
wire exc_80khz;
wire ref_clock;
wire C10_locked;
wire C122_locked;

// div 10 MHz ref clock on Atlas C16  by 125 to get 80 khz 
oddClockDivider refClockDivider(reference, ref_80khz); 

// Use a PLL to divide 10MHz clock from AUX_CLK Excalibur) to 80kHz
C10_PLL PLL2_inst (.inclk0(AUX_CLK), .c0(exc_80khz), .locked(C10_locked));

// Use a PLL to divide 122.88MHz clock to 80kHz
C122_PLL PLL_inst (.inclk0(C122_clk), .c0(osc_80khz), .locked(C122_locked));

// If C10_PLL is locked then use its output, else use C16
assign ref_clock = C10_locked ? exc_80khz : ref_80khz;


// NOTE: If external reference is not available then phase detector 
// will be fed with 80kHz from 122.88MHz clock. Loop filter will 
// set VCXO control volts to 3.3v/2

// Apply to EXOR phase detector 
assign FPGA_PLL = ref_clock ^ osc_80khz; 


//------------------------------------------------------------------------------
//                          LEDs
//------------------------------------------------------------------------------

// LEDs for testing 0 = off, 1 = on
assign DEBUG_LED0 = OVERFLOW; 		// LED 0 on when ADC Overflow
assign DEBUG_LED3 = C122_locked; 
assign DEBUG_LED4 = C10_locked;
assign DEBUG_LED5 = 1'b0;
assign DEBUG_LED6 = 1'b0;
assign DEBUG_LED7 = 1'b0;


// Test pins
assign TEST0 = osc_80khz; // 80kHz from 122.88MHz
assign TEST1 = ref_clock; // 80kHz from 10MHz
assign TEST2 = FPGA_PLL;  // phase detector output
assign TEST3 = 1'b0; 

//------------------------------------------------------------------------------
//                          blink!
//------------------------------------------------------------------------------
reg [26:0]counter;
always @(posedge C122_clk) counter = counter + 1'b1;
assign {DEBUG_LED2,DEBUG_LED1} = counter[24:23];  // even faster flash for this version!


endmodule 



