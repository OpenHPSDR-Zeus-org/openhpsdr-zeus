/////////////////////////////////////////////////////////////////////////
//
// Odin SDR Console by Laurence Barker G8NJJ
//
// this sketch provides a knob and switch interface through USB and CAT
//
// display.h
// this file holds the code to control a Nextion 3.2" display
// it is 400x240 pixels
/////////////////////////////////////////////////////////////////////////

#ifndef __DISPLAY_H
#define __DISPLAY_H

#include <Nextion.h>                        // uses the Nextion class library
#include <Arduino.h>
#include "types.h"


extern EDisplayPage GDisplayPage;                    // global set to current display page number


//
// strings for band and mode, NR, NB, AGC, atten
//
extern char* BandStrings[];
extern char* ModeStrings[];
extern char* NRStrings[];
extern char* NBStrings[];
extern char* AttenStrings[];
extern char* AGCStrings[];


//
// array reference for CAT strings for multifunction encoder setting
//
extern char* MultiEncoderCATStrings[];



//
// helper to redraw encoder strings
//
void RedrawEncoderStrings(void);


//
// display initialise
//
void DisplayInit(void);


//
// display tick
//
void DisplayTick(void);


//
// display encoder handler
// when in I/O test page
// VFO number 0-6 (normal) 7 (VFO)
//
void DisplayEncoderHandler(unsigned int Encoder, int Count);

//
// display button handler
// when in I/O test page
// button = 0-22; 
//
void DisplayButtonHandler(unsigned int Button, bool IsPressed);

//
// display external MOS input hasndler
// when in I/O test psage, light indicator if pressed
//
void DisplayExtMoxHandler(bool IsPressed);

/////////////////////////////////////////////////////////////////////////////////////
//
// handlers for CAT messages to save data for display
//
void DisplayShowABState(bool IsA);
void DisplayShowTXState(bool IsTX, bool IsTune);
void DisplayShowRITState(bool IsRIT);
void DisplayShowFrequency(char* Frequency);         // string with MHz as ASCII
void DisplayShowSMeter(unsigned int Reading);
void DisplayShowTXPower(unsigned int Reading);
void DisplayShowBand(EBand Band);
void DisplayShowMode(EMode Mode);
void DisplayShowNRState(ENRState State);
void DisplayShowNBState(ENBState State);
void DisplayShowSNBState(bool SNBState);
void DisplayShowANFState(bool ANFState);
void DisplayShowAGCSpeed(EAGCSpeed Speed);
void DisplayShowAGCThreshold(int Threshold);
void DisplayShowLockState(bool IsLock);
void DisplayShowSplit(bool IsSplit);
void DisplayShowAtten(EAtten Attenuation);
void DisplayShowFilterLow(int Freq);
void DisplayShowFilterHigh(int Freq);


//
// set display of encoder actions. Currently we have placeholders to show 0,2,4 & 6
// this is only called for a multifunction encoder
//
void DisplaySetEncoderAction(unsigned int EncoderNumber, EEncoderActions Action, bool IsMulti);         // set encoder string to current action

//
// note for legend display that an encoder has been turned. Used to determine which encoder should be displayed.
//
void DisplayEncoderTurned(unsigned int EncoderNumber);


#endif //#ifndef
