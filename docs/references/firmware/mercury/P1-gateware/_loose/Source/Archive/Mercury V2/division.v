//test code for division

// 64 bit version 

module division(quotient,ready,dividend,divider,clk);
   
   input [31:0]  dividend; // frequency 
   input [31:0]  divider;  // 125MHz
   input         clk;
   output [31:0] quotient;
   output        ready;

   reg [95:0]    qr;
   reg [33:0]    diff;
   reg [31:0]    quotient;


   always @ (negedge clk)
   begin
		if (ready)					// get result when ready 
			quotient <= qr[31:0];
   		else 
			quotient <= quotient;
	end

   reg [7:0]     bits; 
   wire          ready = !bits;
   
   always @( posedge clk ) 

     if( ready) begin

        bits = 64;
        qr = {32'd0,dividend, 32'd0}; // multiply freq by 2^32

     end else begin

        diff = qr[95:63] - {1'b0,divider};

        if( diff[32] )
          qr = {qr[94:0],1'd0};      // negative so use qr shifted left, result = 0
        else
          qr = {diff[31:0],qr[62:0],1'd1}; // positive so use  diff shifted left, result = 1
        
        bits = bits - 1'b1;

     end

endmodule

