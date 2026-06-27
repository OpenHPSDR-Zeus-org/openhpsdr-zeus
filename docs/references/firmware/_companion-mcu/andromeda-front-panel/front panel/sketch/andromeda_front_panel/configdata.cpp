/////////////////////////////////////////////////////////////////////////
//
// Andromeda front panel controller by Laurence Barker G8NJJ
// this sketch provides a knob and switch interface through USB and CAT
// copyright (c) Laurence Barker G8NJJ 2019
//
// configdata.c
// this file holds the code to save and load settings to/from FLASH
/////////////////////////////////////////////////////////////////////////

#include <Arduino.h>
#include "globalinclude.h"

#include "types.h"
#include <DueFlashStorage.h>
DueFlashStorage dueFlashStorage;                 // instance of flash reader

#define VFLASHINITPATTERN 0x6D                    // addr 0 set to this if configured


//
// RAM storage of loaded settings
// these are loaded from FLASH after boot up
//
byte GEncoderDivisor;                                // number of edge events per declared click
byte GVFOEncoderDivisor;                             // number of edge events per declared click



//
// function to copy all config settings to flash
// this copies the current RAM vaiables to the persistent storage
//
void CopySettingsToFlash(void)
{
  int Addr=1;
  byte Setting;
  int Cntr;
  
//
// first set that we have initialised the FLASH
//
  dueFlashStorage.write(0, VFLASHINITPATTERN);
//
// now copy settings from RAM data
//
  Setting = (byte) GEncoderDivisor;
  dueFlashStorage.write(Addr++, Setting);
  Setting = (byte) GVFOEncoderDivisor;
  dueFlashStorage.write(Addr++, Setting);
}



//
// function to copy initial settings to FLASH
// this sets the factory defaults
// the settings here should match the fornt panel legend!
//
void InitialiseFlash(void)
{
  int Cntr;
  
  GEncoderDivisor = 2;
  GVFOEncoderDivisor = 4;
 
// now copy them to FLASH
  CopySettingsToFlash();
}



//
// function to load config settings from flash
//
void LoadSettingsFromFlash(void)
{
  int Addr=1;
  byte Setting;
  int Cntr;

//
// first see if we have initialised the FLASH previously
// if not, copy settings to it
//
  Setting = dueFlashStorage.read(0);
  if (Setting != VFLASHINITPATTERN)
    InitialiseFlash();
//
// now copy out settings to RAM data
//
  GEncoderDivisor = (byte)dueFlashStorage.read(Addr++);
  GVFOEncoderDivisor = (byte)dueFlashStorage.read(Addr++);
}
