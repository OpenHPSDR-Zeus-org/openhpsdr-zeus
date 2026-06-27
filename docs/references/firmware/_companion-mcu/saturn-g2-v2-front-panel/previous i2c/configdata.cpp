/////////////////////////////////////////////////////////////////////////
//
// Saturn G2 front panel controller sketch by Laurence Barker G8NJJ
// this sketch provides a knob and switch interface through I2C
// copyright (c) Laurence Barker G8NJJ 2023
//
// the code is written for an Arduino Nano Every module
//
// configdata.cpp
// this file holds the code to save and load settings to/from EEPROM
/////////////////////////////////////////////////////////////////////////

#include <Arduino.h>
#include "globalinclude.h"
#include "encoders.h"

#include <EEPROM.h>

#define VEEINITPATTERN 0x6E                     // addr 0 set to this if configured

byte GEncoderDivisor;                                // number of edge events per declared click
byte GVFOEncoderDivisor;                             // number of edge events per declared click



//
// function to copy all config settings to EEprom
// this copies the current RAM vaiables to the persistent storage
// addr 0: defined pattern (to know EEPROM has been initialised)
// addr 1: normal encoder events per step
// addr 2: VFO encoder events per steo
// addr 3: display brightness
//
void CopySettingsToEEprom(void)
{
  int Addr=1;
  byte Setting;
  int Cntr;
  
//
// first set that we have initialised the EEprom
//
  EEPROM.write(0, VEEINITPATTERN);
//
// now copy settings from RAM data
//
  Setting = (byte) GEncoderDivisor;
  EEPROM.write(Addr++, Setting);
  Setting = (byte) GVFOEncoderDivisor;
  EEPROM.write(Addr++, Setting);
}



//
// function to copy initial settings to EEprom
// this sets the factory defaults
// the settings here should match the fornt panel legend!
//
void InitialiseEEprom(void)
{
  int Cntr;
  
  GEncoderDivisor = 2;                          // OK for the dual shaft encoders
  GVFOEncoderDivisor = 1;                       // max turn rate for Broadcom encoder (set to 4 for larger optical one)
 
// now copy them to FLASH
  CopySettingsToEEprom();
}



//
// function to load config settings from flash
//
void LoadSettingsFromEEprom(void)
{
  int Addr=1;
  byte Setting;
  int Cntr;

//
// first see if we have initialised the EEprom previously
// if not, copy settings to it
//
  Setting = EEPROM.read(0);
  if (Setting != VEEINITPATTERN)
    InitialiseEEprom();
//
// now copy out settings to RAM data
//
  GEncoderDivisor = (byte)EEPROM.read(Addr++);
  GVFOEncoderDivisor = (byte)EEPROM.read(Addr++);
  SetEncoderDivisors(GEncoderDivisor, GVFOEncoderDivisor);
}


