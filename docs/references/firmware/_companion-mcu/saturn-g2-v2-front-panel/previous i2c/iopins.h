/////////////////////////////////////////////////////////////////////////
//
// Saturn G2 front panel controller sketch by Laurence Barker G8NJJ
// this sketch provides a knob and switch interface through I2C
// copyright (c) Laurence Barker G8NJJ 2023
//
// the code is written for an Arduino Nano Every module
// iopins.h
//
/////////////////////////////////////////////////////////////////////////

#ifndef __IOPINS_H
#define __IOPINS_H

#define VPINVFOENCODERA 0         // VFO encoder (NOTE the encoder code reads these direct so if changed, that code will need to be changed too!)
#define VPINVFOENCODERB 1

#define VPINENCODER9A 2           // encoder 9 (5 upper)
#define VPINENCODER9B 3
#define VPINENCODER10A 4          // encoder 10 (5 lower)
#define VPINENCODER10B 5


#define VPININDICATOR5 6
#define VPININDICATOR6 7
#define VPININDICATOR7 8
#define VPININDICATOR8 9
#define VPININDICATOR9 10
#define VPININDICATOR10 A0
#define VPININDICATOR11 A1


#define VPINPIINTERRUPT A7      // active high interrupt out to Raspberry pi
#define VPINMCPCS0 A2           // chip select for MCP23S17 0
#define VPINMCPCS1 A3           // chip select for MCP23S17 0

#define VPINBLINKLED A6

#endif //not defined
