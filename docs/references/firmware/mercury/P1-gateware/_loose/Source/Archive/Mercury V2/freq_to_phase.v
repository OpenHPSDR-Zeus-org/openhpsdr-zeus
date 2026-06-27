/*	
	Calculates  ratio = fo/fs = frequency/122.88Mhz where frequency is in MHz
	Each calculation should take no more than 1 CBCLK
*/

module freq_to_phase(clock, CBCLK, frequency_HZ, sync_phase_word );
	
input clock;
input CBCLK;
input [31:0] frequency_HZ;   	// frequency control bits for CORDIC
output reg  [31:0] sync_phase_word;

localparam M2 = 32'd1172812403;  // B57 = 2^57.  B57/122880000 = M2
reg  [31:0] phase_word;     	// CBCLK domain 
wire [63:0] ratio;
reg  [31:0] spw0;


assign ratio = frequency_HZ * M2; // B0 * B57 number = B57 number

always @ (posedge CBCLK)   // save phase word
begin
  phase_word <= ratio[56:25]; // B57 -> B32 number since R is always >= 0
end

always @ (posedge clock)   
begin
  {sync_phase_word, spw0} <= {spw0, phase_word};  // from CBCLK domain to clock domain
end 

endmodule 
