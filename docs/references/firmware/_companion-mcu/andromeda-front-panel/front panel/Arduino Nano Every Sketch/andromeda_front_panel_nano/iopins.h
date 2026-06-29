/////////////////////////////////////////////////////////////////////////
//
// Andromeda front panel controller by Laurence Barker G8NJJ
// this sketch provides a knob and switch interface through USB and CAT
// copyright (c) Laurence Barker G8NJJ 2019
//
// iopins.h
//
/////////////////////////////////////////////////////////////////////////

#ifndef __IOPINS_H
#define __IOPINS_H

#define VPINVFOENCODERA 0         // VFO encoder (NOTE the encoder code reads these direct so if changed, that code will need to be changed too!)
#define VPINVFOENCODERB 1

#define VPINENCODER9A 2           // encoder 9
#define VPINENCODER9B 3

#define VPINENCODER10A 4          // encoder 10
#define VPINENCODER10B 5

#define VPINENCODER11A A2         // encoder 11
#define VPINENCODER11B A3

#define VPINENCODER12A A6         // encoder 12
#define VPINENCODER12B A7

#define VPININDICATOR1 6
#define VPININDICATOR2 7
#define VPININDICATOR3 8
#define VPININDICATOR4 11
#define VPININDICATOR5 9
#define VPININDICATOR6 12
#define VPININDICATOR7 A0
#define VPININDICATOR8 A1

#define VPINDISPLAYPWM 10


#define VPINORANGELED 13

#endif //not defined
