// A Digital Output Randomizer is fitted to the LT2208. This complements bits 15 to 1 if 
// bit 0 is 1. This helps to reduce any pickup by the A/D input of the digital outputs. 
// We need to de-ramdomize the LT2208 data if this is turned on. 

module random(
	input clock,
	input signed [15:0]ADC_samples,
	input random,
	output reg[15:0]out
	);
				
always @ (posedge clock) 
begin 
  if (random)begin  // RAND set so de-ramdomize
    if (ADC_samples[0])
      out <= {~ADC_samples[15:1],ADC_samples[0]};
    else
      out <= ADC_samples;
  end
  else
    out <= ADC_samples;  // not set so just copy data
end 

endmodule
