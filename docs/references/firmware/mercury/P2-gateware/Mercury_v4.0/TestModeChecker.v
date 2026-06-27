/*
  This module is used to check the data flow in and out of the RX FIFO in Ozy_Janus.v,
  through the NWire_xmit in Ozy to the NWire_rcv in Mercury.  It should match the
  defined FPGA Test Mode data stream.  If not it generates
  an error called tmc_err which can then be used to flash a code on an LED
*/

`timescale 1 ns/100 ps

module TestModeChecker (rst, clk, data, rdy, tmc_err);
input  wire        rst;
input  wire        clk;
input  wire [31:0] data;
input  wire        rdy;
output wire        tmc_err;

localparam TMC_IDLE   = 0,
           TMC_GO     = 1;



reg    [1:0] tmc_state;
reg    [1:0] tmc_state_next;
wire         seq_err;
reg    [7:0] seq_cnt;
wire   [7:0] sp1;

assign tmc_err = (tmc_state != TMC_GO);

always @(posedge clk)
begin
  if (rst)
    tmc_state <= TMC_IDLE;
  else
    tmc_state <= tmc_state_next;

  if (rst)
    seq_cnt <= 3'd1;
  else if (tmc_state == TMC_GO)
  begin
    if (seq_cnt == 8'hFD)
      seq_cnt <= 8'h01;
    else if (rdy)
      seq_cnt <= seq_cnt + 3'd4; // next value we should see when rdy goes high
  end
end

assign sp1 = seq_cnt + 1'b1;

assign seq_err = ({8'b0,seq_cnt, 8'b0,sp1} != data) & rdy;

always @*
begin
  case(tmc_state)
    TMC_IDLE: // wait until we see the 00010002 data
    begin
      if ((data == 32'h00010002) && rdy)
        tmc_state_next = TMC_GO;
      else
        tmc_state_next = TMC_IDLE;
    end

    TMC_GO: // continue to check data sequence
    begin
      if (seq_err)
        tmc_state_next = TMC_IDLE;
      else
        tmc_state_next = TMC_GO;
    end

    default:
      tmc_state_next = TMC_IDLE;
  endcase
end

endmodule