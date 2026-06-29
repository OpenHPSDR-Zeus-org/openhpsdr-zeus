/////////////////////////////////////////////////////////////////////////
//
// Saturn G2 front panel controller sketch by Laurence Barker G8NJJ
// this sketch provides a knob and switch interface through I2C
// copyright (c) Laurence Barker G8NJJ 2023
//
// the code is written for an Arduino Nano Every module
//
// globalinclude.h
// this file holds #defines for conditional compilation
/////////////////////////////////////////////////////////////////////////
#ifndef __globalinclude_h
#define __globalinclude_h


//
// encoder type: for ball bearing encoder, the parameter should be defined
//
//#define HIRESOPTICALENCODER 1

//
// hardware and software version: send back to console on request
//
#define SWVERSION 7
#define HWVERSION 2

//
// product iD: send back to console on request
// 1=Andromeda front panel
// 2 = Aries ATU
// 3 = Ganymede
// 4 = G2V1 panel (no Arduino though)
// 5 = G2V2 panel
//
#define PRODUCTID 5

//
// define the numbers of controls available
//
#define VMAXINDICATORS 11
#define VMAXENCODERS 10             // configurable, not including VFO
#define VMAXBUTTONS 34


#endif      // file sentry
