/////////////////////////////////////////////////////////////////////////
//
// Andromeda front panel controller by Laurence Barker G8NJJ
// this sketch provides a knob and switch interface through USB and CAT
//
//
// CAT handler.cpp
// this file holds the CAT handling code
// responds to parsed messages, and initiates message sends
// this is the main body of the program!
/////////////////////////////////////////////////////////////////////////

#include "globalinclude.h"
#include "cathandler.h"
#include "configdata.h"
#include "encoders.h"
#include "led.h"
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
void CATHandleVFOEncoder(signed char Clicks)
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
// Encoder number internally is 0-(N-1) in normal C style
//
void CATHandleEncoder(byte Encoder, char Clicks)
{
  int Param;

  if (Clicks > 0)                           // clockwise turn, non zero clicks
  {
    if (Clicks > 9)                         // clip value at max 9 clicks
      Clicks=9;
    Param = (Encoder + 1) * 10;
    Param += Clicks;
    MakeCATMessageNumeric(eZZZE, Param);
  }
  else if (Clicks < 0)                      // anticlockwise turn, non zero clicks
  {
    Clicks = -Clicks;
    if (Clicks > 9)                         // clip value at max 9 clicks
      Clicks=9;
    Param = (Encoder + 51) * 10;
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
  int Param;

  Param = (Button) * 10;                // get to param if unpressed
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
// function to send back the current encoder increment settings
//
void MakeEncoderIncrementMessage(void)
{
  int Result;

  Result = GVFOEncoderDivisor*10 + GEncoderDivisor;       // mmn
  MakeCATMessageNumeric(eZZZX,Result);
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
      if((ParsedParam % 10) != 0)
        State = true;
      Device = ParsedParam/10 - 1;
      SetLED(Device, State);
      break;

    case eZZZX:                                                       // set encoder increment
      Param = ParsedParam % 10;                                       // bottom digit - normal encoder
      Device = ParsedParam / 10;                                      // remaining higher digits
      if (Param == 0)                                                 // don't allow a zero
        GEncoderDivisor = 1;
      else
        GEncoderDivisor = Param;
        
      if (Device == 0)                                                 // don't allow a zero
        GVFOEncoderDivisor = 1;
      else
        GVFOEncoderDivisor = Device;

      CopySettingsToEEprom();
      SetEncoderDivisors(GEncoderDivisor, GVFOEncoderDivisor);
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
      MakeEncoderIncrementMessage();
      break;
  }
}
