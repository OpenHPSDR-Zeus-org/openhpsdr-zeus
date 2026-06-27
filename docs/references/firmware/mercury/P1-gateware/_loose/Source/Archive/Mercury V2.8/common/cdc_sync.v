// Simple Clock Domain Crossing module that double buffers data
// also simulation version to model CDC issues - in progress

`timescale 1 ns/100 ps

module cdc_sync #(parameter SIZE=1)
  (input  wire [SIZE-1:0] siga,
   input  wire            rstb, clkb, 
   output reg  [SIZE-1:0] sigb);

`ifdef SIMULATION
wire [SIZE-1:0] y1;
reg  [SIZE-1:0] q1a, q1b;
reg  [SIZE-1:0] DLY = 1'b0;

assign y1 = (~DLY & q1a) | (DLY & q1b);

always @(posedge clkb)
begin
  if (rstb)
    {sigb,q1b,q1a} <= '0;
  else
    {sigb,q1b,q1a} <= {y1,q1a,siga};
end
`else // synthesis
reg [SIZE-1:0] q1;

always @(posedge clkb)
begin
  if (rstb)
    {sigb,q1} <= 2'b00;
  else
    {sigb,q1} <= {q1,siga};
end
`endif
endmodule