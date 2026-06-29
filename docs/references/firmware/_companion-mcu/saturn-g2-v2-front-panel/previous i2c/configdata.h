/////////////////////////////////////////////////////////////////////////
//
// Saturn G2 front panel controller sketch by Laurence Barker G8NJJ
// this sketch provides a knob and switch interface through I2C
// copyright (c) Laurence Barker G8NJJ 2023
//
// the code is written for an Arduino Nano Every module
//
// configdata.h
// this file holds the code to save and load settings to/from EEprom
/////////////////////////////////////////////////////////////////////////

#ifndef __CONFIGDATA_H
#define __CONFIGDATA_H


//
// RAM storage of loaded settings
// these are loaded from EEprom after boot up
//
extern byte GEncoderDivisor;                                // number of edge events per declared click
extern byte GVFOEncoderDivisor;                             // number of edge events per declared click

//
// function to copy all config settings to EEprom
//
void CopySettingsToEEprom(void);


//
// function to load config settings from EEprom
//
void LoadSettingsFromEEprom(void);


//
// function to check EEprom is initialised, and load it if not
// called on start-up BEFORE loading the settings
//
void CheckEEpromInitialised(void);


#endif  //not defined
