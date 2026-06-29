/////////////////////////////////////////////////////////////////////////
//
// Andromeda front panel controller by Laurence Barker G8NJJ
// this sketch provides a knob and switch interface through USB and CAT
// copyright (c) Laurence Barker G8NJJ 2019
//
// types.h
//
/////////////////////////////////////////////////////////////////////////

#ifndef __TYPES_H
#define __TYPES_H

//
// define the numbers of controls available
//
#ifdef V3HARDWARE                   // Andromeda 2nd prototype
#define VMAXINDICATORS 12
#define VMAXENCODERS 10             // configurable, not including VFO
#define VMAXGPIOBUTTONS 20          // attached to I/O pins
#define VMAXBUTTONS 36

#elif defined V2HARDWARE
#define VMAXINDICATORS 9
#define VMAXENCODERS 14             // configurable, not including VFO
#define VMAXGPIOBUTTONS 18          // attached to I/O pins
#define VMAXBUTTONS 34
#endif


#endif //not defined
