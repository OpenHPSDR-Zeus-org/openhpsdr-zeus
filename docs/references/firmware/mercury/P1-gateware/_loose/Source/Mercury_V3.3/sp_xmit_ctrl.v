/*
 Spectrum FIFO and NWire_xmit control logic to send captured spectrum FIFO data
 to Ozy via an NWire_xmit

 Kirk Weedman

 Mar. 22, 2009

*/

`timescale 1ns/100ps

module sp_xmit_ctrl (rst, clk, trigger, fifo_full, fifo_empty, fifo_wreq,
                     fifo_rreq, xfer_req, xfer_rdy, xfer_ack);

input  wire        rst;
input  wire        clk;
input  wire        trigger;    // signal from Ozy to get more data
input  wire        fifo_full;  // Spectrum FIFO full flag
input  wire        fifo_empty; // Spectrum FIFO empty flag
output reg         fifo_wreq;  // Spectrum FIFO write request
output reg         fifo_rreq;  // Spectrum FIFO read request - data comes out on next clk
output reg         xfer_req;   // request input to NWire_xmit
input  wire        xfer_rdy;   // NWire_xmit is ready for more data
input  wire        xfer_ack;   // acknowledge the xfer_rdy;

reg  [2:0] sp_state;
reg  [2:0] sp_state_next;

localparam IDLE      = 0,
           TRIG      = 1,
           CAPTURE   = 2,
           SEND_RDY  = 3,
           SEND_REQ  = 4,
           SEND_DONE = 5,
           T_RDY     = 6,
           T_REQ     = 7;


// When Mercury powers up or is reprogrammed, the spectrum data stream of
// 4096 words needs to be resynchronized to Ozy.  To do this, note that the
// trigger signal from Ozy may be high or low but we wont know where in the
// stream we're at.  Therefore we'll feed it data until trigger goes from
// low to high at which point we can change from SYNC to RUN mode.
          
localparam TPD = 1;

always @(posedge clk)
begin
  if (rst)
    sp_state <= #TPD 1'b0;
  else
    sp_state <= #TPD sp_state_next;
end

always @*
begin
  case (sp_state)
    IDLE:
    begin
      fifo_wreq = 1'b0; // no reading/writing to FIFO while getting in synch with trigger
      fifo_rreq = 1'b0;
      xfer_req  = 1'b0;
      if (trigger)
        sp_state_next = IDLE;  // Wait for trigger to go low
      else
        sp_state_next = T_RDY;
    end

    TRIG:  // this is not the first state after reset, IDLE is!
    begin
      fifo_wreq = 1'b0;
      fifo_rreq = 1'b0;
      xfer_req  = 1'b0;
      if (trigger)
        sp_state_next = CAPTURE;
      else
        sp_state_next = IDLE;
    end

    CAPTURE:
    begin
      fifo_wreq = !fifo_full;
      fifo_rreq = 1'b0;
      xfer_req  = 1'b0;
      if (fifo_full)
        sp_state_next = SEND_RDY;
      else
        sp_state_next = CAPTURE;
    end

    SEND_RDY:
    begin
      fifo_wreq = 1'b0;
      fifo_rreq = xfer_rdy; // get 16 bit data from FIFO when in RUN mode
      xfer_req  = 1'b0;
      if (!xfer_rdy)
        sp_state_next = SEND_RDY;  // Wait for NWire_xmit to be ready for more data
      else
        sp_state_next = SEND_REQ;
    end

    SEND_REQ:
    begin
      fifo_wreq = 1'b0;
      fifo_rreq = 1'b0;
      xfer_req  = 1'b1;
      if (!xfer_ack)
        sp_state_next = SEND_REQ;  // Wait for NWire_xmit to acknowledge my request before removing it
      else
        sp_state_next = SEND_DONE;
    end

    SEND_DONE:
    begin
      fifo_wreq = 1'b0;
      fifo_rreq = 1'b0;
      xfer_req  = 1'b0;
      if (!fifo_empty)
        sp_state_next = SEND_RDY; // FIFO still has more data to send via NWire_xmit
      else
        sp_state_next = TRIG; // done transmitting the FIFO data, go wait for next trigger
    end


    // Send data to Ozy until trigger goes high
    T_RDY:
    begin
      fifo_wreq = 1'b0; // no reading/writing to FIFO while getting in synch with trigger
      fifo_rreq = 1'b0;
      xfer_req  = 1'b0;
      if (trigger)
        sp_state_next = CAPTURE;  // trigger just went high so now we're synched.
      else if (!xfer_rdy)
        sp_state_next = T_RDY;  // Wait for NWire_xmit to be ready for more data
      else
        sp_state_next = T_REQ;
    end

    T_REQ:
    begin
      fifo_wreq = 1'b0;
      fifo_rreq = 1'b0;
      xfer_req  = 1'b1; // send data (any value) to Ozy until trigger goes high
      if (!xfer_ack)
        sp_state_next = T_REQ;  // Wait for NWire_xmit to acknowledge my request before removing it
      else
        sp_state_next = T_RDY;
    end


    default:
    begin
      fifo_wreq = 1'b0;
      fifo_rreq = 1'b0;
      xfer_req  = 1'b0;
      sp_state_next = IDLE;
    end
  endcase
end

endmodule
