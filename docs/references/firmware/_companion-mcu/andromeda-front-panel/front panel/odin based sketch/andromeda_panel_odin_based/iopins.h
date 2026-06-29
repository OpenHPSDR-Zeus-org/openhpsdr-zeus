/////////////////////////////////////////////////////////////////////////
//
// Odin SDR Console by Laurence Barker G8NJJ
//
// this sketch provides a knob and switch interface through USB and CAT
//
// iopins.h
//
/////////////////////////////////////////////////////////////////////////

#ifndef __IOPINS_H
#define __IOPINS_H

#ifdef V3HARDWARE                       // Andromeda 2nd prototype
#define VPINVFOENCODERA 2
#define VPINVFOENCODERB 3

#define VPINENCODER1A 5           // encoder 2A
#define VPINENCODER1B 4

#define VPINENCODER2A 8           // encoder 2B on PCB
#define VPINENCODER2B 7

#define VPINENCODER3A 11          // encoder 3A
#define VPINENCODER3B 10

#define VPINENCODER4A 15          // encoder 3B
#define VPINENCODER4B 14

#define VPINENCODER5A 22          // encoder 4A
#define VPINENCODER5B 17

#define VPINENCODER6A 25          // encoder 4B
#define VPINENCODER6B 24

#define VPINENCODER7A 47          //encoder 6A
#define VPINENCODER7B 46

#define VPINENCODER8A 39          //encoder 6B
#define VPINENCODER8B 12

#define VPINENCODER9A 28          //encoder 5A
#define VPINENCODER9B 27

#define VPINENCODER10A 49         //encoder 7A
#define VPINENCODER10B 48

#define VPINBUTTON1 50
#define VPINBUTTON2 A3
#define VPINBUTTON3 A4
#define VPINBUTTON4 A5
#define VPINBUTTON5 A6
#define VPINBUTTON6 A7
#define VPINBUTTON7 6
#define VPINBUTTON8 9
#define VPINBUTTON9 23
#define VPINBUTTON10 29
#define VPINBUTTON11 40
#define VPINBUTTON12 41
#define VPINBUTTON13 42
#define VPINBUTTON14 43
#define VPINBUTTON15 44
#define VPINBUTTON16 45
#define VPINBUTTON17 52
#define VPINBUTTON18 53
#define VPINBUTTON19 A6
#define VPINBUTTON20 A5
//
// (BUTTONS 21-36 INTERFACED VIA I2C, of which one not used)
//
#define VPININDICATOR1 34
#define VPININDICATOR2 37
#define VPININDICATOR3 A1
#define VPININDICATOR4 32
#define VPININDICATOR5 33
#define VPININDICATOR6 30
#define VPININDICATOR7 A0
#define VPININDICATOR8 38
#define VPININDICATOR9 31
#define VPININDICATOR10 51
#define VPININDICATOR11 18
#define VPININDICATOR12 36



#elif defined V2HARDWARE                       // Andromeda 1st prototype
#define VPINVFOENCODERA 2
#define VPINVFOENCODERB 3

#define VPINENCODER1A 5           // encoder 2A
#define VPINENCODER1B 4

#define VPINENCODER2A 8           // encoder 2B on PCB
#define VPINENCODER2B 7

#define VPINENCODER3A 11          // encoder 3A
#define VPINENCODER3B 10

#define VPINENCODER4A 15          // encoder 3B
#define VPINENCODER4B 14

#define VPINENCODER5A 22          // encoder 4A
#define VPINENCODER5B 17

#define VPINENCODER6A 25          // encoder 4B
#define VPINENCODER6B 24

#define VPINENCODER7A 28          //encoder 5A
#define VPINENCODER7B 27

#define VPINENCODER8A 26          //encoder 5B
#define VPINENCODER8B 16

#define VPINENCODER9A 47          //encoder 6A
#define VPINENCODER9B 46

#define VPINENCODER10A 39         //encoder 6B
#define VPINENCODER10B 12

#define VPINENCODER11A 49         //encoder 7A
#define VPINENCODER11B 48

#define VPINENCODER12A 19         //encoder 7B
#define VPINENCODER12B 18

#define VPINENCODER13A 51         //encoder 8A
#define VPINENCODER13B 50

#define VPINENCODER14A 36         //encoder 8B
#define VPINENCODER14B 35

#define VPINBUTTON1 A2
#define VPINBUTTON2 A3
#define VPINBUTTON3 A4
#define VPINBUTTON4 A5
#define VPINBUTTON5 A6
#define VPINBUTTON6 A7
#define VPINBUTTON7 6
#define VPINBUTTON8 9
#define VPINBUTTON9 23
#define VPINBUTTON10 29
#define VPINBUTTON11 40
#define VPINBUTTON12 41
#define VPINBUTTON13 42
#define VPINBUTTON14 43
#define VPINBUTTON15 44
#define VPINBUTTON16 45
#define VPINBUTTON17 52
#define VPINBUTTON18 53
//
// (BUTTONS 19-33 INTERFACED VIA I2C)
//
#define VPININDICATOR1 34
#define VPININDICATOR2 37
#define VPININDICATOR3 A1
#define VPININDICATOR4 32
#define VPININDICATOR5 33
#define VPININDICATOR6 30
#define VPININDICATOR7 A0
#define VPININDICATOR8 38
#define VPININDICATOR9 31


#else                                   // original Odin hardware
#define VPINVFOENCODERA 2
#define VPINVFOENCODERB 3

#define VPINENCODER1A 5           // encoder 2A
#define VPINENCODER1B 4
#define VPINENCODER1SW 6

#define VPINENCODER2A 8           // encoder 2B on PCB
#define VPINENCODER2B 7

#define VPINENCODER3A 11          // encoder 3A
#define VPINENCODER3B 10
#define VPINENCODER3SW 12

#define VPINENCODER4A 15          // encoder 3B
#define VPINENCODER4B 14

#define VPINENCODER5A 22          // encoder 4A
#define VPINENCODER5B 17
#define VPINENCODER5SW 23

#define VPINENCODER6A 25          // encoder 4B
#define VPINENCODER6B 24

#define VPINENCODER7A 28          //encoder 5A
#define VPINENCODER7B 27
#define VPINENCODER7SW 29

#define VPINENCODER8A 16          //encoder 5B
#define VPINENCODER8B 26

#define VPINBUTTON1 30
#define VPINBUTTON2 31
#define VPINBUTTON3 32
#define VPINBUTTON4 33
#define VPINBUTTON5 34
#define VPINBUTTON6 35
#define VPINBUTTON7 36
#define VPINBUTTON8 37
#define VPINBUTTON9 38
#define VPINBUTTON10 39
#define VPINBUTTON11 40
#define VPINBUTTON12 41
#define VPINBUTTON13 42
#define VPINBUTTON14 43
#define VPINBUTTON15 44
#define VPINBUTTON16 45
#define VPINBUTTON17 9          // SW on PCB

#define VPININDICATOR1 46
#define VPININDICATOR2 47
#define VPININDICATOR3 48
#define VPININDICATOR4 49
#define VPININDICATOR5 50
#define VPININDICATOR6 51
#define VPININDICATOR7 53

#define VPINEXTMOXIN 52
#endif


#define VPINORANGELED 13

#endif //not defined
