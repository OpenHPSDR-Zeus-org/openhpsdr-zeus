/////////////////////////////////////////////////////////////////////////
//
// Saturn G2V1 front panel controller sketch by Laurence Barker G8NJJ
// enables G2V1 panel to be used with prcessor upgrades denying 40pin GPIO
// this sketch provides a knob and switch interface through USB serial
// copyright (c) Laurence Barker G8NJJ 2023
//
// the code is written for a Waveshare RP2040-piZero module

//
// CAT handler.h
// this file holds the CAT handling code
/////////////////////////////////////////////////////////////////////////
#ifndef __cathandler_h
#define __cathandler_h
#include <Arduino.h>
#include "tiger.h"



//
// generate output messages for local control events
//
void CATHandleVFOEncoder(signed int Clicks);

void CATHandleEncoder(byte Encoder, int Clicks);

void CATHandlePushbutton(byte Button, bool IsPressed, bool IsLongPressed);




//
// handlers for received CAT commands
//
void HandleCATCommandNumParam(ECATCommands MatchedCAT, int ParsedParam);
void HandleCATCommandNoParam(ECATCommands MatchedCAT);


#endif //not defined
