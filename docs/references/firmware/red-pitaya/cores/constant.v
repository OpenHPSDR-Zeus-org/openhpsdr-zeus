
`timescale 1 ns / 1 ps

module constant #
(
  parameter integer CONST_WIDTH = 1,
  parameter integer CONST_VALUE = 1
)
(
  output wire [CONST_WIDTH-1:0] dout
);

  assign dout = CONST_VALUE;

endmodule
