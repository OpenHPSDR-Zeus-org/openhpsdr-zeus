/////////////////////////////////////////////////////////////////////////
//
// Andromeda front panel controller by Laurence Barker G8NJJ
// this sketch provides a knob and switch interface through USB and CAT
// copyright (c) Laurence Barker G8NJJ 2019
//
// configdata.h
// this file holds the code to save and load settings to/from FLASH
/////////////////////////////////////////////////////////////////////////

#ifndef __CONFIGDATA_H
#define __CONFIGDATA_H

#include "types.h"


//
// RAM storage of loaded settings
// these are loaded from FLASH after boot up
//
extern byte GEncoderDivisor;                                // number of edge events per declared click
extern byte GVFOEncoderDivisor;                             // number of edge events per declared click

//
// function to copy all config settings to flash
//
void CopySettingsToFlash(void);


//
// function to load config settings from flash
//
void LoadSettingsFromFlash(void);


//
// function to check FLASH is initialised, and load it if not
// called on start-up BEFORE loading the settings
//
void CheckFlashInitialised(void);


#endif  //not defined
