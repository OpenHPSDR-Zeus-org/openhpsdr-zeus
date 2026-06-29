/////////////////////////////////////////////////////////////
//
// Saturn project: Artix7 FPGA + Raspberry Pi4 Compute Module
// PCI Express interface from linux on Raspberry pi
// this application uses C code to emulate HPSDR protocol 2 
//
// copyright Laurence Barker November 2021
// licenced under GNU GPL3
//
// LDGATU.h:
//
// interface an LDG ATU, sending CAT command to request TUNE if required
//
//////////////////////////////////////////////////////////////

#ifndef __LDGATU_h
#define __LDGATU_h


//
// function to initialise a connection to the  ATU; call if selected as a command line option
//
void InitialiseLDGHandler(void);


//
// function to request TUNE power
// paramter true to request tune power provision
//
void RequestATUTune(bool TuneRequested);



#endif      // file sentry