/////////////////////////////////////////////////////////////////////////
//
// Saturn G2V1 front panel controller sketch by Laurence Barker G8NJJ
// enables G2V1 panel to be used with prcessor upgrades denying 40pin GPIO
// this sketch provides a knob and switch interface through USB serial
// copyright (c) Laurence Barker G8NJJ 2023
//
// the code is written for a Waveshare RP2040-piZero module

//
// globalinclude.h
// this file holds #defines for conditional compilation
/////////////////////////////////////////////////////////////////////////
#ifndef __globalinclude_h
#define __globalinclude_h



//
// hardware and software version: send back to console on request
//
#define SWVERSION 1
#define HWVERSION 1

//
// product iD: send back to console on request
// 1=Andromeda front panel
// 2 = Aries ATU
// 3 = Ganymede
// 4 = G2V1 panel (p2app, and G1V1 RP2040 adapter))
// 5 = G2V2 panel
//
#define PRODUCTID 4

//
// define the numbers of controls available
//
#define VMAXENCODERS 8             // configurable, not including VFO
#define VMAXBUTTONS 20
#define HIRESOPTICALENCODER 1

//
// define the serial port used for CAT
//
#define CATSERIAL Serial                            // allows easy change to SerialUSB

#endif      // file sentry
