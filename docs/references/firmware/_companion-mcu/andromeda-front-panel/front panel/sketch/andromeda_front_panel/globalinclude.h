/////////////////////////////////////////////////////////////////////////
//
// Andromeda front panel controller by Laurence Barker G8NJJ
// this sketch provides a knob and switch interface through USB and CAT
// copyright (c) Laurence Barker G8NJJ 2019
//
// globalinclude.h
// this file holds #defines for conditional compilation
/////////////////////////////////////////////////////////////////////////
#ifndef __globalinclude_h
#define __globalinclude_h

//
// define V2HARDWARE to compile for Kjell's Andromeda early prototype PCB
// design proving initally using Odin software
//
#define V2HARDWARE 1

//
// define V3HARDWARE to compile for Kjell's final Andromeda prototype PCB
// design proving initally using Odin software
//
//#define V3HARDWARE 1

//
// software version: send back to console on request
//
#define SWVERSION 4

//
// product iD: send back to console on request
// 1=Andromeda front panel
// 2 = Aries ATU
//
#define PRODUCTID 1

#endif      // file sentry
