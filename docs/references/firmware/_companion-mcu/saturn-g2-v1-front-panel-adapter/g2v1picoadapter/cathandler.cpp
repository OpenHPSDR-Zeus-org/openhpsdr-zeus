/////////////////////////////////////////////////////////////////////////
//
// Saturn G2V1 front panel controller sketch by Laurence Barker G8NJJ
// enables G2V1 panel to be used with prcessor upgrades denying 40pin GPIO
// this sketch provides a knob and switch interface through USB serial
// copyright (c) Laurence Barker G8NJJ 2023
//
// the code is written for a Waveshare RP2040-piZero module

//
// CAT handler.cpp
// this file holds the CAT handling code
// responds to parsed messages, and initiates message sends
// this is the main body of the program!
/////////////////////////////////////////////////////////////////////////

#include "globalinclude.h"
#include "cathandler.h"
#include "encoders.h"
#include <stdlib.h>


//
// clip to numerical limits allowed for a given message type
//
int ClipParameter(int Param, ECATCommands Cmd)
{
  SCATCommands* StructPtr;

  StructPtr = GCATCommands + (int)Cmd;
//
// clip the parameter to the allowed numeric range
//
  if (Param > StructPtr->MaxParamValue)
    Param = StructPtr->MaxParamValue;
  else if (Param < StructPtr->MinParamValue)
    Param = StructPtr->MinParamValue;
  return Param;  
}




//
// VFO encoder: simply request N steps up or down
//
void CATHandleVFOEncoder(int Clicks)
{
  
  if (Clicks != 0)
  {
    if (Clicks < 0)
      MakeCATMessageNumeric(eZZZD, -Clicks);
    else
      MakeCATMessageNumeric(eZZZU, Clicks);
  }
}


//
// other encoder: request N steps up or down
// report code generated is 1-8
//
void CATHandleEncoder(byte Encoder, int Clicks)
{
  int Param;

  if (Clicks > 0)                           // clockwise turn, non zero clicks
  {
    if (Clicks > 9)                         // clip value at max 9 clicks
      Clicks=9;
    Param = Encoder * 10;
    Param += Clicks;
    MakeCATMessageNumeric(eZZZE, Param);
  }
  else if (Clicks < 0)                      // anticlockwise turn, non zero clicks
  {
    Clicks = -Clicks;
    if (Clicks > 9)                         // clip value at max 9 clicks
      Clicks=9;
    Param = (Encoder + 50) * 10;
    Param += Clicks;
    MakeCATMessageNumeric(eZZZE, Param);
  }
}



//
// pushbutton: set pressed or unpressed state
// Button number internally is 0..(N-1) in normal C style
//
void CATHandlePushbutton(byte Button, bool IsPressed, bool IsLongPressed)
{
  unsigned int Param;

  Param = ((unsigned int)Button) * 10;                // get to param if unpressed
  if (IsLongPressed)
    Param += 2;
  else if (IsPressed)
    Param += 1;
  MakeCATMessageNumeric(eZZZP, Param);
}




//
// function to send back a software version message
//
void MakeSoftwareVersionMessage(void)
{
  long Version;
  Version = (PRODUCTID * 100000) + (HWVERSION*1000) + SWVERSION;
  
//  Version = SWVERSION;
  MakeCATMessageNumeric(eZZZS,Version);
}




//
// handle CAT commands with numerical parameters
//
void HandleCATCommandNumParam(ECATCommands MatchedCAT, int ParsedParam)
{
  int Device;
  byte Param;
  bool State = false;
  
  switch(MatchedCAT)
  {
    case eZZZI:                                                       // set indicator
      break;

    case eZZZX:                                                       // set divisors
      break;

  }
}


//
// handle CAT commands with no parameters
//
void HandleCATCommandNoParam(ECATCommands MatchedCAT)
{
  switch(MatchedCAT)
  {
    case eZZZS:                                                       // s/w version reply
      MakeSoftwareVersionMessage();
      break;

    case eZZZX:                                                       // encoder increment reply
      break;
  }
}
