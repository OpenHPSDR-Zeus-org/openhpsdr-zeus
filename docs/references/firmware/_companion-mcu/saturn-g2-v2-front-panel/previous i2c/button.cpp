/////////////////////////////////////////////////////////////////////////
//
// Saturn G2 front panel controller sketch by Laurence Barker G8NJJ
// this sketch provides a knob and switch interface through I2C
// copyright (c) Laurence Barker G8NJJ 2023
//
// the code is written for an Arduino Nano Every module
//
// button.cpp
// this file holds the code to debounce pushbutton inputs scanned from a matrix
/////////////////////////////////////////////////////////////////////////

#include "globalinclude.h"
#include "button.h"
#include "iopins.h"
#include "configdata.h"
//#include "encoders.h"
#include "led.h"
#include "SPIdata.h"                             // for Andromeda h/w MCP23017
#include "eventqueue.h"


bool GBandShiftActive;                          // true if band shift is active
bool GEncoderShiftActive;                       // true if encoder shift is active
bool GShiftOverride;                            // true if shift buttons are to be treated as normal buttons

//
// enum for the states of the matrix scan sequencer
//
enum EScanStates 
{
  eIdle,                            // no button pressed
  eWaitPressed,                     // single button pressed - debounce
  eButtonPressed,                   // single button has been pressed
  eWaitReleased,                    // release state from a single press - debounce
  eMultiPressed,                    // more thna one pressed - wait until released
  eWaitMultiReleased                // debounce state for release from multiple buttons pressed
};

//
// scan codes for band and encoder shift buttons
//
#define VENCODERSHIFTSCANCODE 27
#define VBANDSHIFTSCANCODE 9
#define VENC5BUTTONCODE 13           // looked up encoder 5 button report code
#define VENC5SHIFTEDBUTTONCODE 41           // looked up encoder 5 button report code

//
// switch matrix
// the matrix has 5 column outputs driven by GPIOA(3:0)
// the remaining 3 o/p bits are LEDs: GPIOA(7:4)
// the 8 row inputs are read on GPIOB(7:0)
//
#define VNUMROWS 8
#define VNUMCOLS 4
#define VCOLUMNMASK 0b00001111;
#define VNUMSCANCODES 32
EScanStates GScanState;
byte GScanColumn;                   // scanned column number, 0...4
byte GFoundRow;                     // row where a bit detected
byte GDebounceTickCounter;          // delay counter (units of 2ms)
unsigned int GLongPressCounter;     // counter for a long press





//
// function to process row input and see if only 1 bit is set
// output 0: no bits set; 1-8: row bit 0-7 is set; FF: more than one bit set
//
//
byte AnalyseRowInput(byte RawInput)
{
  byte Result = 0;
  byte Cntr;

  for(Cntr = 0; Cntr < VNUMROWS; Cntr++)      // step through all row inputs
  {
    if ((RawInput & 1) == 0)                  // test bottom bit. If we have a zero input, something is pressed
    {
      if (Result == 0)                        // if we haven't found a bit set, set the current one
        Result = Cntr+1;
      else
        Result = 0xFF;                        // else set more than 1 pressed
    }
    RawInput = RawInput >> 1;                 // move onto mext bit
  }
  return Result;
}




//
// function to drive new column output
// this should be at the END of the code to allow settling time
// this works by having fixed GPIO data, and selectively enabling bits as outputs. 
// For column outputs this makes them like open drain so they are never driven to a 1.
//
void AssertMatrixColumn()
{
  byte Column;

  Column = 1 << GScanColumn;                              // get a 1 in the right bit position
  Column = Column & VCOLUMNMASK;                          // now have a 1 in the right bit position
  Column |= I2CLEDBits;                                   // add in LED bits at the top
  WriteMCPRegister(VMCPMATRIXADDR, IODIRA, ~Column);      // drive 0 to enable output bits to pre-defined state
}




//
// initialise
// init all scanning variables, and assert 1st column
//
void GButtonInitialise(void)
{
  GScanState = eIdle;                                 // initialise the sequencer
  GScanColumn = 0;                                    // initial column
  GFoundRow = 0;
  GDebounceTickCounter = 0;
  I2CLEDBits = 0;                                     // I2C wired LEDs off
  AssertMatrixColumn();
}


//
// array to look up the report code from the software scan code
// s/w scan code begins 0 and this table must have the full 4*8 entries
// reported code see documentation
// the array has two halves: 1st with shift NOT pressed then with shift pressed
//
int ReportCodeLookup[] = 
{
// unshifted
  4,                  // scan code 0
  5, 
  6, 
  7, 
  1, 
  2,
  3,
  0,
  8,                  // scan code 8
  39,    // band shift
  23,
  20,
  17,
  14,
  0,
  0,
  24,                 //  scan code 16
  25,
  21,
  22,
  18,
  19,
  15,
  16,
  9,                  // scan code 24
  10,
  11,
  40,      // enc shift
  12,
  13,
  0,
  0,
//shifted
  4,                  // scan code 0
  5, 
  6, 
  7, 
  1, 
  2,
  3,
  0,
  8,                  // scan code 8
  39,    // band shift
  36,
  33,
  30,
  27,
  0,
  0,
  37,                 //  scan code 16
  38,
  34,
  35,
  31,
  32,
  28,
  29,
  9,                  // scan code 24
  10,
  11,
  40,      // enc shift
  12,
  13,
  0,
  0
};



//
// get a scan code from column and row number
// returns 0xFF if an invalid result
// 0...N-1
//
byte GetScanCode()
{
  byte Result;

  if (GFoundRow == 0xFF)                            // invalid scan if more than one button pressed
    Result = 0xFF;
  else
    Result = (GScanColumn << 3) + GFoundRow - 1;
  return Result;
}


//
// function to lookup the correct button code and add event to queue
// if shift is active, use the second lookup table
// if encoder shift active, modift encoder 5 code to encoder 6 code
//
void SendButtonCode(EEventType ButtonEvent, byte ScanCode, bool Shifted)
{
  byte ButtonCode;             // message report code

  if (ScanCode != 0xFF)
  {
    if (Shifted)
      ScanCode += VNUMSCANCODES;
    ButtonCode = ReportCodeLookup[ScanCode];
    if(GEncoderShiftActive && (ButtonCode == VENC5BUTTONCODE))
      ButtonCode = VENC5SHIFTEDBUTTONCODE;
    if (ButtonCode != 0)
      AddEvent2Q(ButtonEvent, ButtonCode);      //CATHandlePushbutton(ButtonCode, true, IsLongPress); 
  }
}


//
// process an event from the button sequencer
// get scan code, then decide how to handle
//
void ProcessButtonEvent(EEventType ButtonEvent)
{
  byte ScanCode;

  ScanCode = GetScanCode();
  if (ScanCode != 0xFF)               // 0xFF implies error - more than 1 button pressed
  {
    if(GShiftOverride)                // convert to output code including shift buttons
    {
      SendButtonCode(ButtonEvent, ScanCode, false);
    }
    else if (ScanCode == VBANDSHIFTSCANCODE)  // process band shift
    {
      if(ButtonEvent == eEvButtonPress)
      {
        GBandShiftActive = !GBandShiftActive;
        if(LEDTestComplete)
          SetLED(VLEDBANDSHIFT, GBandShiftActive);
      }
    }
    else if (ScanCode == VENCODERSHIFTSCANCODE)   // process encoder shift
    {
      if(ButtonEvent == eEvButtonPress)
      {
        GEncoderShiftActive = !GEncoderShiftActive;
        if(LEDTestComplete)
          SetLED(VLEDENCODERSHIFT, GEncoderShiftActive);
      }
    }
    else                                // normal button event
    {
      SendButtonCode(ButtonEvent, ScanCode, GBandShiftActive);
    }
  }

}


#define VDEBOUNCETICKS 10
#define VLONGPRESSTHRESHOLD 1000             // 2 seconds

//
// Tick
// read row input, then run the sequencer. 
// The sequencer advances to next column only when no buttons pressed. 
// only accept a "pressed" indication if one input only is asserted
//
void ButtonTick(void)
{

  byte Row;                                 // row input read from matrix
  Row = ReadMCPRegister(VMCPMATRIXADDR, GPIOB);             // read raw row value
  Row = AnalyseRowInput(Row);               // check whether none, one or more than one button pressed


  if (GDebounceTickCounter != 0)            // if delay counter isn't 0, count down delay
    GDebounceTickCounter--;
  else                                      // else step the sequencer
  {
    switch(GScanState)
    {
      case eIdle:                           // no button pressed
        if (Row == 0)                       // still not pressed - advance column                
        {
          if (++GScanColumn >= VNUMCOLS)
            GScanColumn = 0;
        }
        else if (Row == 0xFF)               // more than one button pressed
        {
          GScanState = eMultiPressed;
          GDebounceTickCounter = VDEBOUNCETICKS;
        }
        else                                // single button pressed
        {
          GScanState = eWaitPressed;
          GFoundRow = Row;
          GDebounceTickCounter = VDEBOUNCETICKS;
        }
        break;
  
      case eWaitPressed:                    // single button pressed - debounce
        if (Row == GFoundRow)               // same button pressed
        {
          GScanState = eButtonPressed;
          ProcessButtonEvent(eEvButtonPress);                  // action the button press as a short press
          GLongPressCounter = VLONGPRESSTHRESHOLD;          // initialise count
        }
        else                                // single button pressed
        {
          GScanState = eMultiPressed;
          GDebounceTickCounter = VDEBOUNCETICKS;
        }
        break;
  
      case eButtonPressed:                  // single button has been pressed
        if (Row == 0)                       // 1st detect of button released
        {
          GScanState = eWaitReleased;
          GDebounceTickCounter = VDEBOUNCETICKS;
        }
        else if (Row != GFoundRow)          // multiple or different button pressed
        {
          GScanState = eMultiPressed;
          GDebounceTickCounter = VDEBOUNCETICKS;
          ProcessButtonEvent(eEvButtonRelease);
        }
        else                                // same button, so see if a long press
        {
          if(GLongPressCounter != 0)        // if we decrement the long press timeout to zero
            if(--GLongPressCounter == 0)    // it will be a long press
              ProcessButtonEvent(eEvButtonLongpress);          // action the button press as a long press
        }
        break;
      
      case eWaitReleased:                   // release state from a single press - debounce
        if (Row == 0)                       // button now released after debounce
        {
          GScanState = eIdle;
          GDebounceTickCounter = VDEBOUNCETICKS;
          ProcessButtonEvent(eEvButtonRelease);
        }
        else if (Row != GFoundRow)          // multiple or different button pressed
        {
          GScanState = eMultiPressed;
          GDebounceTickCounter = VDEBOUNCETICKS;
        }
        break;
      
      case eMultiPressed:                   // more than one pressed - wait until released
        if (Row == 0)                       // 1st detect of button released
        {
          GScanState = eWaitMultiReleased;
          GDebounceTickCounter = VDEBOUNCETICKS;
        }
        break;
      
      case eWaitMultiReleased:              // debounce state for release from multiple buttons pressed
        if (Row == 0)                       // button confirmed released after debounce
        {
          GScanState = eIdle;
          GDebounceTickCounter = VDEBOUNCETICKS;
        }
        else                                // there is still something pressed during debounces, so go back to waiting
        {
          GScanState = eMultiPressed;
          GDebounceTickCounter = VDEBOUNCETICKS;
        }
        break;
    }
  }
}


