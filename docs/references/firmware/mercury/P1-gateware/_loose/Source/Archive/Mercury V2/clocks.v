// Generate clocks for Atlas bus etc


module clocks(
	input clock,
	input CBCLK,
	output reg MCLK_12MHZ,
	output reg SPI_clk
	);

// Generate 122.88MHz/10 MCLK_12MHZ for Atlas bus

reg [2:0] MCLK_count;

always @ (posedge clock)
begin
  if (MCLK_count == 4)  // divide 122.88MHz clock by 10 to give 12.288MHz
  begin
    MCLK_12MHZ <= ~MCLK_12MHZ;
    MCLK_count <= 0;
  end
  else
    MCLK_count <= MCLK_count + 1'b1;
end

// Generate CBCLK (3.072MHz)/4 for SPI interface
	
reg SPI_count;
always @(posedge CBCLK)
begin
  if(SPI_count == 1)
  begin
    SPI_clk <= ~SPI_clk;
    SPI_count <=0;
  end 
  else
    SPI_count <= SPI_count + 1'b1;
end	

endmodule
