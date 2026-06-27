10/24/2024 - N1GP

This is firmware meant for a Hermes board to be used as a 10 DDC RX Skimmer.
The frequency range is up to 30MHz and is fixed at 192ksps. This is due to using
an NCO -vs- Cordic and only using CIC filters in the DSP. This uses less FPGA
resources and allow many more receivers. This was inspired by the Hermes-Lite2
folks as they put together a 10 receiver slice firmware for use with skimming.
That would include clients such as:

CW Skimmer / Skimmer Server:
	https://www.dxatlas.com/CwSkimmer
	https://www.dxatlas.com/SkimServer
	https://www.dxatlas.com/RttySkimServ


SparkSDR:
	https://www.sparksdr.com

This firmware provides up to 10 receiver slices (or DDCs) @ 192ksps. It is Protocol 2
based.
