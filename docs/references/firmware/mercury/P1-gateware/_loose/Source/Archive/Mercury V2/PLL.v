/* 
	Divide the 10MHz reference and 122.88MHz clock to give 80kHz signals.
	Apply these to an EXOR phase detector. If the 10MHz reference is not
	present the EXOR output will be a 80kHz square wave. When passed through 
	the loop filter this will provide a dc level of (3.3/2)v which will
	set the 122.88MHz VCXO to its nominal frequency.
	The selection of the internal or external 10MHz reference for the PLL
	is made using ref_ext.
*/

module PLL(clock, reference, FPGA_PLL, osc_80khz, ref_80khz);

input  clock;
inout  reference;
output FPGA_PLL;
output osc_80khz;
output ref_80khz;

// div 10 MHz ref clock by 125 to get 80 khz 

wire ref_80khz; 
reg osc_80khz; 

oddClockDivider refClockDivider(reference, ref_80khz); 

// Divide  122.88 MHz by 1536 to get 80 khz 
reg [9:0] count_12288; 

always @ (posedge clock)
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

endmodule

