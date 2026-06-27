/////////////////////////////////////////////////////////////////////////
//
// Saturn G2V1 front panel controller sketch by Laurence Barker G8NJJ
// enables G2V1 panel to be used with prcessor upgrades denying 40pin GPIO
// this sketch provides a knob and switch interface through USB serial
// copyright (c) Laurence Barker G8NJJ 2023
//
// the code is written for a Waveshare RP2040-piZero module

// iopins.h
//
/////////////////////////////////////////////////////////////////////////

#ifndef __IOPINS_H
#define __IOPINS_H


#define VPINVFOENCODERA 18         // VFO encoder 
#define VPINVFOENCODERB 17

#define VPINENCODER1A 20        // encoder 1 (upper, bottom left)
#define VPINENCODER1B 26
#define VPINENCODER2A 6         // encoder 2 (lower, bottom left)
#define VPINENCODER2B 15
#define VPINENCODER3A 14        // encoder 3 (upper, top left)
#define VPINENCODER3B 21
#define VPINENCODER4A 7         // encoder 4 (lower, top left)
#define VPINENCODER4B 12
#define VPINENCODER5A 16        // encoder 5 (upper, bottom right)
#define VPINENCODER5B 19
#define VPINENCODER6A 11        // encoder 6 (lower, bottom right)
#define VPINENCODER6B 10
#define VPINENCODER7A 25        // encoder 7 (upper, top right)
#define VPINENCODER7B 8
#define VPINENCODER8A 9         // encoder 8 (lower, top right)
#define VPINENCODER8B 13

#define VPINENCODER1PB 22       // pushbutton
#define VPINENCODER2PB 27       // pushbutton
#define VPINENCODER3PB 23       // pushbutton
#define VPINENCODER4PB 24       // pushbutton

#define VPINSCL 3               // I2C
#define VPINSDA 2               // I2C

#define VPINBLINKLED 0

#endif //not defined
