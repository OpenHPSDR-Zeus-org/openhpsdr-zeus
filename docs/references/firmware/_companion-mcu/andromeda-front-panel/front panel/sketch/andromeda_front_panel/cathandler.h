/////////////////////////////////////////////////////////////////////////
//
// Andromeda front panel controller by Laurence Barker G8NJJ
// this sketch provides a knob and switch interface through USB and CAT
// copyright (c) Laurence Barker G8NJJ 2019
//
// CAT handler.h
// this file holds the CAT handling code
/////////////////////////////////////////////////////////////////////////
#ifndef __cathandler_h
#define __cathandler_h
#include <Arduino.h>
#include "types.h"
#include "tiger.h"



//
// generate output messages for local control events
//
void CATHandleVFOEncoder(int Clicks);

void CATHandleEncoder(int Encoder, int Clicks);

void CATHandlePushbutton(int Button, bool IsPressed, bool IsLongPressed);




//
// handlers for received CAT commands
//
void HandleCATCommandNumParam(ECATCommands MatchedCAT, int ParsedParam);
void HandleCATCommandNoParam(ECATCommands MatchedCAT);


#endif //not defined
