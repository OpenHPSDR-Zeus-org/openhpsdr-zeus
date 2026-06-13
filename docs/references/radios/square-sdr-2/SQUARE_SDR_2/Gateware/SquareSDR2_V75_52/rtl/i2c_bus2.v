`timescale 1ns / 1ps

module i2c_bus2
(
  clk,
  rst,

  cmd_addr,
  cmd_data,
  cmd_rqst,
  cmd_ack,
  cmd_resp_data,

  read_done,
  en_i2c2,
  ready,

  scl_i,
  scl_o,
  scl_t,
  sda_i,
  sda_o,
  sda_t
);

input         clk;
input         rst;

// Command slave interface
input  [5:0]  cmd_addr;
input  [31:0] cmd_data;
input         cmd_rqst;
output        cmd_ack;
output [31:0] cmd_resp_data;

output  logic read_done;
output  logic en_i2c2;
output        ready;

input         scl_i;
output        scl_o;
output        scl_t;
input         sda_i;
output        sda_o;
output        sda_t;

logic [6:0]   cmd_address;
logic         cmd_start;
logic         cmd_read;
logic         cmd_write;
logic         cmd_write_multiple;
logic         cmd_stop;
logic         cmd_valid;
logic         cmd_ready;

logic [7:0]   data_in;
logic         data_in_valid;
logic         data_in_ready;
logic         data_in_last;

logic [7:0]   data_out;
logic         data_out_valid;
logic         data_out_ready;
logic         data_out_last;

logic [4:0]   state, state_next;
logic         busy, missed_ack;

logic [6:0]   cmd_reg, cmd_next;
logic [7:0]   data0_reg, data0_next, data1_reg, data1_next;

logic         cmd_ack_reg, cmd_ack_next;

logic [6:0]   filter_select_reg, filter_select_next;
logic         rx_antenna_reg, rx_antenna_next;

logic         en_i2c2_next;

logic [31:0]  resp_data_next, resp_data=32'h00000000;

`ifdef TLV320AIC
  // Mic boost two-step helper flags for Select Page prewrite
  logic tlv_mb_step_reg, tlv_mb_step_next;
  logic tlv_mb_val_reg,  tlv_mb_val_next;
  // Speaker on/off two-step helper flags for Select Page prewrite
  logic tlv_sp_step_reg, tlv_sp_step_next;
  logic tlv_sp_val_reg,  tlv_sp_val_next;
  // Common lock for two-step I²C sequences
  logic twostep_busy_reg, twostep_busy_next;  
`endif
`ifdef AK4951
   logic aucodec_spkon_reg, aucodec_spkon_next;
   logic aucodec_micboost_reg, aucodec_micboost_next;
`endif



// Control
localparam [4:0]
  STATE_IDLE           = 5'h00,
  STATE_CMDADDR        = 5'h01,
  STATE_WRITE_DATA0    = 5'h02,
  STATE_WRITE_DATA1    = 5'h03,
  STATE_FCMDADDR       = 5'h05,
  STATE_WRITE_FDATA0   = 5'h06,
  STATE_WRITE_FDATA1   = 5'h07,
  STATE_WRITE_FDATA2   = 5'h04,
  STATE_READ_CMDADDR   = 5'h08,
  STATE_READ_DATA0     = 5'h09,
  STATE_READ_DATA1     = 5'h0a,
  STATE_READ_DATA2     = 5'h0b,
  STATE_READ_DATA3     = 5'h0c,
  STATE_READ_DATA4     = 5'h0d,
  STATE_FCMDADDR_2     = 5'h10, //I2C write 2 bytes
  STATE_WRITE_FDATA0_2 = 5'h11, //I2C write 2 bytes
  STATE_WRITE_FDATA1_2 = 5'h12; //I2C write 2 bytes
  
always @(posedge clk) begin
  if (rst) begin
    state <= STATE_IDLE;
    cmd_reg <= 'h0;
    data0_reg <= 'h0;
    data1_reg <= 'h0;
    cmd_ack_reg <= 1'b0;
    filter_select_reg <= 'h0;
    rx_antenna_reg <= 1'b0;
`ifdef TLV320AIC
    tlv_mb_step_reg <= 1'b0;
    tlv_mb_val_reg  <= 1'b0;
    tlv_sp_step_reg <= 1'b0;
    tlv_sp_val_reg  <= 1'b0;
	 twostep_busy_reg <= 1'b0;
`endif
`ifdef AK4951
    aucodec_spkon_reg <= 1'b0;
    aucodec_micboost_reg <= 1'b0;
`endif

  end else begin
    state <= state_next;
    cmd_reg <= cmd_next;
    data0_reg <= data0_next;
    data1_reg <= data1_next;
    cmd_ack_reg <= cmd_ack_next;
    filter_select_reg <= filter_select_next;
    rx_antenna_reg <= rx_antenna_next;
`ifdef TLV320AIC
    tlv_mb_step_reg <= tlv_mb_step_next;
    tlv_mb_val_reg  <= tlv_mb_val_next;
    tlv_sp_step_reg <= tlv_sp_step_next;
    tlv_sp_val_reg  <= tlv_sp_val_next;
	 twostep_busy_reg <= twostep_busy_next;
`endif
`ifdef AK4951
    aucodec_spkon_reg <= aucodec_spkon_next;
    aucodec_micboost_reg <= aucodec_micboost_next;
`endif

    en_i2c2 <= en_i2c2_next;
  end
  resp_data <= resp_data_next;
end

assign cmd_address = cmd_reg;
assign cmd_start = 1'b0;
assign cmd_write_multiple = 1'b0;

assign data_in_last = 1'b1;

assign data_out_ready = 1'b1;

assign cmd_ack = cmd_ack_reg;

assign cmd_resp_data = resp_data;


always @* begin
  state_next = state;
  resp_data_next = resp_data;
  cmd_ack_next = cmd_ack;
  filter_select_next = filter_select_reg;
  rx_antenna_next = rx_antenna_reg;
`ifdef TLV320AIC
  tlv_mb_step_next = tlv_mb_step_reg;
  tlv_mb_val_next  = tlv_mb_val_reg;
  tlv_sp_step_next = tlv_sp_step_reg;
  tlv_sp_val_next  = tlv_sp_val_reg;
  twostep_busy_next = twostep_busy_reg;
`endif
`ifdef AK4951
  aucodec_spkon_next = aucodec_spkon_reg;
  aucodec_micboost_next = aucodec_micboost_reg;
`endif

  cmd_next = cmd_reg;
  data0_next = data0_reg;
  data1_next = data1_reg;
  en_i2c2_next = en_i2c2;


  cmd_valid = 1'b1;
  cmd_write = 1'b0;
  cmd_read  = 1'b0;
  cmd_stop = 1'b0;
  read_done = 1'b0;

  data_in = data0_reg;
  data_in_valid = 1'b0;

  ready = 1'b0;

  case(state)

    STATE_IDLE: begin
      cmd_valid = 1'b0;
      cmd_ack_next = 1'b1;
      ready = ~busy;
      if (cmd_rqst) begin
        if (((cmd_addr == 6'h3d) | (cmd_addr == 6'h3c)) & (cmd_data[31:25] == 7'h03)) begin
          // Must send
          if (~busy) begin
            cmd_next = cmd_data[22:16];
            data0_next  = cmd_data[15:8];
            data1_next = cmd_data[7:0];
            en_i2c2_next = (cmd_addr == 6'h3d);
            state_next = cmd_data[24] ? STATE_READ_CMDADDR : STATE_CMDADDR;
          end else begin
            cmd_ack_next = 1'b0; // Missed
          end
        end

        // Filter select update
        if (cmd_addr == 6'h00) begin
          if ((cmd_data[23:17] != filter_select_reg) | (cmd_data[13] != rx_antenna_reg)) begin
            // Must send
            if (~busy) begin
              filter_select_next = cmd_data[23:17];
              rx_antenna_next = cmd_data[13];
              cmd_next = 'h20;
              data0_next = 'h0a;
              // Alex rx antenna option passed to GP7 on MCP23008 GP7
              data1_next = {cmd_data[13],cmd_data[23:17]};
              en_i2c2_next = 1'b1;
              state_next = STATE_FCMDADDR;
            end else begin
              cmd_ack_next = 1'b0; // Missed
            end
          end


// SPEAKER ON/OFF 
`ifdef TLV320AIC
    // TLV320AIC3204 speaker on/off setting update with mandatory Page 1 pre-write (WiSER - SQUARE SDR 2)
    // Two-step sequence:
    //  (1) Write 0x00 <- 0x00 (select Page 0)
    //  (2) Write 0x34 <- 0x0D (on) or 0x0C (off)
    // Uses tlv_sp_step_reg to chain step (1) -> (2) across STATE_IDLE.
		if (((cmd_data[11] != aucodec_spkon_reg) && ~twostep_busy_reg) || tlv_sp_step_reg) begin
      if (~busy) begin
        if (tlv_sp_step_reg == 1'b0) begin
          // Step 1: Page select to Page 0
          twostep_busy_next = 1'b1;   // two-step sequence started
          tlv_sp_step_next = 1'b1;
          tlv_sp_val_next  = cmd_data[11];
          cmd_next     = 'h18;
          data0_next   = 'h00;
          data1_next   = 'h00;
          en_i2c2_next = 1'b0;
          state_next   = STATE_FCMDADDR_2; // execute Reg 0x00 write
        end else begin
          // Step 2: GPIO/MFP5 register write on Page 0 (Reg 0x34)
          tlv_sp_step_next     = 1'b0;
          aucodec_spkon_next = tlv_sp_val_reg;
          cmd_next     = 'h18;
          data0_next   = 'h34;
          data1_next   = tlv_sp_val_reg ? 8'h0d : 8'h0c; // on : off
          en_i2c2_next = 1'b0;
          twostep_busy_next = 1'b0;   // release after completing the sequence
          state_next   = STATE_FCMDADDR_2; // execute Reg 0x34 write
        end
      end else begin
        cmd_ack_next = 1'b0; // Missed
      end
    end
`else
	`ifdef AK4951
			 // AK4951 speaker on/off setting update
			 if (cmd_data[11] != aucodec_spkon_reg) begin
				// Must send
				if (~busy) begin
				  aucodec_spkon_next = cmd_data[11]; // reuse Dither
				  cmd_next = 'h12;
				  data0_next = 'h02;
				  data1_next = 8'h2e | (cmd_data[11]? 8'h80 : 8'h00);
				  en_i2c2_next = 1'b0;
				  state_next = STATE_FCMDADDR;
				end else begin
				  cmd_ack_next = 1'b0; // Missed
				end
			 end
	`endif 
`endif          
        end


		  
// MIC BOOST ON/OFF 
`ifdef TLV320AIC
    // TLV320AIC3204 mic boost setting update with mandatory Page 1 pre-write (WiSER - SQUARE SDR 2)
    // Two-step sequence:
    //  (1) Write 0x00 <- 0x01 (select Page 1)
    //  (2) Write 0x3B <- 0x28 (20dB) or 0xA8 (0dB)
    // Uses tlv_mb_step_reg to chain step (1) -> (2) across STATE_IDLE.
    if (cmd_addr == 6'h09) begin
		if (((cmd_data[16] != aucodec_micboost_reg) && ~twostep_busy_reg) || tlv_mb_step_reg) begin
        if (~busy) begin
          if (tlv_mb_step_reg == 1'b0) begin
            // Step 1: Page select to Page 1
            twostep_busy_next = 1'b1;   // two-step sequence started
            tlv_mb_step_next = 1'b1;
            tlv_mb_val_next  = cmd_data[16];
            cmd_next     = 'h18;
            data0_next   = 'h00;
            data1_next   = 'h01;
            en_i2c2_next = 1'b0;
            state_next   = STATE_FCMDADDR_2; // execute Reg 0x00 write
          end else begin
            // Step 2: Mic boost register write on Page 1 (Reg 0x3B)
            tlv_mb_step_next     = 1'b0;
            aucodec_micboost_next = tlv_mb_val_reg;
            cmd_next     = 'h18;
            data0_next   = 'h3b;
            data1_next   = tlv_mb_val_reg ? 8'h28 : 8'ha8; // 20dB : 0dB
            en_i2c2_next = 1'b0;
				twostep_busy_next = 1'b0;   // release after completing the sequence
            state_next   = STATE_FCMDADDR_2; // execute Reg 0x3B write
          end
        end else begin
          cmd_ack_next = 1'b0; // Missed
        end
      end
    end
`else
	`ifdef AK4951
			 // AK4951 mic boost setting update
			 if (cmd_addr == 6'h09) begin
			  if (cmd_data[16] != aucodec_micboost_reg) begin
				 // Must send
				 if (~busy) begin
				   aucodec_micboost_next = cmd_data[16];
				   cmd_next = 'h12;
				   data0_next = 'h0d;
				   data1_next = cmd_data[16]? 8'hc7: 8'h91; // 20.25dB : 0dB
				   en_i2c2_next = 1'b0;
				   state_next = STATE_FCMDADDR;
				 end else begin
				   cmd_ack_next = 1'b0; // Missed
			  	 end
			  end
  			 end
	`endif
`endif          
      end
    end

    STATE_CMDADDR: begin
      cmd_write = 1'b1;
      if (cmd_ready) state_next = STATE_WRITE_DATA0;
    end

    STATE_WRITE_DATA0: begin
      cmd_write = 1'b1;
      data_in_valid = 1'b1;
      data_in = data0_reg;
      if (data_in_ready) state_next = STATE_WRITE_DATA1;
    end

    STATE_WRITE_DATA1: begin
      cmd_write = 1'b1;
      data_in_valid = 1'b1;
      data_in = data1_reg;
      if (data_in_ready) state_next = STATE_IDLE;
    end

    STATE_READ_CMDADDR: begin
      cmd_ack_next = 1'b0; // Hold ack low until read data is ready
      cmd_write = 1'b1;
      cmd_stop = 1'b1;
      if (cmd_ready) state_next = STATE_READ_DATA0;
    end

    STATE_READ_DATA0: begin
      cmd_read = 1'b1;
      data_in_valid = 1'b1;
      data_in = data0_reg;
      if (data_in_ready) state_next = STATE_READ_DATA1;
    end

    STATE_READ_DATA1: begin
      cmd_read = 1'b1;
      resp_data_next = {resp_data[31:8],data_out};
      if (data_out_valid) state_next = STATE_READ_DATA2;
    end

    STATE_READ_DATA2: begin
      cmd_read = 1'b1;
      resp_data_next = {resp_data[31:16],data_out,resp_data[7:0]};
      if (data_out_valid) state_next = STATE_READ_DATA3;
    end

    STATE_READ_DATA3: begin
      cmd_read = 1'b1;
      resp_data_next = {resp_data[31:24],data_out,resp_data[15:0]};
      if (data_out_valid) state_next = STATE_READ_DATA4;
    end

    STATE_READ_DATA4: begin
      cmd_stop = 1'b1;
      cmd_read = 1'b1;
      resp_data_next = {data_out,resp_data[23:0]};
      if (data_out_valid) begin
        read_done = 1'b1;
        state_next = STATE_IDLE;
      end
    end

    STATE_FCMDADDR: begin
      cmd_write = 1'b1;
      if (cmd_ready) state_next = STATE_WRITE_FDATA0;
    end

    STATE_WRITE_FDATA0: begin
      cmd_write = 1'b1;
      data_in_valid = 1'b1;
      data_in = data0_reg;
      if (data_in_ready) state_next = STATE_WRITE_FDATA1;
    end

    STATE_WRITE_FDATA1: begin
      cmd_write = 1'b1;
      data_in_valid = 1'b1;
      data_in = data1_reg;
      if (data_in_ready) state_next = STATE_WRITE_FDATA2;
    end

    STATE_WRITE_FDATA2: begin
      cmd_write = 1'b1;
      data_in_valid = 1'b1;
      data_in = 'h00;
      if (data_in_ready) state_next = STATE_IDLE;
    end

    STATE_FCMDADDR_2: begin
      cmd_write = 1'b1;
      if (cmd_ready) state_next = STATE_WRITE_FDATA0_2;
    end

    STATE_WRITE_FDATA0_2: begin
      cmd_write = 1'b1;
      data_in_valid = 1'b1;
      data_in = data0_reg;
      if (data_in_ready) state_next = STATE_WRITE_FDATA1_2;
    end

    STATE_WRITE_FDATA1_2: begin
      cmd_write = 1'b1;
      data_in_valid = 1'b1;
      data_in = data1_reg;
      if (data_in_ready) state_next = STATE_IDLE;
    end
	 
	 endcase
end


i2c_master i2c_master_i (
  .clk(clk),
  .rst(rst),
  /*
   * Host interface
   */
  .cmd_address(cmd_address),
  .cmd_start(cmd_start),
  .cmd_read(cmd_read),
  .cmd_write(cmd_write),
  .cmd_write_multiple(cmd_write_multiple),
  .cmd_stop(cmd_stop),
  .cmd_valid(cmd_valid),
  .cmd_ready(cmd_ready),

  .data_in(data_in),
  .data_in_valid(data_in_valid),
  .data_in_ready(data_in_ready),
  .data_in_last(data_in_last),

  .data_out(data_out),
  .data_out_valid(data_out_valid),
  .data_out_ready(data_out_ready),
  .data_out_last(data_out_last),

  // I2C
  .scl_i(scl_i),
  .scl_o(scl_o),
  .scl_t(scl_t),
  .sda_i(sda_i),
  .sda_o(sda_o),
  .sda_t(sda_t),

  // Status
  .busy(busy),
  .bus_control(),
  .bus_active(),
  .missed_ack(missed_ack),

  // Configuration
  .prescale(16'h0002),
  .stop_on_idle(1'b1)
);

endmodule

