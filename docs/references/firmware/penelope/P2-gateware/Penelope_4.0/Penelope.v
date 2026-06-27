/////////////////////////////////////////////////////////////////
//
// Copyright (C) 2007, 2007, 2008, 2009, 2011 Phil Harman VK6APH
// Copyright (C) 2009 Kirk Weedman KD7IRS
// Copyright (C) 2013 Joe Martin K5SO 
//
//  HPSDR - High Performance Software Defined Radio
//
//
//  Penelope and PennyLane Exciter.
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



/*  
  This program interfaces the AD9744 ADC to PowerSDR over USB.
  The data to the AD9744  is in 14 bit parallel format and 
  is sent at the negative edge of the 122.88MHz clock.
  
  The 48kHz I and Q data from PowerSDR is interpolated by 2560 in a CIC filter to 
  give a data rate of 122.88MHz. The I and Q data in I2S format is taken from the 
  Atlas bus. 
  
  The data is processed by a CORDIC NCO and passed to the AD9744 DAC. 
  
  Command and control data is broadcast  over the Atlas bus (C20) for
  use by other cards e.g. Mercury and Penelope.
  
  Change log:
  
  12 Apr 2009 - Modified to use NWirwe - Kirk Weedman, KD7IRS
  18 May 2009 - Released as V1.2
  6  Jun 2010 - Modifed 122.88MHz clock selection to use external clock if PLL not locked.
					 This overcomes the problem with not being able to select the correct clock
					 if one is not already present.
  7           - Force source_122MHZ = 1'b0 at reset as an alternative to the PLL locked solution.
			     - Change DAC to DAC[13:0] = C122_i_out[16:3];
  4 Jan 2011 - Added support for PennyLane, common code for both boards. Penelope
                will ignore the signal on DAC_ALC.
                Tx_level is set to 128 for testing.
 11 Jan 2011 - Drive_Level sent from Ozy using nWire via Atlas_C18, ignored by Penelope.
 12          - Replaced CORDIC etc with code from Hermes. Changed overall gain to approx unity.
 21          - Corrected overall gain levels. Code now works with Penelope and PennyLane.
					- LED[7] now flashes to show V1.3 loaded, flashes x2 when PTT active.
 12 Feb 2011 - Set I and Q to zero when PTT not active
				 - Can't use Quartus V9.2sp2 since have to leave USB Blaster connected or wont load from flash.
				   This version built using Quartus V10.1sp1.
 29 Jul 2011 - Added FWD and REV power from Alex. Release as V1.4.
  6 Aug 2011 - Changed FWD and REV power conections to AIN1 and AIN2 respectively
 15 Sep 2011 - Removed PennyLane Drive level from Atlas_C18, now obtained from C&C data
 24 Mar 2013 - Removed NWire comm module for Atlas A5, freeing up Atlas A5, removed references to Atlas A5, 
					replaced the i2c module with a new i2c interface module and added a new i2c_slave module
					to pass the firmware version number, Penny output power level, FWD power level (from Alex AIN1),
					and REV power level (from Alex AIN2), mic gain selection, mic/line-in selection, and line-in gain
					selections.  Replaced the Penelope.sdc file with a new constraint file that provides "100% timing met"
					results with the TimeQuest timing wizard analyses. Changed the firmware version to v1.8.

  1 Dec 2023 - Rick / N1GP, Updated C&C encoder message to accomodate Mercs_installed used by Metis, Ozy, and Mercury.
               Ported over Polyphase_FIR for DAC DSP as is used in Hermes TX code.
               Changed the firmware version to v4.0.
 
  
*/


/************************************************************************************************
  IMPORTANT:  In Analysis and Synthesis Settings make sure Power-Up Don't Care is NOT checked.
  
    **************** Built with Quartus V10.1sp1 ********************* 
	 
	 since V9.2sp2 will not program flash correctly
**************************************************************************************************/

//    Atlas bus
//
//    Pin   Signal    Function
//    A11   CDOUT_P   mic data to Ozy
//    C12   J_CDIN    I2S I&Q data from Ozy to Janus
//    C19   P_CDIN    nWire I&Q data from Ozy to Penelope
//    C22   M_LR_clk  from Mercury to Ozy
//    C23   P_IQ_sync  from Penelope to Ozy
//    C20   CC        Command and Control data from Ozy       
//
////////////////////////////////////////////////////////////


module Penelope (
        _10MHZ,ext_10MHZ,C122_clk,A6,A11,C19,C22,LED2,LED3,LED4,LED5,LED6,LED7,
        USEROUT0,USEROUT1,USEROUT2,USEROUT3,USEROUT4,USEROUT5,USEROUT6,DAC,nLVDSRXE,LVDSTXE,
        FPGA_PLL,PTT,PTT_in,nCS,CMODE,TLV320_CDIN,TLV320_CDOUT,TLV320_CBCLK,TLV320_CLRCIN,TLV320_CLRCOUT,TLV320_CMCLK,
        CC, ADCMOSI,ADCCLK,ADCMISO,nADCCS,PWM0,PWM1,PWM2,FPGA_PTT, DAC_ALC, SDA, SCL
        );

input  wire _10MHZ;
inout  tri  ext_10MHZ;  // 10MHz reference to Atlas pin C16
input  wire C122_clk;
output wire A11;        // CDOUT_P (Mic) to Atlas bus 
input  wire C19;        // I_data (NWire I&Q) from Ozy
input  wire A6;         // Q_data (NWire I&Q) from Ozy
inout  wire C22;        // P_IQ_sync from Penelope to Ozy
output wire LED2;
output wire LED3;
output wire LED4;
output wire LED5;
output wire LED6;
output wire LED7;
output wire USEROUT0;
output wire USEROUT1;
output wire USEROUT2;
output wire USEROUT3;
output wire USEROUT4;
output wire USEROUT5;
output wire USEROUT6;
output reg [13:0] DAC;
output wire nLVDSRXE;
output wire LVDSTXE;
output wire FPGA_PLL;
input  wire PTT;        // PTT from mic connector or DB25 pin 1
output tri  PTT_in;     // PTT on Atlas bus - C15
output wire nCS;
output wire CMODE;
output wire TLV320_CDIN;
input  wire TLV320_CDOUT;      // Mic or Line In data from TLV320
output wire TLV320_CBCLK;
output wire TLV320_CLRCIN;
output wire TLV320_CLRCOUT;
output wire TLV320_CMCLK;      // 12.288MHz
input  wire CC;                // Command & Control data from Ozy C20
output wire ADCMOSI;
output wire ADCCLK;
input  wire ADCMISO;
output wire nADCCS;
output wire PWM0;
output wire PWM1;
output wire PWM2;
output wire FPGA_PTT;   // controls PTT FET Q3
output reg  DAC_ALC;	// PWM drive level for PennyLane
inout  wire SDA;			// ATL SDA bus line, Atlas A21, FPGA pin 69
input  wire SCL;			// ATL SCL bus line, Atlas A20, FPGA pin 72

wire   source_122MHZ;   // Set when internal 122.88MHz source is used and sent to LVDS

assign CMODE    = 1'b0; // Set to 0 for I2C mode
assign nCS      = 1'b1; // I2C address of TLV320 is 0x1B

localparam SERIAL = 8'd40;  // software version serial number = 4.0

localparam C122_TPD = 1.3;

//----------------------------------
//		Reset
//----------------------------------


reg C122_rst;
reg [10:0] C122_rst_cnt;

always @(posedge C122_clk)
begin
  if (!C122_rst_cnt[10])
    C122_rst_cnt <= #C122_TPD C122_rst_cnt + 1'b1;

  C122_rst <= #C122_TPD C122_rst_cnt[10] ? 1'b0 : 1'b1;
end

//---------------------------
//    CLOCKS
//---------------------------

localparam SPEED_48K = 2'b00;

reg        C122_DFS1, C122_DFS0;
reg        C122_cgen_rst;
reg  [1:0] C122_SPEED;

// create a slower system clock = 122.88Mhz / 10 = 12.288Mhz
clk_div TLVCLK (.reset(C122_rst), .clk_in(C122_clk), .clk_out(TLV320_CMCLK));

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

wire C122_cbrise, C122_cbfall;
wire C122_CBCLK, CLRCLK;

clk_lrclk_gen clrgen 
    (.reset(C122_cgen_rst), .CLK_IN(C122_clk), .BCLK(C122_CBCLK),
     .Brise(C122_cbrise), .Bfall(C122_cbfall), .LRCLK(CLRCLK), .Speed(SPEED_48K));

assign TLV320_CBCLK   = C122_CBCLK;
assign TLV320_CLRCIN  = CLRCLK;
assign TLV320_CLRCOUT = CLRCLK;

wire LRfall;
clk_lrclk_gen lrgen (.reset(C122_cgen_rst), .CLK_IN(C122_clk), .BCLK(), .LRCLK(),
                     .LRfall(LRfall), .Speed({C122_DFS1,C122_DFS0}));
                     
wire pll192;
clk_lrclk_gen clr192gen (.reset(C122_cgen_rst), .CLK_IN(C122_clk), .BCLK(), .LRCLK(pll192),
                     .LRfall(), .Speed(2'b10));
                     
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
    count_12288 <= count_12288 + 10'b1;
end

// Apply to EXOR phase detector 
assign FPGA_PLL = ref_80khz ^ osc_80khz; 


// detect on board 122.88MHz clock using PLL locked signal so we can select external LVDS 122.88MHz clock if
// local clock not fitted to Penny.

wire PLL_locked;
PLL_clock_detector PLL_inst(.inclk0(C122_clk),.c0(),.locked(PLL_locked));    

// Select 122.88MHz source. If source_122MHZ set and PLL locked  then use Penelope's 122.88MHz clock and send to LVDS
// Otherwise get external clock from LVDS

//assign nLVDSRXE = (source_122MHZ && PLL_locked) ? 1'b1 : 1'b0;  // enable LVDS receiver if clock is external
//assign LVDSTXE  = (source_122MHZ && PLL_locked) ? 1'b1 : 1'b0;  // enable LVDS transmitter if Penny is the source 
//assign nLVDSRXE = LVDSTXE;

assign nLVDSRXE = 1'b1;
assign LVDSTXE  = 1'b1;


// select 10MHz reference source. If ref_ext is set use Penelope's 10MHz ref and send to Atlas C16

wire reference;
wire ref_ext;        // Set when internal 10MHz reference sent to Atlas C16

assign reference = ref_ext ? _10MHZ : ext_10MHZ; 
assign ext_10MHZ = ref_ext ? _10MHZ : 1'bz;    // C16 is bidirectional so set high Z if input. 


//-----------------------------
//    ADC SPI interface 
//-----------------------------

wire [11:0] AIN1;						// holds 12 bit ADC value of Forward Power from Alex
wire [11:0] AIN2;						// holds 12 bit ADC value of Reverse Power from Alex
wire [11:0] AIN5;  					// holds 12 bit ADC value of Penny(Lane) output power detector.
reg  [11:0] C122_AIN1;				// holds 12 bit ADC value of Reverse Power from Alex
reg  [11:0] C122_AIN2;				// holds 12 bit ADC value of Forward Power from Alex
reg  [11:0] C122_Power_out;		// holds Penny(Lane) power output in C122_clk domain 
reg  [11:0] temp;
reg  [11:0] temp1;
reg  [11:0] temp2;
	
ADC ADC_SPI(.clock(C122_CBCLK), .SCLK(ADCCLK), .nCS(nADCCS), .MISO(ADCMISO), .MOSI(ADCMOSI), .AIN1(AIN1),.AIN2(AIN2),.AIN5(AIN5));

// transfer ADC data into C122 clock domain
always @ (posedge C122_clk)
begin
	{C122_Power_out, temp} <= {temp, AIN5};
	{C122_AIN1, temp1} <= {temp1, AIN1};
	{C122_AIN2, temp2} <= {temp2, AIN2};
end 



	
	
//-------------------------------------------------
//       I2C bus interface, sends firmware version number, Penny_pwr_out, FWD, REV power levels via the 
//       two-line i2c (SDA/SCL) bus
//-------------------------------------------------

I2C I2C_inst(.CLK(TLV320_CMCLK), 
				 .sda(SDA), .scl(SCL), 
				 .version_no(SERIAL), 
				 .Penny_pwr_out(C122_Power_out), 
				 .FWD(C122_AIN1), 
				 .REV(C122_AIN2)
				 );


reg   [31:0] C122_phase_word;

//---------------------------------------------------------
//                 Transmitter code 
//---------------------------------------------------------	

/* 
	The gain distribution of the transmitter code is as follows.
	Since the CIC interpolating filters do not interpolate by 2^n they have an overall loss.
	
	The overall gain in the interpolating filter is ((RM)^N)/R.  So in this case its 2560^4.
	This is normalised by dividing by ceil(log2(2560^4)).
	
	In which case the normalized gain would be (2560^4)/(2^46) = .6103515625
	
	The CORDIC has an overall gain of 1.647.
	
	Since the CORDIC takes 16 bit I & Q inputs but output needs to be truncated to 14 bits, in order to
	interface to the DAC, the gain is reduced by 1/4 to 0.41175
	
	We need to be able to drive to DAC to its full range in order to maximise the S/N ratio and 
	minimise the amount of PA gain.  We can increase the output of the CORDIC by multiplying it by 4.
	This is simply achieved by setting the CORDIC output width to 16 bits and assigning bits [13:0] to the DAC.
	
	The gain distripution is now:
	
	0.61 * 0.41174 * 4 = 1.00467 
	
	This means that the DAC output will wrap if a full range 16 bit I/Q signal is received. 
	This can be prevented by reducing the output of the CIC filter.
	
	If we subtract 1/128 of the CIC output from itself the level becomes
	
	1 - 1/128 = 0.9921875
	
	Hence the overall gain is now 
	
	0.61 * 0.9921875 * 0.41174 * 4 = 0.996798
	

*/

wire signed [47:0] IQ_sync_data;
reg signed [47:0]C122_cic_iq;

// latch I&Q data on strobe from CIC
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

//---------------------------------------------------------------------------
//		Get P_IQ_data (Atlas C19)
//---------------------------------------------------------------------------

wire       IQ_rdy;
reg        IQ_ack, do_ack;

NWire_rcv #(.OSL(48), .OSW(1), .ICLK_FREQ(122000000), .XCLK_FREQ(192000), .SLOWEST_FREQ(10000))
     IPWM (.irst(C122_rst), .iclk(C122_clk), .xrst(C122_rst), .xclk(clk192),
            .xrcv_rdy(IQ_rdy), .xrcv_ack(IQ_ack), .xrcv_data(IQ_sync_data),
            .din(C19));

assign C22 = C122_clock_select[1] ? 1'bz : pll192; // P_IQ_sync
assign clk192 = C122_clock_select[1] ? C22 : pll192;

wire req1;
wire [16:0] y2_r, y2_i;

CicInterpM5 #(.RRRR(640), .IBITS(24), .OBITS(17), .GBITS(38)) in2 (C122_clk, 1'b1, req1, C122_cic_iq[47:24], C122_cic_iq[23:0], y2_r, y2_i);

//---------------------------
//    CORDIC NCO 
//---------------------------

// Code rotates input at set frequency and produces I & Q /
cpl_cordic # (.IN_WIDTH(17))
 		cordic_inst (.clock(C122_clk), .frequency(C122_phase_word), .in_data_I(y2_i), 
		.in_data_Q(y2_r), .out_data_I(C122_cordic_i_out), .out_data_Q());		

wire signed [21:0] C122_cordic_i_out;

// NOTE:  I and Q inputs reversed to give correct sideband out

/* 
  We can use either the I or Q output from the CORDIC directly to drive the DAC.

    exp(jw) = cos(w) + j sin(w)

  When multplying two complex sinusoids f1 and f2, you get only f1+f2, no
  difference frequency.

      Z = exp(j*f1) * exp(j*f2) = exp(j*(f1+f2))
        = cos(f1 + f2) + j sin(f1 + f2)
*/

// the CORDIC output is stable on the negative edge of the clock
// only enable RF out when PTT is active

always @ (negedge C122_clk)
begin
	if (PTT_out) 
		DAC[13:0] <= C122_cordic_i_out[21:8];   //gain of 4
	else DAC[13:0] <= 14'd0;
end 


//--------------------------------------------
// Single bit PWM 16 bit D/A converters
//--------------------------------------------

// not presently used 

//wire [15:0] PWM0_Data;
//wire [15:0] PWM1_Data;
//wire [15:0] PWM2_Data;
//reg  [15:0] PWM0_Data_in;
//reg  [15:0] PWM1_Data_in;
//reg  [15:0] PWM2_Data_in;
//reg  [16:0] PWM0_accumulator;
//reg  [16:0] PWM1_accumulator;
//reg  [16:0] PWM2_accumulator;
//
//assign PWM0_Data = 0;
//assign PWM1_Data = 0;
//assign PWM2_Data = 0;
//
//
//// This runs off the 122.88MHz clock to provide adequate resolution.
//
//always @(posedge C122_clk)
//begin
//  PWM0_Data_in      <= PWM0_Data + 16'h8000;         // so that 0 in gives 50:50 mark/space
//  PWM1_Data_in      <= PWM1_Data + 16'h8000;
//  PWM2_Data_in      <= PWM2_Data + 16'h8000;
//  PWM0_accumulator  <= PWM0_accumulator[15:0] + PWM0_Data_in;
//  PWM1_accumulator  <= PWM1_accumulator[15:0] + PWM1_Data_in;
//  PWM2_accumulator  <= PWM2_accumulator[15:0] + PWM2_Data_in;
//end
//
//assign PWM0 = PWM0_accumulator[16];       // send to PWM LPFs for now 
//assign PWM1 = PWM1_accumulator[16]; 
//assign PWM2 = PWM2_accumulator[16]; 

// for board tests
assign PWM0 = DAC_ALC;
assign PWM1 = 0;
assign PWM2 = 0;

//------------------------------------------------------------
//  Set Power Output (PennyLane only)
//------------------------------------------------------------

// PWM DAC to set drive current to DAC on PennyLane. PWM_count increments 
// using C122_clk. If the count is less than the Drive 
// Level set by the PC then DAC_ALC will be high, otherwise low.  

reg [7:0] PWM_count;
always @ (posedge C122_clk)
begin 
	PWM_count <= PWM_count + 1'b1;
	if (Drive_Level >= PWM_count)
		DAC_ALC <= 1'b1;
	else 
		DAC_ALC <= 1'b0;
end 


//-----------------------------------------
//    Command and Control Decoder 
//-----------------------------------------

/*
	The C&C encoder broadcasts data over the Atlas bus C20 for
	use by other cards e.g. Mercury and Penelope.
	
	The data format is as follows:
	
	<[93:90]Mercs_installed>[89:88]DFS1,DFS0><[87]PTT><[86:84]address><[83:52]frequency><[51:49]clock_source><[48:42]OC>
	<[41]Mode><[40]PREAMP><[39]DITHER><[38]RAND><[37:36]ATTEN><[35:34]TX_relay><[33]Rout><[32:31]RX_relay>
	<[30:16]Alex><[15:8]Drive_Level><[7:5]multiRx_mode><[4:1]RxN_preamps><[0]common_Merc_freq> 
	
	for a total of 91 bits. Frequency is in Hz and 32 bit binary format and 
	OC is the open collector data on Penelope. Mode is for a future Class E PA,
	DITHER and RAND are ADC settings and ATTEN the attenuator on Alex
	
	The clock source (clock_select) decodes as follows:
	
	x00  = 10MHz reference from Atlas bus ie Gibraltar
	x01  = 10MHz reference from Penelope
	x10  = 10MHz reference from Mercury
	0xx  = 122.88MHz source from Penelope 
	1xx  = 122.88MHz source from Mercury 
	
*/

wire   [96:0] rcv_data;				
wire          rcv_rdy;

// get necessary C & C data

parameter PENNY_ADDR = 3'b0; 		// set C&C address that Penny will respond to
reg         PTT_out;       		// PTT to Penelope
reg   [2:0] C122_clock_select;  	// 10MHz and 122.88MHz clock selection
reg   [6:0] OC;            		// Open Collector outputs data
reg   [7:0] Drive_Level;                // PennyLane drive level	

always @ (posedge C122_clk)
begin
  if (C122_rst)
  begin
    C122_DFS1         <= 1'b0;   // I/Q sampling rate selection
    C122_DFS0         <= 1'b0;
    PTT_out           <= 1'b0; 
    //C122_phase_word   <= 32'd136314880; //3900khz
    C122_phase_word   <= 32'd0;
    C122_clock_select <= 3'b001;
    OC                <= 7'b0;   // Penelope Open Collectors
    Drive_Level       <= 8'b0;	 // PennyLane drive level
  end
  else if (rcv_rdy)
  begin
    PTT_out      <= rcv_data[92];
    if (rcv_data[91:89] == PENNY_ADDR) // check that the C&C data is for this board
    begin
      C122_phase_word   <= rcv_data[88:57];
      C122_clock_select <= rcv_data[56:54];     
      OC                <= rcv_data[53:47]; 	// Penelope Open Collectors
      Drive_Level       <= rcv_data[12:5];		// PennyLane drive level	
    end
  end
end

/*
        <[96]sync0><[95]merc1_2_pres><[94:93]sampling_rate><[92]PTT><[91:89]address><[88:57]frequency><[56:54]clock_source>
        <[53:47]OC><[46:15]Alex_data><[14]DITHER><[13]RAND><[12:5]Drive_Level><[4]Alex_enable><[3:2]RxN_preamps><[1:0]adc2>
*/

NWire_rcv  #(.OSL(97), .OSW(1), .ICLK_FREQ(122880000), .XCLK_FREQ(122880000), .SLOWEST_FREQ(10000)) 
      CCrcv (.irst(C122_rst), .iclk(C122_clk), .xrst(C122_rst), .xclk(C122_clk),
             .xrcv_data(rcv_data), .xrcv_rdy(rcv_rdy), .xrcv_ack(rcv_rdy), .din(CC));

// if set use internal 10MHz TCXO and send to C16 else get from C16
assign ref_ext        = C122_clock_select[0];
// if set use internally and send to LVDS else get from LVDS. Force to 0 during reset 
assign source_122MHZ  = C122_rst ? 1'b0 : (C122_clock_select[2] == 1'b0);


I2S_xmit #(.DATA_BITS(32))  // CLRCLK running at 48KHz
  IQD (.rst(C122_rst), .lrclk(CLRCLK), .clk(C122_clk), .CBrise(C122_cbrise),
       .CBfall(C122_cbfall), .sample({I_sync_data,Q_sync_data}), .outbit(TLV320_CDIN));


//------------------------------------------------------
// send microphone data to CDOUT_P (Atlas A11)
//------------------------------------------------------

reg  [15:0] C122_mic_data;
wire        C122_mic_rdy;
wire [31:0] C122_mic_LR;

NWire_xmit #(.SEND_FREQ(48000),.OSL(16), .OSW(1), .ICLK_FREQ(122880000), .XCLK_FREQ(122880000), .LOW_TIME(1'b0))
      P_MIC (.irst(C122_rst), .iclk(C122_clk), .xrst(C122_rst), .xclk(C122_clk),
             .xdata(C122_mic_data), .xreq(LRfall), .xrdy(), .xack(), .dout(A11));

always @(posedge C122_clk)
begin
  if (C122_mic_rdy) // this happens before LRfall
    C122_mic_data <= C122_mic_LR[31:16]; // we're only using the Left data
end

I2S_rcv #(32,2,1) // WARNING: values 2,1 may need adjusting for best capture of data
   PJD (.xrst(C122_rst), .xclk(C122_clk), .xData(C122_mic_LR),
        .xData_rdy(C122_mic_rdy), .BCLK(C122_CBCLK), .LRCLK(CLRCLK), .din(TLV320_CDOUT));
        
 

///////////////////////////////////////////////////////////
//
//    PTT 
//
///////////////////////////////////////////////////////////

/* 
  The Atlas bus has a tri-state PTT line (C15 = PTT_in) that
  when high indicates a board on the bus has requested PTT. 
  When inactive set high Z, a pull down resistor on the bus will 
  set PTT_in to logic 0.
*/

assign  PTT_in    = PTT ? 1'bz: 1'b1;  // set high if PTT active(low) else high Z 
assign  FPGA_PTT  = PTT_out;       // turn PTT FET Q3 on when Txing

/////////////////////////////////////////////////////////////////
//
// General I O pins
//
/////////////////////////////////////////////////////////////////


// LEDs for testing       PCB LED Marking
// NOTE: LED1 is power on 
// Bar graph for power output 
assign LED2 = (AIN5 > 250)?  1'b0 : 1'b1;  
assign LED3 = (AIN5 > 500)?  1'b0 : 1'b1; 
assign LED4 = (AIN5 > 1000)? 1'b0 : 1'b1;  
assign LED5 = (AIN5 > 2000)? 1'b0 : 1'b1;  
assign LED6 = (AIN5 > 3000)? 1'b0 : 1'b1;  

reg[26:0]counter;
always @(posedge C122_clk) counter = counter + 1'b1;
assign LED7 = PTT_out ? counter[24] : counter[26];  // LED 7 flashes twice as fast when PTT active.

// User open collector outputs 
assign USEROUT0 = OC[0];
assign USEROUT1 = OC[1];
assign USEROUT2 = OC[2];
assign USEROUT3 = OC[3];
assign USEROUT4 = OC[4];
assign USEROUT5 = OC[5];
assign USEROUT6 = OC[6];

function integer clogb2;
input [31:0] depth;
begin
  for(clogb2=0; depth>0; clogb2=clogb2+1)
  depth = depth >> 1;
end
endfunction

endmodule 

