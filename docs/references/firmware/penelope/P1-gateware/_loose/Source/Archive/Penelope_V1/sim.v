`timescale 1 ns/100 ps


// Penelope simulation

module sim ();

reg           _10MHZ;     // 10MHz clock 
wire          ext_10MHZ;  // external 10MHz clock 
reg           _122MHZ;    // 122.88 Mhz
reg           RUN;
reg           LROUT;
wire          C4;         // LROUT (Rx audio) from Atlas bus
reg           CBCLK;
wire          C8;         // CBCLK from Atlas bus
reg           CLRCLK;
wire          C9;         // CLRCLK from Atlas bus
reg           C12;        // CDIN (I&Q) from Atlas bus
reg           C17;        // CLK_MCLK from Atlas bus
reg           PTT;        // PTT from mic connector or DB25 pin 1
wire          PTT_in;		  // PTT on Atlas bus - C15
reg           CDOUT;      // Mic or Line In out to Atlas Bus
reg           CC; 			  // Command & Control data from Ozy C12
reg           DCMISO;
wire          LVDSTXE;
wire          nLVDSRXE;
wire   [13:0] DAC, DAC_orig;

assign C4 = LROUT;
assign C8 = CBCLK;
assign C9 = CLRCLK;


//	The data fomat is as follows:
//	
//	<1 bit PTT><4 bits address><32 bits frequency><4 bits clock_s><7 bits OC><1 bit mode> 
//	
//	for a total of 49 bits. Frequency is in Hz and OC is the open collector data on Penelope
reg    [58:0] CC_STREAM;

// Initialize Inputs
initial
begin
  RUN = 1'b0; // make _10MHZ clock wait a while
  // Simulate power up reset to internal registers that matter
  P1.CMCLK_counter = 3'b0;
  P1.PCLK_12MHZ = 1'b0;
  P1.reset_count = 11'b0;
  P1.IQ_state = 3'b0;
//  P1.ALC_state = 1'b0;
  P1.CC_state = 2'b0;
  P1.PTT_out = 1'b0;
  P1.frequency_HZ = 0;
  P1.clock_select = 4'b0;
  P1.OC = 7'b0;
  P1.div_dly = 4'b0;
  P1.freq_changed = 1'b0;
  P1.ALC_out = 0;
//  P1.ALC_in = 0;
//  P1.previous = 0;
  P1.I_data = 0;
  P1.Q_data = 0;
  P1.I_sync_data = 0;
  P1.Q_sync_data = 0;
  P1.cic_i = 0;
  P1.cic_q = 0;

  P2.CMCLK_counter = 3'b0;
  P2.PCLK_12MHZ = 1'b0;
  P2.reset_count = 11'b0;
  P2.IQ_state = 3'b0;
  P2.ALC = 1'b0;
  P2.CC_state = 2'b0;
  P2.PTT_out = 1'b0;
  P2.frequency_HZ = 0;
  P2.clock_select = 4'b0;
  P2.OC = 7'b0;
  P2.ALC_out = 0;
  P2.ALC_in = 0;
  P2.previous = 0;
  P2.I_data = 0;
  P2.Q_data = 0;
  P2.I_sync_data = 0;
  P2.Q_sync_data = 0;
  P2.cic_i = 0;
  P2.cic_q = 0;

  // initialize necessary input pins
  PTT = 1'b0;
  CC_STREAM = 0; //<1 bit PTT><4 bits address><32 bits frequency><4 bits clock_s><7 bits OC><1 bit mode>
  #500;
  RUN = 1'b1;
  #50;
  if (_10MHZ === 1'bx)
  begin
    $display ("ERROR: _10MHZ clock not running");
    $stop;
  end  
  if (_122MHZ === 1'bx)
  begin
    $display ("ERROR: _122MHZ clock not running");
    $stop;
  end  
  if (ext_10MHZ === 1'bx)
  begin
    $display ("ERROR: ext_10MHZ clock not running");
    $stop;
  end
  if (CBCLK === 1'bx)
  begin
    $display ("ERROR: CBCLK clock not running");
    $stop;
  end  
  if (CLRCLK === 1'bx)
  begin
    $display ("ERROR: CLRCLK clock not running");
    $stop;
  end  

  @(posedge CLRCLK);
  @(posedge CLRCLK);
  @(posedge CLRCLK);
  @(posedge CLRCLK);
  @(posedge CLRCLK);
  //<1 bit PTT><4 bits address><32 bits frequency><4 bits clock_s><7 bits OC><1 bit mode>
  CC_STREAM = {1'b1, 4'b0, 32'd22000000, 4'b1010, 7'b1011010, 1'b1, 10'b0};
  @(posedge CLRCLK);
  @(posedge CLRCLK);
  @(posedge CLRCLK);
  @(posedge CLRCLK);  // manually check LRCLK and BCLK waveforms here
  $display ("Succesful simulation completion");
  $stop;
end


Penelope P1 (._10MHZ(_10MHZ), .ext_10MHZ(ext_10MHZ), ._122MHZ(_122MHZ), .A5(A5),.A11(A11),
		.C4(C4), .C8(C8), .C9(C9), .C12(C12), .C17(C17), .LED2(LED2), .LED3(LED3),
		.LED4(LED4), .LED5(LED5), .LED6(LED6), .LED7(LED7), .USEROUT0(USEROUT0),
		.USEROUT1(USEROUT1), .USEROUT2(USEROUT2), .USEROUT3(USEROUT3), .USEROUT4(USEROUT4),
		.USEROUT5(USEROUT5), .USEROUT6(USEROUT6), .DAC(DAC), .nLVDSRXE(nLVDSRXE),
		.LVDSTXE(LVDSTXE), .FPGA_PLL(FPGA_PLL), .PTT(PTT), .PTT_in(PTT_in), .nCS(nCS), .CMODE(CMODE),
		.CDOUT(CDOUT), .CBCLK(), .CLRCIN(CLRCIN), .CLRCOUT(CLRCOUT), .LROUT(),
		.CMCLK(CMCLK), .CC(CC), .ADCMOSI(ADCMOSI), .ADCCLK(ADCCLK), .ADCMISO(ADCMISO),
		.nADCCS(nADCCS), .PWM0(PWM0), .PWM1(PWM1), .PWM2(PWM2), .FPGA_PTT(FPGA_PTT)
				);

Penelope_orig P2(._10MHZ(_10MHZ), .ext_10MHZ(ext_10MHZ_orig), ._122MHZ(_122MHZ), .A5(A5_orig),.A11(A11_orig),
		.C4(C4), .C8(C8), .C9(C9), .C12(C12), .C17(C17), .LED2(LED2_orig), .LED3(LED3_orig),
		.LED4(LED4_orig), .LED5(LED5_orig), .LED6(LED6_orig), .LED7(LED7_orig), .USEROUT0(USEROUT0_orig),
		.USEROUT1(USEROUT1_orig), .USEROUT2(USEROUT2_orig), .USEROUT3(USEROUT3_orig), .USEROUT4(USEROUT4_orig),
		.USEROUT5(USEROUT5_orig), .USEROUT6(USEROUT6_orig), .DAC(DAC_orig), .nLVDSRXE(nLVDSRXE_orig),
		.LVDSTXE(LVDSTXE_orig), .FPGA_PLL(FPGA_PLL_orig), .PTT(PTT), .PTT_in(PTT_in_orig), .nCS(nCS_orig), .CMODE(CMODE_orig),
		.CDOUT(CDOUT), .CBCLK(), .CLRCIN(CLRCIN_orig), .CLRCOUT(CLRCOUT_orig), .LROUT(),
		.CMCLK(CMCLK_orig), .CC(CC), .ADCMOSI(ADCMOSI_orig), .ADCCLK(ADCCLK_orig), .ADCMISO(ADCMISO),
		.nADCCS(nADCCS_orig), .PWM0(PWM0_orig), .PWM1(PWM1_orig), .PWM2(PWM2_orig), .FPGA_PTT(FPGA_PTT_orig)
				);


///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// continuously send some I & Q data on the CDIN (pin C12)

reg [15:0] iqd;
initial
begin
  #2000;
  iqd = 0;
  while(1)
  begin
    C12 = 1'bz;
    @(negedge CLRCLK); // wait for CLRCLK to go low 
iqd = -10000;
    IQ_DATA(iqd);
    C12 = 1'bz;
    @(posedge CLRCLK); // wait for CLRCLK to go high
iqd = 10000;
    IQ_DATA(iqd);
    iqd = iqd + 16'h8001;
  end
end

task IQ_DATA;
input [15:0] din;
integer iq_cnt;
begin
  @(negedge CBCLK); // 1st falling edge of CBCLK after LRCLK goes low
//  @(negedge CBCLK); // 2nd falling edge of CBCLK after LRCLK goes low
  for (iq_cnt = 0; iq_cnt < 16; iq_cnt = iq_cnt + 1)
  begin
    C12 = din[15-iq_cnt]; // MSB first
    @(negedge CBCLK);
  end
end
endtask

///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// continuously send C & C data on the CC pin

reg [58:0] ccd;
initial
begin
  #2000;
  while(1)
  begin
    ccd <= CC_STREAM;
    @(negedge CLRCLK);
    CC_DATA(ccd);
  end
end

task CC_DATA;
input [58:0] din;
integer acnt;
begin
  @(negedge CBCLK); // 1st falling edge of CBCLK after LRCLK goes low
//  @(negedge CBCLK); // 2nd falling edge of CBCLK after LRCLK goes low
  for (acnt = 0; acnt < 59; acnt = acnt + 1)
  begin
    CC   = din[58-acnt]; // MSB first
    @(negedge CBCLK);
  end
end
endtask

///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
parameter CB_SPEED = 162.8;  // CBCLK = 12.288Mhz/4 = 3.07 = 325.5nS, CB_SPEED =  325.5nS/2 = 162.8nS

initial
begin
  CBCLK = 1'b0;
  $display ("CBCLK started ");
  #66;
  wait(RUN);
  forever
  begin
    #CB_SPEED CBCLK = 1'b1;
    #CB_SPEED CBCLK = 1'b0;
  end
end

///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
integer k;
initial
begin
  CLRCLK = 1'b0;
  $display ("CLRCLK started ");
  #3;
  wait(RUN);
  @ (posedge CBCLK);
  forever
  begin
    @ (negedge CBCLK);
    #15
    CLRCLK = 1'b0;
    for (k = 0; k < 32; k = k + 1)
      @ (posedge CBCLK);
    @ (negedge CBCLK);
    #15
    CLRCLK = 1'b1;
    for (k = 0; k < 32; k = k + 1)
      @ (posedge CBCLK);
  end
end


///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
parameter CLK10_SPEED = 50.0;  // CLK_MCLK = 10Mhz = 100nS, CPUSPEED = 100nS/2 = 50.0nS

initial
begin
  _10MHZ = 1'b0;
  $display ("_10Mhz started ");
  #5;
  wait(RUN);
  forever
  begin
    #CLK10_SPEED _10MHZ = 1'b1;
    #CLK10_SPEED _10MHZ = 1'b0;
  end
end


///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
parameter CLK123_SPEED = 4.07;  // IFCLK = 122.88Mhz = 8.14nS, CPUSPEED = 8.14nS/2 = 4.07nS

initial
begin
  $display ("_122MHZ started");
  _122MHZ = 1'b0;
  #15  //start at a different time than CLK_MCLK
  forever
  begin
    #CLK123_SPEED _122MHZ = 1'b1;
    #CLK123_SPEED _122MHZ = 1'b0;
  end
end
///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
parameter C17_SPEED = 40.7;  // CLK_MCLK = 12.288Mhz = 81.38nS, CPUSPEED = 81.38nS/2 = 40.7nS

initial
begin
  C17 = 1'b0;
  $display ("C17 (i.e. CLK_MCLK) started");
  #47;
  wait(RUN);
  forever
  begin
    #C17_SPEED C17 = 1'b1;
    #C17_SPEED C17 = 1'b0;
  end
end


endmodule