SP_fifo	SP_fifo_inst (
	.clock ( clock_sig ),
	.data ( data_sig ),
	.rdreq ( rdreq_sig ),
	.sclr ( sclr_sig ),
	.wrreq ( wrreq_sig ),
	.empty ( empty_sig ),
	.full ( full_sig ),
	.q ( q_sig )
	);
