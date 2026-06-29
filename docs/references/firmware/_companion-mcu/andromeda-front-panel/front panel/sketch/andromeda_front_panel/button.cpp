/////////////////////////////////////////////////////////////////////////
//
// Andromeda front panel controller by Laurence Barker G8NJJ
// this sketch provides a knob and switch interface through USB and CAT
// copyright (c) Laurence Barker G8NJJ 2019
//
// button.c
// this file holds the code to debounce pushbutton inputs and the external MOX input
/////////////////////////////////////////////////////////////////////////

#include "globalinclude.h"
#include "types.h"
#include "button.h"
#include "iopins.h"
#include "cathandler.h"
#include "configdata.h"
#include "encoders.h"
#include "Wire.h"                             // for Andromeda h/w MCP23017

#define V23017INPUTS 16

//
// note switch and encoder numbering for original Odin:
// in the software switches are numbered 0-20, and encoders 0-7. The VFO encoder is treated separately.
// these correspond to the control of Kjell's PCB as follows:
//
// switch numbering:
//    PCB   software
//    SW1                 0
//    SW2                 1
//    SW3                 2
//    SW4                 3
//    SW5                 4
//    SW6                 5
//    SW7                 6
//    SW8                 7
//    SW9                 8
//    SW10                9
//    SW11               10
//    SW12               11
//    SW13               12
//    SW14               13
//    SW15               14
//    SW16               15
//    SW17               16
//    encoder 2 push     17
//    encoder 3 push     18
//    encoder 4 push     19
//    encoder 5 push     20
//
// switch 21 is the external MOX input

// V2 hardware:
// DIG54 = GPIOB 54
// DIG69 = GPIOA 69

bool GIsEvenTick = false;                           // toggles between "even" and "odd"
bool GButtonPressed[VMAXBUTTONS+1];                 // true when button debounced as pressed

//
// I/O pins for each button
// 1-16 are pushbuttons 1-16; 17-23 are the 7 "normal" encoder clicks
// (and on Kjell's PCB, 3 of those are used for normal pushbuttons)
//
#ifdef V3HARDWARE                                   // Andromeda 2nd prototype hardware
byte GButtonPinNumbers[VMAXGPIOBUTTONS] =
{
  VPINBUTTON1,
  VPINBUTTON2,
  VPINBUTTON3,
  VPINBUTTON4,
  VPINBUTTON5,
  VPINBUTTON6,
  VPINBUTTON7,
  VPINBUTTON8,
  VPINBUTTON9,
  VPINBUTTON10,
  VPINBUTTON11,
  VPINBUTTON12,
  VPINBUTTON13,
  VPINBUTTON14,
  VPINBUTTON15,
  VPINBUTTON16,
  VPINBUTTON17,
  VPINBUTTON18,
  VPINBUTTON19,
  VPINBUTTON20
};

#elif defined V2HARDWARE
byte GButtonPinNumbers[VMAXGPIOBUTTONS] =
{
  VPINBUTTON1,
  VPINBUTTON2,
  VPINBUTTON3,
  VPINBUTTON4,
  VPINBUTTON5,
  VPINBUTTON6,
  VPINBUTTON7,
  VPINBUTTON8,
  VPINBUTTON9,
  VPINBUTTON10,
  VPINBUTTON11,
  VPINBUTTON12,
  VPINBUTTON13,
  VPINBUTTON14,
  VPINBUTTON15,
  VPINBUTTON16,
  VPINBUTTON17,
  VPINBUTTON18
};
#endif

//
// array of debounce states
// these are simply the last 8 samples of the pin, starting in the LSB
//
byte GInputDebounce[VMAXBUTTONS];
byte GInput23017Debounce[V23017INPUTS];


//
// initialise
// simply set all the debounce inputs to 0xFF (button released)
//
void GButtonInitialise(void)
{
  int Cntr;

  for (Cntr=0; Cntr < VMAXGPIOBUTTONS; Cntr++)
    GInputDebounce[Cntr] = 0xFF;
  for (Cntr=0; Cntr < V23017INPUTS; Cntr++)
    GInput23017Debounce[Cntr] = 0xFF;
  Wire.begin();
}

//
// arrays to look up the report code from the software scan code
// s/w scan code begins 0
// reported code begins 1-7 (encoders) then 21-49 (pushbuttons)
//
#ifdef V3HARDWARE
int ReportCodeLookup[] = 
{
  11,      // scan code 0
  21,
  22,
  23,
  24,
  25,
  1, 
  26, 
  5,
  7, 
  29,     // scan code 10
  30,
  31,
  32,
  33,
  34,
  27,
  3,
  35,
  36,
  37,     // scan code 20
  38,
  39,
  40,
  41,
  42,
  43,
  9,
  44,
  45,
  46,     // scan code 30
  47, 
  48,
  0,
  49
};

#elif defined V2HARDWARE
int ReportCodeLookup[] = 
{
  29,     // scan code 0
  21,
  22,
  23,
  24,
  25,
  1,
  30,
  5,
  7,
  31,     // scan code 10
  32,
  33,
  34,
  35,
  36,
  37,
  3,
  38,
  39,
  40,     // scan code 20
  41,
  42,
  43,
  44,
  9,
  11,
  13,
  45,
  46,
  26,     // scan code 30
  27,
  28,
  47
};

#endif


//
// called when a button is pressed
// the parameter is the button number (0..N-1)
//
void ButtonPressed(int ButtonNum)
{
  int ButtonCode;             // message report code
  
  GButtonPressed[ButtonNum] = true;
  ButtonCode = ReportCodeLookup[ButtonNum];
  if (ButtonCode != 0)
    CATHandlePushbutton(ButtonCode, true, false); 
}



//
// called when a button is released
// the parameter is the button number (0..N-1)
//
void ButtonReleased(int ButtonNum)
{
  int ButtonCode;             // message report code
  
  GButtonPressed[ButtonNum] = false;
  ButtonCode = ReportCodeLookup[ButtonNum];
  if (ButtonCode != 0)
    CATHandlePushbutton(ButtonCode, false, false); 
}


#define VEDGEMASK 0b00000111                                  // 3 most recent samples
#define VPRESSPATTERN 0b00000100                              // 2 consecutive presses
#define VRELEASEPATTERN 0b00000011                            // 2 consecutive releases

//
// Tick
// read each pin into the LSB of its debounce register; then look for press or release pattern
// only accept a "pressed" pattern if button is currently released and vice versa
void ButtonTick(void)
{
  int Cntr;
  byte Input;                                   // becomes the new bit sequence for an input
  byte Input2;
  unsigned int Input23017;                      // 16 bit value
//
// poll hardwired inputs every 2nd clock
//
  GIsEvenTick = !GIsEvenTick;
  if (GIsEvenTick)
  {
    for(Cntr=0; Cntr < VMAXGPIOBUTTONS; Cntr++)
    {
      Input = GInputDebounce[Cntr] << 1;          // move it left
      if (digitalRead(GButtonPinNumbers[Cntr]) == HIGH)
        Input |= 1;                               // set bottom bit if input was high
      GInputDebounce[Cntr] = Input;               // write it back
      if (!GButtonPressed[Cntr])
      {
        if ((Input & VEDGEMASK) == VPRESSPATTERN)
          ButtonPressed(Cntr);
      }
      else      // button is already pressed
      {
        if ((Input & VEDGEMASK) == VRELEASEPATTERN)
          ButtonReleased(Cntr);
      }
    }
  }
  else                                                    // reserved to poll V2 hardware extra inputs
  {
    Wire.beginTransmission(0x20);
    Wire.write(0x12);                                     // point to GPIOA
    Wire.endTransmission();
    Wire.requestFrom(0x20, 2);                            // read 2 bytes
    Input=Wire.read();                                    // GPIOA
    Input2 = Wire.read();                                 // GPIOB
    Input23017 = (Input << 8) | Input2;                   // bit0 = dig54
//
// now process the 16 bits, one at a time
//
    for(Cntr=0; Cntr < V23017INPUTS; Cntr++)
    {
      Input = GInputDebounce[Cntr + VMAXGPIOBUTTONS] << 1;          // move it left
      if ((Input23017 & 1) == HIGH)
        Input |= 1;                               // set bottom bit if input was high
      GInputDebounce[Cntr + VMAXGPIOBUTTONS] = Input;               // write it back
      Input23017 = Input23017 >> 1;
      if (!GButtonPressed[Cntr + VMAXGPIOBUTTONS])
      {
        if ((Input & VEDGEMASK) == VPRESSPATTERN)
          ButtonPressed(Cntr + VMAXGPIOBUTTONS);
      }
      else      // button is already pressed
      {
        if ((Input & VEDGEMASK) == VRELEASEPATTERN)
          ButtonReleased(Cntr + VMAXGPIOBUTTONS);
      }
    }
  }
}
