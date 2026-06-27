/////////////////////////////////////////////////////////////////////////
//
// Odin SDR Console by Laurence Barker G8NJJ
//
// this sketch provides a knob and switch interface through USB and CAT
//
// display.c
// this file holds the code to control a Nextion 3.2" display
// it is 400x240 pixels
// note the Nextion Display appears on "Serial1"
// its serial port is defined in nexconfig.h in the library folder
// if the console does not have a display: NODISPLAY should be defined
// all funcvtions here are wrapped with #ifndef NODISPLAY
/////////////////////////////////////////////////////////////////////////

#include "globalinclude.h"
#include "display.h"
#include "types.h"
#include "led.h"
#include "stdlib.h"
#include "configdata.h"
#include "cathandler.h"
#include "encoders.h"

//
// define colours for  Nextion display
//
#define NEXBLACK 0L
#define NEXWHITE 65535L
#define NEXRED 63488L
#define NEXGREEN 2016L
#define NEXBLUE 31L


#define VDISPLAYMINANGLE 34                              // range 34 to 146 degrees
#define VMETERUPDATETICKS 10                  // 100ms update

EDisplayPage GDisplayPage;                    // global set to current display page number
EControlTypes GControlType;                   // type of control being edited
unsigned int GControlNumber;                  // number of control being edited
unsigned int GActionNumber;                   // displayed action of control
unsigned int G2ndActionNumber;                // displayed action of control
bool GDirectionSetting;                       // setting for the encoder direction button
int GMeterUpdateTicks;                        // ticks until we update the S meter
bool GValidFilterLow = false;                 // true if we have a valid filter value
bool GValidFilterHigh = false;                // true if we have a valid filter value
bool GIs4_3_Display = false;                  // true if we have the larger display

//
// settings for the "filter width" display area
// (4.3" display will have different settings)
#define VDISP32FILTX 280
#define VDISP32FILTY 122
#define VDISP32FILTW 120
#define VDISP32FILTH 15
#define VDISP43FILTX 312
#define VDISP43FILTY 170
#define VDISP43FILTW 168
#define VDISP43FILTH 15

#define VDISPLAYLOWERFNTICKS 500                  // 5 seconds

//
// variables to hold what's currently on the display
// the display is only updated if the new value is different
// to reduce flicker, CPU time
// (these initialise to zero)
//
bool DisplayABState = true;        // current A/B setting (true = A)
bool DisplayTXState;               // current TX state true=TX
bool DisplayTuneState;             // current TUNE state true = tune
bool DisplayRITState;              // current RIT state true=on
bool DisplayCurrentSplitState;     // currently displayed SPLIT state
bool DisplayCurrentLockState;      // currently displayed LOCK state
char DisplayCurrentFrequency[20] = "14.101056";  // currently displayed frequency
int DisplayCurrentSReading = 50;         // current S Meter reading (pointer angle; 34 to 146)
int DisplayCurrentPowerReading = 70;     // current TX Power Meter reading (pointer angle; 34 to 146)
//EBand DisplayCurrentBand;          // currently displayed mode NOT NEEDED
EMode DisplayCurrentMode;          // currently displayed mode
ENRState DisplayCurrentNRState;    // currently displayed NR setting
ENBState DisplayCurrentNBState;    // currently displayed NB setting
bool DisplayCurrentSNBState;       // currently displayed SNB state
bool DisplayCurrentANFState;       // currently displayed ANF state
EAGCSpeed DisplayCurrentAGCSpeed;  // currently displayed AGC speed
//int DisplayCurrentAGCThreshold;    // currently displayed AGC threshold requested on demand
EAtten DisplayCurrentAtten;        // currently displayed attenuation
EEncoderActions DisplayCurrentAction1;                      // display 1
EEncoderActions DisplayCurrentAction2;                      // display 2
EEncoderActions DisplayCurrentAction3;                      // display 3
EEncoderActions DisplayCurrentAction4;                      // display 4
EEncoderActions DisplayCurrentActionLower1;                 // display 1, for lower encoder
EEncoderActions DisplayCurrentActionLower2;                 // display 2, for lower encoder
EEncoderActions DisplayCurrentActionLower3;                 // display 3, for lower encoder
EEncoderActions DisplayCurrentActionLower4;                 // display 4, for lower encoder
bool Enc1DisplayLower;                                      // true if we should display the lower encoder action
bool Enc2DisplayLower;                                      // true if we should display the lower encoder action
bool Enc3DisplayLower;                                      // true if we should display the lower encoder action
bool Enc4DisplayLower;                                      // true if we should display the lower encoder action
int Enc1DisplayLowerCntr;                                   // ticks until we revert to display upper encoder function
int Enc2DisplayLowerCntr;                                   // ticks until we revert to display upper encoder function
int Enc3DisplayLowerCntr;                                   // ticks until we revert to display upper encoder function
int Enc4DisplayLowerCntr;                                   // ticks until we revert to display upper encoder function
bool Enc1IsMulti;                                           // true if display 1 is multi
bool Enc2IsMulti;                                           // true if display 2 is multi
bool Enc3IsMulti;                                           // true if display 3 is multi
bool Enc4IsMulti;                                           // true if display 4 is multi
int DisplayCurrentFilterLow;                                // LF edge of filter passband  
int DisplayCurrentFilterHigh;                               // HF edge of filter passband  



//
// declare pages:
//
NexPage page0main = NexPage(1, 0, "page0main");   // creates touch event for "main" page
NexPage page1 = NexPage(2, 0, "page1");       // creates touch event for "I/O Test" page
NexPage page2 = NexPage(3, 0, "page2");       // creates touch event for "About" page
NexPage page3 = NexPage(4, 0, "page3");       // creates touch event for "Frequency entry" page
NexPage page4 = NexPage(5, 0, "page4");       // creates touch event for "Band" page
NexPage page5 = NexPage(6, 0, "page5");       // creates touch event for "mode" page
NexPage page6 = NexPage(7, 0, "page6");       // creates touch event for "NR" page
NexPage page7 = NexPage(8, 0, "page7");       // creates touch event for "RF" page
NexPage page8 = NexPage(9, 0, "page8");       // creates touch event for "general settings" page
NexPage page9 = NexPage(10, 0, "page9");       // creates touch event for "configure" page
NexPage page0 = NexPage(0, 0, "page0");       // creates touch event for "splash" page

//
// initial page ("real" page 0) object
//
NexVariable p0vasize = NexVariable(0, 5, "vasize");  // screen size

//
// page 0 objects:
//
NexText p0t0 = NexText(1, 1, "t0");                   // VFO A/B
NexText p0t2 = NexText(1, 4, "t2");                   // frequency
NexText p0t8 = NexText(1, 13, "t8");                  // mode
NexText p0t4 = NexText(1, 6, "t4");                   // RHS encoder
NexText p0t5 = NexText(1, 7, "t5");                   // LHS encoder
NexText p0t6 = NexText(1, 8, "t6");                   // middle encoder
NexText p0t7 = NexText(1, 15, "t7");                  // RHS encoder
NexText p0t9 = NexText(1, 17, "t9");                  // LOCK
NexText p0t10 = NexText(1, 18, "t10");                // RIT "on" text
NexText p0t11 = NexText(1, 19, "t11");                // SPLIT
NexGauge p0z0 = NexGauge(1, 14, "z0");                // gauge
NexText p0t13 = NexText(1, 21, "t13");                // RX/TX/TUNE indicator

//
// declare objects on I/O test page:
// these are all on "page1"
NexDSButton p1bt0 = NexDSButton(2, 6, "bt0");         // LED 1 button
NexDSButton p1bt1 = NexDSButton(2, 7, "bt1");         // LED 2 button
NexDSButton p1bt2 = NexDSButton(2, 8, "bt2");         // LED 3 button
NexDSButton p1bt3 = NexDSButton(2, 9, "bt3");         // LED 4 button
NexDSButton p1bt4 = NexDSButton(2, 10, "bt4");        // LED 5 button
NexDSButton p1bt5 = NexDSButton(2, 11, "bt5");        // LED 6 button
NexDSButton p1bt6 = NexDSButton(2, 12, "bt6");        // LED 7 button
NexText p1tEnc1 = NexText(2, 13, "tenc1");            // encoder 1 display
NexText p1tEnc2 = NexText(2, 14, "tenc2");            // encoder 2 display
NexText p1tEnc3 = NexText(2, 15, "tenc3");            // encoder 3 display
NexText p1tEnc4 = NexText(2, 16, "tenc4");            // encoder 4 display
NexText p1tEnc5 = NexText(2, 17, "tenc5");            // encoder 5 display
NexText p1tEnc6 = NexText(2, 18, "tenc6");            // encoder 6 display
NexText p1tEnc7 = NexText(2, 19, "tenc7");            // encoder 7 display
NexText p1tEnc8 = NexText(2, 20, "tenc8");            // encoder 8 display
NexText p1tEnc9 = NexText(2, 38, "tenc9");            // vfo encoder display
NexText p1tpb8 = NexText(2, 22, "tpb8");              // pushbutton SW1 display
NexText p1tpb9 = NexText(2, 23, "tpb9");              // pushbutton SW2 display
NexText p1tpb10 = NexText(2, 24, "tpb10");            // pushbutton SW3 display
NexText p1tpb11 = NexText(2, 25, "tpb11");            // pushbutton SW4 display
NexText p1tpb12 = NexText(2, 26, "tpb12");            // pushbutton SW5 display
NexText p1tpb13 = NexText(2, 27, "tpb13");            // pushbutton SW6 display
NexText p1tpb14 = NexText(2, 28, "tpb14");            // pushbutton SW7 display
NexText p1tpb15 = NexText(2, 29, "tpb15");            // pushbutton SW8 display
NexText p1tpb16 = NexText(2, 30, "tpb16");            // pushbutton SW9 display
NexText p1tpb17 = NexText(2, 31, "tpb17");            // pushbutton SW10 display
NexText p1tpb18 = NexText(2, 32, "tpb18");            // pushbutton SW11 display
NexText p1tpb19 = NexText(2, 33, "tpb19");            // pushbutton SW12 display
NexText p1tpb20 = NexText(2, 34, "tpb20");            // pushbutton SW13 display
NexText p1tpb21 = NexText(2, 35, "tpb21");            // pushbutton SW14  display
NexText p1tpb22 = NexText(2, 36, "tpb22");            // pushbutton SW15 display
NexText p1tpb23 = NexText(2, 37, "tpb23");            // pushbutton SW16 display
NexText p1tpb24 = NexText(2, 39, "tpb24");            // pushbutton SW17 display
NexText p1tpb25 = NexText(2, 40, "tpb25");            // ext PTT display

//
// page 2 objects:
//
NexButton p2b1 = NexButton(3, 6, "p2b1");             // encoder button
NexButton p2b2 = NexButton(3, 7, "p2b2");             // pushbutton button
NexButton p2b3 = NexButton(3, 8, "p2b3");             // indicator button
NexButton p2b5 = NexButton(3, 10, "b5");              // save settings button
NexText p2t5 = NexText(3, 12, "t5");                  // Arduino s/w string

//
// page 3 objects:
//
NexButton p3b1 = NexButton(4, 5, "b1");               // set frequency button
NexText p3t2 = NexText(4, 2, "t2");                   // frequency text


//
// page 4 objects:
//
NexVariable p4vaband = NexVariable(5, 16, "vaband");  // band state variable
NexDSButton p4bt0 = NexDSButton(5, 4, "bt0");         // 160M button
NexDSButton p4bt1 = NexDSButton(5, 5, "bt1");         // 80M button
NexDSButton p4bt2 = NexDSButton(5, 6, "bt2");         // 60M button
NexDSButton p4bt3 = NexDSButton(5, 7, "bt3");         // 40M button
NexDSButton p4bt4 = NexDSButton(5, 8, "bt4");         // 30M button
NexDSButton p4bt5 = NexDSButton(5, 9, "bt5");         // 20M button
NexDSButton p4bt6 = NexDSButton(5, 10, "bt6");        // 17M button
NexDSButton p4bt7 = NexDSButton(5, 11, "bt7");        // 15M button
NexDSButton p4bt8 = NexDSButton(5, 12, "bt8");        // 12M button
NexDSButton p4bt9 = NexDSButton(5, 13, "bt9");        // 10M button
NexDSButton p4bt10 = NexDSButton(5, 14, "bt10");      // 6M button
NexDSButton p4bt11 = NexDSButton(5, 15, "bt11");      // "gen" button


//
// page 5 objects:
//
NexVariable p5vamode = NexVariable(6, 16, "vamode");  // mode state variable
NexDSButton p5bt0 = NexDSButton(6, 4, "bt0");         // LSB button
NexDSButton p5bt1 = NexDSButton(6, 5, "bt1");         // USB button
NexDSButton p5bt2 = NexDSButton(6, 6, "bt2");         // DSB button
NexDSButton p5bt3 = NexDSButton(6, 7, "bt3");         // CWL button
NexDSButton p5bt4 = NexDSButton(6, 8, "bt4");         // CWU button
NexDSButton p5bt5 = NexDSButton(6, 9, "bt5");         // FM button
NexDSButton p5bt6 = NexDSButton(6, 10, "bt6");        // AM button
NexDSButton p5bt7 = NexDSButton(6, 11, "bt7");        // SAM button
NexDSButton p5bt8 = NexDSButton(6, 12, "bt8");        // SPEC button
NexDSButton p5bt9 = NexDSButton(6, 13, "bt9");        // DIG L button
NexDSButton p5bt10 = NexDSButton(6, 14, "bt10");      // DIG U button
NexDSButton p5bt11 = NexDSButton(6, 15, "bt11");      // DRM button

//
// page 6 (noise) objects
//
NexDSButton p6bt2 = NexDSButton(7, 6, "bt2");         // NR off button
NexDSButton p6bt3 = NexDSButton(7, 7, "bt3");         // NR 1 button
NexDSButton p6bt4 = NexDSButton(7, 8, "bt4");         // NR 2 button
NexDSButton p6bt5 = NexDSButton(7, 9, "bt5");         // NB off button
NexDSButton p6bt6 = NexDSButton(7, 10, "bt6");        // NB 1 button
NexDSButton p6bt7 = NexDSButton(7, 11, "bt7");        // NB 2 button
NexDSButton p6bt0 = NexDSButton(7, 4, "bt0");         // SNB button
NexDSButton p6bt1 = NexDSButton(7, 5, "bt1");         // ANF button
NexVariable p6vanr = NexVariable(7, 12, "vanr");      // NR state variable
NexVariable p6vanb = NexVariable(7, 13, "vanb");      // NB state variable


//
// page 7 (RF) objects
//
NexDSButton p7bt0 = NexDSButton(8, 5, "bt0");         // 0dB button
NexDSButton p7bt1 = NexDSButton(8, 6, "bt1");         // 10dB button
NexDSButton p7bt2 = NexDSButton(8, 7, "bt2");         // 20dB button
NexDSButton p7bt3 = NexDSButton(8, 8, "bt3");         // 30dB button
NexDSButton p7bt4 = NexDSButton(8, 11, "bt4");        // fixed button
NexDSButton p7bt5 = NexDSButton(8, 12, "bt5");        // long button
NexDSButton p7bt6 = NexDSButton(8, 13, "bt6");        // slow button
NexDSButton p7bt7 = NexDSButton(8, 14, "bt7");        // med button
NexDSButton p7bt8 = NexDSButton(8, 15, "bt8");        // fast button
NexVariable p7vaagc = NexVariable(8, 16, "vaagc");    // agc state variable
NexVariable p7vaatten = NexVariable(8, 17, "vaatten");  // atten state variable
NexSlider p7h0 = NexSlider(8, 3, "h0");               // AGC threshold


//
// page 8 "general settings" objects
// note multiple callbacks folded over onto just 2 buttons
//
NexDSButton p8bt0 = NexDSButton(9, 5, "bt0");         // 9600 baud button
NexDSButton p8bt1 = NexDSButton(9, 6, "bt1");         // 9600 baud button
NexDSButton p8bt2 = NexDSButton(9, 7, "bt2");         // 9600 baud button
NexDSButton p8bt4 = NexDSButton(9, 8, "bt4");         // "click" button
NexDSButton p8bt5 = NexDSButton(9, 9, "bt5");         // "click" button
NexDSButton p8bt3 = NexDSButton(9, 13, "bt3");        // "bottom" button
NexDSButton p8bt6 = NexDSButton(9, 14, "bt6");        // "side" button
NexVariable p8vaenc = NexVariable(9, 11, "vaenc");    // encoder state variable
NexVariable p8vabaud = NexVariable(9, 10, "vabaud");  // baud rate variable
NexButton p8b1 = NexButton(9, 20, "b1");              // encoder divisor minus
NexButton p8b2 = NexButton(9, 19, "b2");              // encoder divisor plus
NexButton p8b3 = NexButton(9, 21, "b3");              // VFO encoder divisor minus
NexButton p8b4 = NexButton(9, 22, "b4");              // VFO encoder divisor plus
NexText p8t4 = NexText(9, 15, "t4");                  // encoder divisor 
NexText p8t7 = NexText(9, 18, "t7");                  // VFO encoder divisor 

//
// page 9 "configure" objects
//
NexText p9t6 = NexText(10, 12, "t6");                  // indicator/encoder/button number
NexText p9t4 = NexText(10, 8, "t4");                   // function
NexButton p9b1 = NexButton(10, 4, "b1");               // device number minus
NexButton p9b2 = NexButton(10, 5, "b2");               // device number plus
NexButton p9b3 = NexButton(10, 6, "b3");               // function minus
NexButton p9b4 = NexButton(10, 7, "b4");               // function plus
NexButton p9b7 = NexButton(10, 10, "b7");              // Accept/Set
NexDSButton p9bt0 = NexDSButton(10, 13, "bt0");        // "direction" button

//
// declare touch event objects to the touch event list
// this tells the code what touch events too look for
//
NexTouch *nex_listen_list[] = 
{
  &page0main,                                 // page change 
  &page1,                                     // page change
  &page2,                                     // page change
  &page3,                                     // page change
  &page4,                                     // page change
  &page5,                                     // page change
  &page6,                                     // page change
  &page7,                                     // page change
  &page8,                                     // page change
  &page9,                                     // page change
  &page0,                                    // page change
  &p1bt0,                                     // LED 1 button
  &p1bt1,                                     // LED 2 button
  &p1bt2,                                     // LED 3 button
  &p1bt3,                                     // LED 4 button
  &p1bt4,                                     // LED 5 button
  &p1bt5,                                     // LED 6 button
  &p1bt6,                                     // LED 7 button
  &p2b1,                                      // encoder configure button
  &p2b2,                                      // pushbutton configure button
  &p2b3,                                      // indicator configure button
  &p2b5,                                      // save settings button
  &p3b1,                                      // set frequency button
  &p4bt0,                                     // 160M button (all callbacks fold to this)
  &p5bt0,                                     // LSB button (all callbacks fold to this)
  &p6bt2,                                     // NR (3 buttons fold to this)
  &p6bt5,                                     // NB (3 buttons)
  &p6bt0,                                     // SNB
  &p6bt1,                                     // ANF
  &p7bt0,                                     // Atten (4 buttons)
  &p7bt4,                                     // AGC (5 buttons fold onto this callback)
  &p7h0,                                      // threshold slider
  &p8bt0,                                     // baud button
  &p8bt3,                                     // encoder string bottom button
  &p8bt4,                                     // click button
  &p8bt6,                                     // encoder string side button
  &p8b1,                                      // encoder divisor -
  &p8b2,                                      // encoder divisor +
  &p8b3,                                      // VFO encoder divisor -
  &p8b4,                                      // VFO encoder divisor +
  &p9b1,                                      // configure page #-
  &p9b2,                                      // configure page #+
  &p9b3,                                      // configure page fn-
  &p9b4,                                      // configure page fn+
  &p9b7,                                      // configure page Set/Accept
  &p9bt0,                                     // "direction"
  NULL                                        // terminates the list
};


//
// strings for mode
//
char* ModeStrings[] = 
{
  "LSB",
  "USB",
  "DSB",
  "CWL",
  "CWU",
  "FM",
  "AM",
  "DIG U",
  "SPEC",
  "DIG L",
  "SAM",
  "DRM"
};


//
// strings for band
//
char* BandStrings[] = 
{
  "160m",
  "80m",
  "60m",
  "40m",
  "30m",
  "20m",
  "17m",
  "15m",
  "12m",
  "10m",
  "60m",
  "GEN"
};


//
// strings for NR
//
char* NRStrings[] =
{
  "NR off",
  "NR 1",
  "NR 2" 
};

//
// strings for NB
//
char* NBStrings[] =
{
  "NB off",
  "NB 1",
  "NB 2" 
};


//
// strings for atten
//
char* AttenStrings[] =
{
  "0 dB",
  "10 dB",
  "20 dB",
  "30 dB" 
};


//
// strings for AGC
//
char* AGCStrings[] =
{
  "Fixed",
  "Long",
  "Slow",
  "Medium", 
  "Fast" 
};





//
// strings for editing control settings
// the string count must match the number of enumerations!
// 25 chars max!
// 123456789012345678901234*
//
char* EncoderActionStrings[] = 
{
  "No action",
  "Master AF Gain",
  "A/B AF Gain",
  "RX1 AF Gain",
  "RX2 AF Gain",
  "A/B AGC Level",
  "RX1 AGC level",
  "RX2 AGC level", 
  "A/B Step Atten",
  "RX1 Step Atten",
  "RX2 Step Atten",
  "Filter High Cut",
  "Filter Low Cut",
  "Drive",
  "Mic Gain",
  "VFO A Tune",
  "VFO B Tune",
  "VOX Gain",
  "VOX Delay",
  "CW Sidetone",
  "CW Speed",
  "Squelch level",
  "Diversity Gain",
  "Diversity Phase",
  "Comp Threshold",
  "RIT",
  "Display Pan",
  "Display Zoom",
  "Sub-RX AF Gain",
  "Sub-RX Stereo Balance",
  "RX1 Stereo Balance",
  "RX2 Stereo Balance",
  "Multifunction"        // multifunction
};

char* MultiEncoderActionStrings[] = 
{
  "M:No Action",
  "M:Master AF",
  "M:A/B AF Gain",
  "M:RX1 AF Gain",
  "M:RX2 AF Gain",
  "M:A/B AGC Level",
  "M:RX1 AGC",
  "M:RX2 AGC", 
  "M:A/B Step Atten",
  "M:RX1 Step Atten",
  "M:RX2 Step Atten",
  "M:Filt High",
  "M:Filt Low",
  "M:Drive",
  "M:Mic Gain",
  "M:VFO A",
  "M:VFO B",
  "M:VOX Gain",
  "M:VOX Delay",
  "M:CW Tone",
  "M:CW Speed",
  "M:Squelch",
  "M:Div'ty Gain",
  "M:Div'ty Phase",
  "M:Comp",
  "M:RIT",
  "M:Display Pan",
  "M:Display Zoom",
  "M:Sub-RX AF Gain",
  "M:Sub-RX Stereo Balance",
  "M:RX1 Stereo Balance",
  "M:RX2 Stereo Balance",
  "M:Multi"                      // multifunction
};


//
// strings for CAT display of multifunction device setting
// the string count must match the number of enumerations!
// Each must be exactly 15 chars!
// 123456789012345678901234*
//
char* MultiEncoderCATStrings[] = 
{
  "No action      ",
  "Master AF Gain ",
  "A/B AF Gain    ",
  "RX1 AF Gain    ",
  "RX2 AF Gain    ",
  "A/B AGC Level  ",
  "RX1 AGC level  ",
  "RX2 AGC level  ", 
  "A/B Step Atten ",
  "RX1 Step Atten ",
  "RX2 Step Atten ",
  "Filter High Cut",
  "Filter Low Cut ",
  "Drive          ",
  "Mic Gain       ",
  "VFO A Tune     ",
  "VFO B Tune     ",
  "VOX Gain       ",
  "VOX Delay      ",
  "CW Sidetone    ",
  "CW Speed       ",
  "Squelch level  ",
  "Diversity Gain ",
  "Diversity Phase",
  "Comp Threshold ",
  "RIT            ",
  "Display Pan    ",
  "Display Zoom   ",
  "Sub-RX Gain    ",
  "Sub-RX Stereo  ",
  "RX1 Stereo Bal ",
  "RX2 Stereo Bal ",
  "Multifunction  "        // multifunction (this should never happen)
};

char* IndicatorActionStrings[] = 
{
  "MOX",
  "Tune",
  "RIT On",
  "Split",
  "Click Tune on",
  "VFO Lock",
  "NB on",
  "NR on",
  "SNB on",
  "ANF on",
  "Squelch on",
  "VFO A/B",
  "Comp Enabled",
  "PS Enabled",
  "XIT On",
  "VFO Sync On",
  "Encoder 2nd fn",
  "None"
};


char* ButtonActionStrings[] = 
{
  "No Function",                            // no assigned function
  "Encoder Click",                    // for dual fn encoders
  "Toggle A/B VFO",
  "Mox",
  "Tune",
  "A/B AF Mute",
  "RX1 Mute",
  "RX2 Mute",
  "Filter Reset",
  "Band Up",
  "Band Down",
  "Mode Up",
  "Mode Down",
  "AGC Speed",
  "NB Step",
  "NR Step",
  "SNB",
  "ANF",
  "RIT On/off",
  "RIT Step Up",
  "RIT Step Down",
  "Copy VFO A to VFO B",
  "Copy VFO B to VFO A",
  "swap VFO A & B",
  "Split",
  "Click Tune",
  "VFO Lock",
  "Radio Start / Stop",
  "Squelch on/off",
  "Atten step",
  "VOX on/off",
  "Diversity fast/slow",
  "Comp Enable",
  "PS Enable",
  "PS 2 Tone Cal",
  "PS Single Cal",
  "MON Enable",
  "Diversity Enable",
  "VFO Sync",
  "ClearcRIT",
  "Filter Up",
  "Filter Down",
  "VAC 1",
  "VAC 2",
  "Centre Display"
};


//
// I/O test LED strings
//
char* IOTestLEDStrings[]=
{
  "LED1",
  "LED2",
  "LED3",
  "LED4",
  "LED5",
  "LED6",
  "LED7"
};


//
// I/O test encoder strings
//
char* IOTestEncoderStrings[]=
{
  "Enc 2A",
  "Enc 2B",
  "Enc 3A",
  "Enc 3B",
  "Enc 4A",
  "Enc 4B",
  "Enc 5A",
  "Enc 5B"
};


//
// I/O test pushbutton strings
//
char* IOTestButtonStrings[] =
{
  "SW1",
  "SW2",
  "SW3",
  "SW4",
  "SW5",
  "SW6",
  "SW7",
  "SW8",
  "SW9",
  "SW10",
  "SW11",
  "SW12",
  "SW13",
  "SW14",
  "SW15",
  "SW16",
  "SW17",
  "SW_E2",
  "SW_E3",
  "SW_E4",
  "SW_E5"
};



//
// list of encoder text box controls for the I/O test page
// indexed by the button number (0 to 20; 17 pushbuttons + 4 encoder clicks)
//
const char * BtnObjectNames[] = 
{
  "tpb8",             // button 1
  "tpb9",
  "tpb10",            // button 3
  "tpb11",
  "tpb12",            // button 5
  "tpb13",
  "tpb14",            // button 7
  "tpb15",
  "tpb16",            // button 9
  "tpb17",
  "tpb18",            // button 11
  "tpb19",
  "tpb20",            // button 13
  "tpb21",
  "tpb22",            // button 15
  "tpb23",
  "tpb24",            // button 17
  "tenc1",            // encoder 2
  "tenc3",            // encoder 3
  "tenc5",            // encoder 4
  "tenc7"             // encoder 5
};


//
// draw a bar on the screen
// this has X,Y,W,H co-ordinates and a colour
// it uses the display's native "fill" command (see the Nextion command set)
// and uses the Arduino library sendCommand to issue it to the display
// note the fill command is "fill X,Y,W,H,Col" with NO spaces!
//
void DrawDisplayBar (unsigned int X, unsigned int Y, unsigned int W, unsigned int H, unsigned int Colour)
{
#ifndef NODISPLAY                               // if defined, no action
  
  char CmdBuffer[30], Cmd[10];
  strcpy(CmdBuffer, "fill ");                   // "fill "
  itoa(X, Cmd, 10);
  strcat(CmdBuffer,Cmd);
  strcat(CmdBuffer,",");                        // "fill X,"
  itoa(Y, Cmd, 10);
  strcat(CmdBuffer,Cmd);
  strcat(CmdBuffer,",");                        // "fill X,Y,"
  itoa(W, Cmd, 10);
  strcat(CmdBuffer,Cmd);
  strcat(CmdBuffer,",");                        // "fill X,Y,W,"
  itoa(H, Cmd, 10);
  strcat(CmdBuffer,Cmd);
  strcat(CmdBuffer, ",");                       // "fill X,Y,W,H,"
  itoa(Colour, Cmd, 10);
  strcat(CmdBuffer,Cmd);                        // "fill X,Y,W,H,colour"
  sendCommand(CmdBuffer);
#endif
}

//
// function to redraw the IF filter passband
// only draw if the correct (main) page shown!
// the action needed is to dwaw a white rectangle (to erase)
// then a black bar
//
void RedrawFilterPassband(void)
{
  #ifndef NODISPLAY                               // if defined, no action

  long IdealLow, IdealHigh, IdealWidth, IdealCentre;      // ideal filter passband 
  long DisplayWidth, DisplayLow, DisplayHigh;             // display range (LHS pixel, centre pixel, RHS pixel)
  int LeftPix, RightPix, WidthPix;                        // low and high pixels for actual filter settings
  long PixelWidth;
  int StartPix;                                           // left hand side of bar
  int Filtx, Filty, Filtw, Filth;                         // co-ordinates of display area

//
// pick up correct coordinates
//
  if (GIs4_3_Display == true)
  {
    Filtx = VDISP43FILTX;
    Filty = VDISP43FILTY;
    Filtw = VDISP43FILTW;
    Filth = VDISP43FILTH;
  }
  else
  {
    Filtx = VDISP32FILTX;
    Filty = VDISP32FILTY;
    Filtw = VDISP32FILTW;
    Filth = VDISP32FILTH;
  }
//
// first of all find the frequency range corresponding to the displayed passband (centred on the pixel range)
//
  PixelWidth = Filtw;
  IdealLow = GetOptimumIFFilterLow();             // get mode dependent low and high values
  IdealHigh = GetOptimumIFFilterHigh();
  IdealWidth = abs(IdealHigh - IdealLow);
  IdealCentre = (IdealHigh + IdealLow)/2;              // centre of display area
  DisplayWidth = 2* IdealWidth;
  DisplayLow = IdealCentre - IdealWidth;
  DisplayHigh = IdealCentre + IdealWidth;


//
// calculate edges of the pixel region to draw
// note that reversed spectrum calculated differently!
//

  LeftPix = (int) ((PixelWidth * (DisplayCurrentFilterLow-DisplayLow))/DisplayWidth);
  RightPix = (int) ((PixelWidth * (DisplayCurrentFilterHigh-DisplayLow))/DisplayWidth);
// clip to the display area
  LeftPix = constrain(LeftPix, 0, PixelWidth-1);
  RightPix = constrain(RightPix, 0, PixelWidth-1);
  
  if((DisplayCurrentMode == eLSB) || (DisplayCurrentMode == eCWL) || (DisplayCurrentMode == eDIGL))     // if reversed spectrum
  {
    LeftPix = PixelWidth - LeftPix;                           // reversed if an LSB mode
    RightPix = PixelWidth - RightPix;                         // reversed if an LSB mode
    StartPix = RightPix;                                       // draw from left to right pixel
    WidthPix = LeftPix-RightPix+1;  
  }
  else
  {
    StartPix = LeftPix;                                       // draw from left to right pixel
    WidthPix = RightPix-LeftPix+1;  
  }
  StartPix += Filtx;
//
// now erase the drawing area;
// then redraw if valid low and high filter settings
// 
  if ((GDisplayPage == eFrontPage) && (GValidFilterHigh) && (GValidFilterLow))
  {
    DrawDisplayBar(Filtx, Filty, Filtw, Filth, NEXWHITE);     // erase old bar
    DrawDisplayBar(StartPix, Filty, WidthPix, Filth, NEXGREEN);     // draw new bar
  }
#endif
}

//
// functions to redraw test boxes on main page (page 0)
// VFO status: 10ch to show "SPLIT LOCK" (with spaces if not active)
//
void RedrawVFOStatusBox(void)
{
  #ifndef NODISPLAY                               // if defined, no action

  char String[30];
  memset(String, 0, sizeof(String));

  if (DisplayCurrentSplitState)
    strcpy(String, "SPLIT");
  else
    strcpy(String, "     ");

  if (DisplayCurrentLockState)
    strcat(String, " LOCK");
  else
    strcat(String, "     ");

  if(GDisplayPage == eFrontPage)
    p0t9.setText(String);  
#endif
}


//
// RX status: 20ch to show "30dB NB2 NR2 SNB ANF" (with spaces if not active)
//
void RedrawRXStatusBox(void)
{
#ifndef NODISPLAY                               // if defined, no action
  
  char String[30];
  memset(String, 0, sizeof(String));

  switch(DisplayCurrentAtten)
  {
    case e0dB:  strcpy(String, " 0dB"); break;
    case e10dB: strcpy(String, "10dB"); break;
    case e20dB: strcpy(String, "20dB"); break;
    case e30dB: strcpy(String, "30dB"); break;
  }

  switch(DisplayCurrentNBState)
  {
    case eNBOff: strcat(String, "    "); break;
    case eNB1:   strcat(String, " NB "); break;
    case eNB2:   strcat(String, " NB2"); break;
  }

  switch(DisplayCurrentNRState)
  {
    case eNROff: strcat(String, "    "); break;
    case eNR1:   strcat(String, " NR "); break;
    case eNR2:   strcat(String, " NR2"); break;
  }

  if(DisplayCurrentSNBState)
    strcat(String, " SNB");
  else
    strcat(String, "    ");

  if(DisplayCurrentANFState)
    strcat(String, " ANF");
  else
    strcat(String, "    ");

  if(GDisplayPage == eFrontPage)
    p0t11.setText(String);  
#endif
}


void RedrawRIT(bool IsRIT)
{
#ifndef NODISPLAY                               // if defined, no action
  
  if (IsRIT)
  {
    p0t10.Set_font_color_pco(NEXRED);
    p0t10.setText("ON");  
  }
  else
  {
    p0t10.Set_font_color_pco(NEXBLACK);
    p0t10.setText("OFF");
  }
#endif
}

//
// "helper" function to find the max number of controls of the type being edited
// return one less than the number
// (ie for encoders return 6 because we are allowed 0..6)
//
unsigned int Page9GetMaxControlCount(void)
{
#ifndef NODISPLAY                               // if defined, no action

  unsigned int Result;
  switch(GControlType)                        // now find the initial actions
  {
    case eEncoders:
      Result = VMAXENCODERS - 1;
      break;
    case ePushbuttons:
      Result = VMAXBUTTONS - 1;
      break;
    case eIndicators:
      Result = VMAXINDICATORS - 1;
      break;
  }
  return Result;  
#else
  return 0;
#endif  
}


//
// "helper" function to find the max number of actions of the type being edited
// return one less than the number
//
unsigned int Page9GetMaxActionCount(void)
{
#ifndef NODISPLAY                               // if defined, no action
  
  unsigned int Result;
  switch(GControlType)                        // now find the initial actions
  {
    case eEncoders:
      Result = VNUMENCODERACTIONS - 1;
      break;
    case ePushbuttons:
      Result = VNUMBUTTONACTIONS - 1;
      break;
    case eIndicators:
      Result = VNUMINDICATORACTIONS - 1;
      break;
  }
  return Result;  
#else
  return 0;
#endif  
}


//
// a "helper" function to get the current programmed actions for the configure page
// this retrieves the settings currently in Flash EPROM
//
void Page9GetActions(void)
{
#ifndef NODISPLAY                               // if defined, no action
  
  switch(GControlType)                        // now find the initial actions
  {
    case eEncoders:
      GActionNumber = (unsigned int) GetEncoderAction(GControlNumber, false);      // get current programmed main & 2nd actions
      GDirectionSetting = GetEncoderReversed(GControlNumber);
      break;
    case ePushbuttons:
      GActionNumber = (unsigned int) GetButtonAction(GControlNumber);
      break;
    case eIndicators:
      GActionNumber = (unsigned int) GetIndicatorAction(GControlNumber);
      break;
  }
#endif
}



//
// a "helper" function to set the controls in the configure page
//
void Page9SetControls(void)
{
#ifndef NODISPLAY                               // if defined, no action
  
  switch(GControlType)                        // now find the initial action and display it
  {
    case eEncoders:
      p9t6.setText(IOTestEncoderStrings[GControlNumber]);              // show what control were editing
      p9t4.setText(EncoderActionStrings[GActionNumber]);             // show in text boxes
      p9bt0.setValue((int)GDirectionSetting);
      if (GDirectionSetting)
        p9bt0.setText("reversed");
      else
        p9bt0.setText("normal");
      break;

    case ePushbuttons:
      p9t6.setText(IOTestButtonStrings[GControlNumber]);              // show what control were editing
      p9t4.setText(ButtonActionStrings[GActionNumber]);             // show in text boxes
      break;

    case eIndicators:
      p9t6.setText(IOTestLEDStrings[GControlNumber]);              // show what control were editing
      p9t4.setText(IndicatorActionStrings[GActionNumber]);             // show in text boxes
      break;
  }
#endif
}



//
// helper to redraw encoder strings
//
void RedrawEncoderString1(void)
{
#ifndef NODISPLAY                               // if defined, no action
  
  if ( GDisplayPage == eFrontPage)
  {
//
// first get the colour correct:
//    
    if (Enc1DisplayLower)
      p0t5.Set_font_color_pco(NEXRED);
    else
      p0t5.Set_font_color_pco(NEXBLUE);
//
// now display the string
//
    if (Enc1DisplayLower)
      p0t5.setText(EncoderActionStrings[(unsigned int)DisplayCurrentActionLower1]);
    else
    {
      if (Enc1IsMulti)
        p0t5.setText(MultiEncoderActionStrings[(unsigned int)DisplayCurrentAction1]);
      else
        p0t5.setText(EncoderActionStrings[(unsigned int)DisplayCurrentAction1]);
    }
  }
#endif
}

void RedrawEncoderString2(void)
{
#ifndef NODISPLAY                               // if defined, no action

  if ( GDisplayPage == eFrontPage)
  {
//
// first get the colour correct:
//    
    if (Enc2DisplayLower)
      p0t6.Set_font_color_pco(NEXRED);
    else
      p0t6.Set_font_color_pco(NEXBLUE);
//
// now display the string
//
    if (Enc2DisplayLower)
      p0t6.setText(EncoderActionStrings[(unsigned int)DisplayCurrentActionLower2]);
    else
    {
      if (Enc2IsMulti)
        p0t6.setText(MultiEncoderActionStrings[(unsigned int)DisplayCurrentAction2]);
      else
        p0t6.setText(EncoderActionStrings[(unsigned int)DisplayCurrentAction2]);
    }
  }
#endif
}

void RedrawEncoderString3(void)
{
#ifndef NODISPLAY                               // if defined, no action
  
  if ( GDisplayPage == eFrontPage)
  {
//
// first get the colour correct:
//    
    if (Enc3DisplayLower)
      p0t7.Set_font_color_pco(NEXRED);
    else
      p0t7.Set_font_color_pco(NEXBLUE);
//
// now display the string
//
    if (Enc3DisplayLower)
      p0t7.setText(EncoderActionStrings[(unsigned int)DisplayCurrentActionLower3]);
    else
    {
      if (Enc3IsMulti)
        p0t7.setText(MultiEncoderActionStrings[(unsigned int)DisplayCurrentAction3]);
      else
        p0t7.setText(EncoderActionStrings[(unsigned int)DisplayCurrentAction3]);
    }
  }
#endif
}

void RedrawEncoderString4(void)
{
#ifndef NODISPLAY                               // if defined, no action

  if ( GDisplayPage == eFrontPage)
  {
//
// first get the colour correct:
//    
    if (Enc4DisplayLower)
      p0t4.Set_font_color_pco(NEXRED);
    else
      p0t4.Set_font_color_pco(NEXBLUE);
//
// now display the string
//
    if (Enc4DisplayLower)
      p0t4.setText(EncoderActionStrings[(unsigned int)DisplayCurrentActionLower4]);
    else
    {
      if (Enc4IsMulti)
        p0t4.setText(MultiEncoderActionStrings[(unsigned int)DisplayCurrentAction4]);
      else
        p0t4.setText(EncoderActionStrings[(unsigned int)DisplayCurrentAction4]);
    }
  }
#endif
}


//
// set meter background
//
void DisplaySetMeterBackground(void)
{
#ifndef NODISPLAY                               // if defined, no action
  
  int Angle;
  
  if(GDisplayPage == eFrontPage)                // redraw main page, if displayed
  {
    if (DisplayTXState)
      p0z0.Set_background_crop_picc(2);
    else
      p0z0.Set_background_crop_picc(1);
//
// now set the meter angle as it will be different
//
    if (DisplayTXState)
      Angle = DisplayCurrentPowerReading;
    else
      Angle = DisplayCurrentSReading;
    p0z0.setValue(Angle);
  }
#endif
}



////////////////////////////////////////////////////////////////////////////////////////////////////
//
// touch event handlers: PAGE change
//

//
// page 0 - update all controls to current variables
// this is a "postinitialise" callback - preinitialise didn't seem to be invoked from a page0.show() call
//
void page0mainPushCallback(void *ptr)             // called when page 0 loads (main page)
{
#ifndef NODISPLAY                               // if defined, no action

// begin by setting screen number then request screen size
  
  GDisplayPage = eFrontPage;
  
  if (DisplayABState)
    p0t0.setText("A");  
  else
    p0t0.setText("B");  
  
  p0t2.setText(DisplayCurrentFrequency);
  p0t8.setText(ModeStrings[DisplayCurrentMode]);

  RedrawVFOStatusBox();
  RedrawRXStatusBox();
  RedrawRIT(DisplayRITState);

//  p0z0.setValue(DisplayCurrentSReading);                // this is set by the setmeterbackground() call

  if (GBottomEncoderStrings)
  {
    RedrawEncoderString1();
    RedrawEncoderString2();
    RedrawEncoderString3();
  }
  if (GSideEncoderStrings)
    RedrawEncoderString4();

  DisplaySetMeterBackground();
  RedrawFilterPassband();    
#endif
}

void page1PushCallback(void *ptr)             // called when page 1 loads (I/O test page)
{
#ifndef NODISPLAY                               // if defined, no action
  GDisplayPage = eIOTestPage;
  ClearLEDs();
#endif
}

void page2PushCallback(void *ptr)             // called when page 2 loads (about page)
{
#ifndef NODISPLAY                               // if defined, no action
  GDisplayPage = eAboutPage;
  p2t5.setText("Arduino s/w v0.3");
#endif
}

void page3PushCallback(void *ptr)             // called when page 3 loads (frequency entry page)
{
#ifndef NODISPLAY                               // if defined, no action
  GDisplayPage = eFreqEntryPage;
#endif
}



void page4PushCallback(void *ptr)             // called when page 4 loads (band page)
{
#ifndef NODISPLAY                               // if defined, no action
  GDisplayPage = eBandPage;                   // set the new page number
  CATRequestBand();
#endif
}



void page5PushCallback(void *ptr)             // called when page 5 loads (mode page)
{
#ifndef NODISPLAY                               // if defined, no action
  GDisplayPage = eModePage;
  switch(DisplayCurrentMode)                         // now set 1 button to current mode
  {
    case eLSB: p5bt0.setValue(1); break;      // set button
    case eUSB: p5bt1.setValue(1); break;      // set button
    case eDSB: p5bt2.setValue(1); break;      // set button
    case eCWL: p5bt3.setValue(1); break;      // set button
    case eCWU: p5bt4.setValue(1); break;      // set button
    case eFM: p5bt5.setValue(1); break;       // set button
    case eAM: p5bt6.setValue(1); break;       // set button
    case eDIGU: p5bt10.setValue(1); break;    // set button
    case eSPEC: p5bt8.setValue(1); break;     // set button
    case eDIGL: p5bt9.setValue(1); break;     // set button
    case eSAM: p5bt7.setValue(1); break;      // set button
    case eDRM: p5bt11.setValue(1); break;     // set button
  }
  p5vamode.setValue((int)DisplayCurrentMode);              // finally set the "which button is pressed" variable
#endif
}

void page6PushCallback(void *ptr)             // called when page 6 loads (noise page)
{
#ifndef NODISPLAY                               // if defined, no action

  GDisplayPage = eNRPage;

  p6bt0.setValue((unsigned int)DisplayCurrentSNBState);     // set SNB initial state
  p6bt1.setValue((unsigned int)DisplayCurrentANFState);     // ANF initial state
  switch(DisplayCurrentNRState)                             // NR initial state
  {
    case 0: p6bt2.setValue(1); break;
    case 1: p6bt3.setValue(1); break;
    case 2: p6bt4.setValue(1); break;
  }
  p6vanr.setValue(DisplayCurrentNRState);                   // set the "which button pressed" variable

  switch(DisplayCurrentNBState)                             // NB initial state
  {
    case 0: p6bt5.setValue(1); break;
    case 1: p6bt6.setValue(1); break;
    case 2: p6bt7.setValue(1); break;
  }
  p6vanb.setValue(DisplayCurrentNBState);                   // set the "which button pressed" variable
#endif
}

void page7PushCallback(void *ptr)             // called when page 7 loads (RF page)
{
#ifndef NODISPLAY                               // if defined, no action

  GDisplayPage = eRFPage;

  switch(DisplayCurrentAtten)                          // attenuation initial state
  {
    case 0: p7bt0.setValue(1); break;
    case 1: p7bt1.setValue(1); break;
    case 2: p7bt2.setValue(1); break;
    case 3: p7bt3.setValue(1); break;
  }
  p7vaatten.setValue((int)DisplayCurrentAtten);                  // set the "which button pressed" variable

  switch(DisplayCurrentAGCSpeed)                            // AGC speed initial state
  {
    case 0: p7bt4.setValue(1); break;
    case 1: p7bt5.setValue(1); break;
    case 2: p7bt6.setValue(1); break;
    case 3: p7bt7.setValue(1); break;
    case 4: p7bt8.setValue(1); break;
  }
  p7vaagc.setValue((int)DisplayCurrentAGCSpeed);                // set the "which button pressed" variable
  CATRequestAGCThreshold();

//  p7h0.setValue(DisplayCurrentAGCThreshold);               // set slider
#endif
}

//
// when page 8 loads, set the initial state of its buttons
//
void page8PushCallback(void *ptr)             // called when page 8 loads
{
#ifndef NODISPLAY                               // if defined, no action

  char NumStr[5];                             // short string
  GDisplayPage = eSettingsPage;

  switch(GUSBBaudRate)
  {
    case eBaud9600: p8bt0.setValue(1); break;
    case eBaud38400: p8bt1.setValue(1); break;
    case eBaud115200: p8bt2.setValue(1); break;
  }
  
  switch(GEncoderOperation)
  {
    case eDualFnClick: p8bt4.setValue(1); break;
    case eDualFnPress: p8bt5.setValue(1); break;
  }
//
// set the bottom and side encoder legend controls
//
  p8bt3.setValue((uint32_t)GBottomEncoderStrings);
  p8bt6.setValue((uint32_t)GSideEncoderStrings);

//
// set the encoder divisor strings
//
  itoa(GEncoderDivisor, NumStr, 10);
  p8t4.setText(NumStr);
  itoa(GVFOEncoderDivisor, NumStr, 10);
  p8t7.setText(NumStr);
#endif
}

//
// when page 9 is first displayed: we are on control 0.
// set the control number and show its initial function.
//
void page9PushCallback(void *ptr)             // called when page 9 loads
{
#ifndef NODISPLAY                               // if defined, no action

  GDisplayPage = eConfigurePage;
  Page9GetActions();                            // get the current assigned actions
  Page9SetControls();                           // update the text boxes
#endif
}



//
// when page 10 is first displayed: a serial connection has been established
// move to page 0.
//
void page0PushCallback(void *ptr)             // called when page 0 loads (splash page)
{
#ifndef NODISPLAY                               // if defined, no action
  GDisplayPage = eSplashPage;
#endif
}



////////////////////////////////////////////////////////////////////////////////////////////////////
//
// touch event handlers: Page 1
//
void p1bt0PushCallback(void *ptr)             // I/O test LED1
{
#ifndef NODISPLAY                               // if defined, no action
  uint32_t LEDState;
  p1bt0.getValue(&LEDState);                // read the button state
  SetLED(0, (LEDState != 0));
#endif
}

void p1bt1PushCallback(void *ptr)             // I/O test LED2
{
#ifndef NODISPLAY                               // if defined, no action
  uint32_t LEDState;
  p1bt1.getValue(&LEDState);                // read the button state
  SetLED(1, (LEDState != 0));
#endif
}

void p1bt2PushCallback(void *ptr)             // I/O test LED3
{
#ifndef NODISPLAY                               // if defined, no action
  uint32_t LEDState;
  p1bt2.getValue(&LEDState);                // read the button state
  SetLED(2, (LEDState != 0));
#endif
}

void p1bt3PushCallback(void *ptr)             // I/O test LED4
{
#ifndef NODISPLAY                               // if defined, no action
  uint32_t LEDState;
  p1bt3.getValue(&LEDState);                // read the button state
  SetLED(3, (LEDState != 0));
#endif
}

void p1bt4PushCallback(void *ptr)             // I/O test LED5
{
#ifndef NODISPLAY                               // if defined, no action
  uint32_t LEDState;
  p1bt4.getValue(&LEDState);                // read the button state
  SetLED(4, (LEDState != 0));
#endif
}

void p1bt5PushCallback(void *ptr)             // I/O test LED6
{
#ifndef NODISPLAY                               // if defined, no action
  uint32_t LEDState;
  p1bt5.getValue(&LEDState);                // read the button state
  SetLED(5, (LEDState != 0));
#endif
}

void p1bt6PushCallback(void *ptr)             // I/O test LED7
{
#ifndef NODISPLAY                               // if defined, no action
  uint32_t LEDState;
  p1bt6.getValue(&LEDState);                // read the button state
  SetLED(6, (LEDState != 0));
#endif
}


////////////////////////////////////////////////////////////////////////////////////////////////////
//
// touch event handlers: Page 2
//
void p2b1PushCallback(void *ptr)             // encoder configure button
{
#ifndef NODISPLAY                               // if defined, no action
  GControlType = eEncoders;                   // type of control being edited = encoders
  GControlNumber = 0;                         // control being edited = first encoder
#endif
}

void p2b2PushCallback(void *ptr)              // pushbutton configure button
{
#ifndef NODISPLAY                               // if defined, no action
  GControlType = ePushbuttons;                // type of control being edited = pushbuttons
  GControlNumber = 0;                         // control being edited = first pushbutton
#endif
}

void p2b3PushCallback(void *ptr)             // indicator configure button
{
#ifndef NODISPLAY                               // if defined, no action
  GControlType = eIndicators;                 // type of control being edited = indicators
  GControlNumber = 0;                         // control being edited = first indicator
#endif
}

void p2b5PushCallback(void *ptr)             // "save settings" button
{
#ifndef NODISPLAY                               // if defined, no action
  CopySettingsToFlash();
#endif
}


////////////////////////////////////////////////////////////////////////////////////////////////////
//
// touch event handlers: Page 3 (freq entry)
//
void p3b1PushCallback(void *ptr)              // set frequency button
{
#ifndef NODISPLAY                               // if defined, no action
  char NewFreq[20];
  memset(NewFreq, 0, sizeof(NewFreq));
  p3t2.getText(NewFreq, sizeof(NewFreq));
  CATSetFrequency(NewFreq); 
#endif
}



////////////////////////////////////////////////////////////////////////////////////////////////////
//
// touch event handlers: Page 4 (band)
//
void p4bt0PushCallback(void *ptr)             // all band buttons
{
#ifndef NODISPLAY                               // if defined, no action
  uint32_t BandValue;
  p4vaband.getValue(&BandValue);
  CATSetBand((EBand)BandValue);
#endif
}



////////////////////////////////////////////////////////////////////////////////////////////////////
//
// touch event handlers: Page 5 (mode)
//
void p5bt0PushCallback(void *ptr)             // all mode buttons
{
#ifndef NODISPLAY                               // if defined, no action
  uint32_t ModeValue;
  p5vamode.getValue(&ModeValue);
  CATSetMode((EMode)ModeValue);
  DisplayCurrentMode = (EMode)ModeValue;
#endif
}



////////////////////////////////////////////////////////////////////////////////////////////////////
//
// touch event handlers: Page 6 (Noise)
//
void p6bt0PushCallback(void *ptr)             // SNB button
{
#ifndef NODISPLAY                               // if defined, no action
  uint32_t SNBValue;
  p6bt0.getValue(&SNBValue);
  CATSetSNBState((bool)SNBValue);
  DisplayCurrentSNBState = (bool)SNBValue;
#endif
}

void p6bt1PushCallback(void *ptr)             // ANF button
{
#ifndef NODISPLAY                               // if defined, no action
  uint32_t ANFValue;
  p6bt1.getValue(&ANFValue);
  CATSetANFState((bool)ANFValue);
  DisplayCurrentANFState = (bool)ANFValue;
#endif
}


void p6bt2PushCallback(void *ptr)             // NR button
{
#ifndef NODISPLAY                               // if defined, no action
  uint32_t NRValue;
  p6vanr.getValue(&NRValue);
  CATSetNRState((ENRState)NRValue);
  DisplayCurrentNRState = (ENRState)NRValue;
#endif
}


void p6bt5PushCallback(void *ptr)             // NB button
{
#ifndef NODISPLAY                               // if defined, no action
  uint32_t NBValue;
  p6vanb.getValue(&NBValue);
  CATSetNBState((ENBState)NBValue);
  DisplayCurrentNBState = (ENBState)NBValue;
#endif
}



////////////////////////////////////////////////////////////////////////////////////////////////////
//
// touch event handlers: Page 7 (RF)
//
void p7bt0PushCallback(void *ptr)             // atten button
{
#ifndef NODISPLAY                               // if defined, no action
  uint32_t AttenValue;
  p7vaatten.getValue(&AttenValue);
  CATSetAttenuation((EAtten)AttenValue);
  DisplayCurrentAtten = (EAtten)AttenValue;
#endif
}


void p7bt4PushCallback(void *ptr)             // AGC button
{
#ifndef NODISPLAY                               // if defined, no action
  uint32_t AGCValue;
  p7vaagc.getValue(&AGCValue);
  CATSetAGCSpeed((EAGCSpeed)AGCValue);
  DisplayCurrentAGCSpeed = (EAGCSpeed)AGCValue;
#endif
}



//
// there is a 20 unit offset between CAT and slider
// CAT range -20 to +120; slider range 0 to 140
//
void p7h0PopCallback(void *ptr)              // AGC slider
{
#ifndef NODISPLAY                               // if defined, no action
  uint32_t AGCValue;
  p7h0.getValue(&AGCValue);
  CATSetAGCThreshold(AGCValue-20);
#endif
}



////////////////////////////////////////////////////////////////////////////////////////////////////
//
// touch event handlers: Page 8
//
// copy back enumerated variables from the display.
//
void p8bt0PushCallback(void *ptr)             // all baud buttons
{
#ifndef NODISPLAY                               // if defined, no action
  uint32_t BaudValue;
  p8vabaud.getValue(&BaudValue);
  GUSBBaudRate = (EBaudRates)BaudValue;
#endif
}

void p8bt4PushCallback(void *ptr)             // all encoder buttons
{
#ifndef NODISPLAY                               // if defined, no action
  uint32_t EncoderValue;
  p8vaenc.getValue(&EncoderValue);
  GEncoderOperation = (EDualFnEncoders)EncoderValue;
#endif
}

void p8bt3PushCallback(void *ptr)             // bottom encoder string button
{
#ifndef NODISPLAY                               // if defined, no action
  uint32_t BottomStringValue;
  p8bt3.getValue(&BottomStringValue);
  GBottomEncoderStrings = (bool) BottomStringValue;
#endif
}

void p8bt6PushCallback(void *ptr)             // side encoder string button
{
#ifndef NODISPLAY                               // if defined, no action
  uint32_t SideStringValue;
  p8bt6.getValue(&SideStringValue);
  GSideEncoderStrings = (bool) SideStringValue;
#endif
}

void p8b1PushCallback(void* ptr)              // encoder minus button
{
#ifndef NODISPLAY                               // if defined, no action
  char DivisorStr[10];
  int Divisor;
  
  memset(DivisorStr, 0, sizeof(DivisorStr));
  p8t4.getText(DivisorStr, sizeof(DivisorStr));
  Divisor=atoi(DivisorStr);
  if (Divisor > 1)
    Divisor--;
  itoa(Divisor, DivisorStr, 10);
  p8t4.setText(DivisorStr);
  GEncoderDivisor = Divisor;
#endif
}

void p8b2PushCallback(void* ptr)              // encoder plus button
{
#ifndef NODISPLAY                               // if defined, no action
  char DivisorStr[10];
  int Divisor;
  
  memset(DivisorStr, 0, sizeof(DivisorStr));
  p8t4.getText(DivisorStr, sizeof(DivisorStr));
  Divisor=atoi(DivisorStr);
  if (Divisor < 8)
    Divisor++;
  itoa(Divisor, DivisorStr, 10);
  p8t4.setText(DivisorStr);
  GEncoderDivisor = Divisor;
#endif
}

void p8b3PushCallback(void* ptr)              // VFO encoder minus button
{
#ifndef NODISPLAY                               // if defined, no action

  char DivisorStr[10];
  int Divisor;
  
  memset(DivisorStr, 0, sizeof(DivisorStr));
  p8t7.getText(DivisorStr, sizeof(DivisorStr));
  Divisor=atoi(DivisorStr);
  if (Divisor > 1)
    Divisor--;
  itoa(Divisor, DivisorStr, 10);
  p8t7.setText(DivisorStr);
  GVFOEncoderDivisor = Divisor;
#endif
}

void p8b4PushCallback(void* ptr)              // VFO encoder plus button
{
#ifndef NODISPLAY                               // if defined, no action
  char DivisorStr[10];
  int Divisor;
  
  memset(DivisorStr, 0, sizeof(DivisorStr));
  p8t7.getText(DivisorStr, sizeof(DivisorStr));
  Divisor=atoi(DivisorStr);
  if (Divisor < 8)
    Divisor++;
  itoa(Divisor, DivisorStr, 10);
  p8t7.setText(DivisorStr);
  GVFOEncoderDivisor = Divisor;
#endif
}


////////////////////////////////////////////////////////////////////////////////////////////////////
//
// touch event handlers: Page 9


//
// Control -:
// decrement encoder/button/LED number, and repopulate the number
// then find the current assigned action & display
//
void p9b1PushCallback(void *ptr)             // device number -
{
#ifndef NODISPLAY                               // if defined, no action
  if (GControlNumber > 0)
    GControlNumber--;
  Page9GetActions();                            // get the current assigned actions
  Page9SetControls();                           // update the text boxes
#endif
}

//
// Control +:
// increment encoder/button/LED number, and repopulate the number
// then find the current assigned action & display
//
void p9b2PushCallback(void *ptr)             // device number +
{
#ifndef NODISPLAY                               // if defined, no action
  unsigned int MaxControlNum;
  MaxControlNum = Page9GetMaxControlCount();          // find out the max number we are allowed
  if (GControlNumber < MaxControlNum)
    GControlNumber++;
  Page9GetActions();                            // get the current assigned actions
  Page9SetControls();                           // update the text boxes
#endif
}

//
// Action -:
// decrement action number & redisplay
//
void p9b3PushCallback(void *ptr)             // function -
{
#ifndef NODISPLAY                               // if defined, no action
  if (GActionNumber > 0)
    GActionNumber--;
  Page9SetControls();                           // update the text boxes
#endif
}

//
// Action +:
// increment action number & redisplay
//
void p9b4PushCallback(void *ptr)             // function +
{
#ifndef NODISPLAY                               // if defined, no action
  unsigned int MaxActionNum;
  MaxActionNum = Page9GetMaxActionCount();          // find out the max number we are allowed
  if (GActionNumber < MaxActionNum)
    GActionNumber++;
  Page9SetControls();                           // update the text boxes
#endif
}


//
// save the currently displayed setting back to the stored configuration data
// this can then be written to Flash.
//
void p9b7PushCallback(void *ptr)             // Set/Accept
{
#ifndef NODISPLAY                               // if defined, no action
  switch(GControlType)                        // now find the initial actions
  {
    case eEncoders:
      SetEncoderReversed(GControlNumber, GDirectionSetting);
      SetEncoderAction(GControlNumber, (EEncoderActions)GActionNumber);      // set current programmed main & 2nd actions
      break;
    case ePushbuttons:
      SetButtonAction(GControlNumber, (EButtonActions)GActionNumber);
      break;
    case eIndicators:
      SetIndicatorAction(GControlNumber, (EIndicatorActions)GActionNumber);
      break;
  }
#endif
}

//
// push direction button handler:
//
void p9bt0PushCallback(void *ptr)               // "direction" press
{
#ifndef NODISPLAY                               // if defined, no action
  uint32_t DirectionValue;
  p9bt0.getValue(&DirectionValue);
  GDirectionSetting = (bool)DirectionValue;
  Page9SetControls();                           // update the text boxes
#endif
}



//
// display initialise
//
void DisplayInit(void)
{
#ifndef NODISPLAY                               // if defined, no action
//
// set baud rate & register event callback functions
//  
  nexInit(115200);
//  Serial1.begin(115200);                              // baud rate for Nextion display
  page0main.attachPush(page0mainPushCallback);
  page1.attachPush(page1PushCallback);
  page2.attachPush(page2PushCallback);
  page3.attachPush(page3PushCallback);
  page4.attachPush(page4PushCallback);
  page5.attachPush(page5PushCallback);
  page6.attachPush(page6PushCallback);
  page7.attachPush(page7PushCallback);
  page8.attachPush(page8PushCallback);
  page9.attachPush(page9PushCallback);
  page0.attachPush(page0PushCallback);
  p1bt0.attachPush(p1bt0PushCallback);
  p1bt1.attachPush(p1bt1PushCallback);
  p1bt2.attachPush(p1bt2PushCallback);
  p1bt3.attachPush(p1bt3PushCallback);
  p1bt4.attachPush(p1bt4PushCallback);
  p1bt5.attachPush(p1bt5PushCallback);
  p1bt6.attachPush(p1bt6PushCallback);
  p2b1.attachPush(p2b1PushCallback);
  p2b2.attachPush(p2b2PushCallback);
  p2b3.attachPush(p2b3PushCallback);
  p2b5.attachPush(p2b5PushCallback);
  p3b1.attachPush(p3b1PushCallback);
  p4bt0.attachPush(p4bt0PushCallback);
  p5bt0.attachPush(p5bt0PushCallback);
  p6bt0.attachPush(p6bt0PushCallback);
  p6bt1.attachPush(p6bt1PushCallback);
  p6bt2.attachPush(p6bt2PushCallback);
  p6bt5.attachPush(p6bt5PushCallback);
  p7bt0.attachPush(p7bt0PushCallback);
  p7bt4.attachPush(p7bt4PushCallback);
  p7h0.attachPop(p7h0PopCallback);
  p8bt0.attachPush(p8bt0PushCallback);
  p8bt4.attachPush(p8bt4PushCallback);
  p8bt3.attachPush(p8bt3PushCallback);
  p8bt6.attachPush(p8bt6PushCallback);
  p8b1.attachPush(p8b1PushCallback);
  p8b2.attachPush(p8b2PushCallback);
  p8b3.attachPush(p8b3PushCallback);
  p8b4.attachPush(p8b4PushCallback);

  p9b1.attachPush(p9b1PushCallback);
  p9b2.attachPush(p9b2PushCallback);
  p9b3.attachPush(p9b3PushCallback);
  p9b4.attachPush(p9b4PushCallback);
  p9b7.attachPush(p9b7PushCallback);
  p9bt0.attachPush(p9bt0PushCallback);
//
// get the initial encoder actions
//
  DisplayCurrentAction1 = GetEncoderAction(0, false);    
  DisplayCurrentActionLower1 = GetEncoderAction(1, false);    
  if (DisplayCurrentAction1 == eENMulti)
  {
    Enc1IsMulti = true;
    DisplayCurrentAction1 = GMultiAction;
  }
  
  DisplayCurrentAction2 = GetEncoderAction(2, false);    
  DisplayCurrentActionLower2 = GetEncoderAction(3, false);    
  if (DisplayCurrentAction2 == eENMulti)
  {
    Enc2IsMulti = true;
    DisplayCurrentAction2 = GMultiAction;
  }
  
  DisplayCurrentAction3 = GetEncoderAction(4, false);    
  DisplayCurrentActionLower3 = GetEncoderAction(5, false);    
  if (DisplayCurrentAction3 == eENMulti)
  {
    Enc3IsMulti = true;
    DisplayCurrentAction3 = GMultiAction;
  }
  
  DisplayCurrentAction4 = GetEncoderAction(6, false);
  DisplayCurrentActionLower4 = GetEncoderAction(7, false);    
  if (DisplayCurrentAction4 == eENMulti)
  {
    Enc4IsMulti = true;
    DisplayCurrentAction4 = GMultiAction;
  }


//
// find out the screen size by reading the "vasize" variable on page 0
// if we get 0, the display isn't responding yet - wait for an answer
//
  uint32_t SizeValue = 0;                       // read back value - int should be set to "32" for 3.2" display
  delay(100);                                   // let the display initialise
  while (SizeValue == 0)
  {
    p0vasize.getValue(&SizeValue);
    delay(10);                                  // 10ms wait before trying again
  }
  if (SizeValue == 43)
    GIs4_3_Display = true;

//
// tell the display to move to the main page from the splash page
// that will lead to a callback when we redraw the display.
//
  page0main.show();
#endif
}



//
// display tick (currently 10ms)
//
void DisplayTick(void)
{
#ifndef NODISPLAY                               // if defined, no action
//
// handle touch display events
//  
  nexLoop(nex_listen_list);

//
// decrement timers for encoder string displays & redraw if they time out
// if value is 1, then it times out - reset; else just decrement
//
  if (Enc1DisplayLowerCntr != 0)                        // test encoder 1
  {
    if (Enc1DisplayLowerCntr == 1)                      // about to time out
    {
      Enc1DisplayLowerCntr = 0;
      Enc1DisplayLower = false;
      RedrawEncoderString1();
    }
    else
      Enc1DisplayLowerCntr--;                           // just decrement if not expired
  }

  if (Enc2DisplayLowerCntr != 0)                        // test encoder 2
  {
    if (Enc2DisplayLowerCntr == 1)                      // about to time out
    {
      Enc2DisplayLowerCntr = 0;
      Enc2DisplayLower = false;
      RedrawEncoderString2();
    }
    else
      Enc2DisplayLowerCntr--;                           // just decrement if not expired
  }

  if (Enc3DisplayLowerCntr != 0)                        // test encoder 3
  {
    if (Enc3DisplayLowerCntr == 1)                      // about to time out
    {
      Enc3DisplayLowerCntr = 0;
      Enc3DisplayLower = false;
      RedrawEncoderString3();
    }
    else
      Enc3DisplayLowerCntr--;                           // just decrement if not expired
  }

  if (Enc4DisplayLowerCntr != 0)                        // test encoder 4
  {
    if (Enc4DisplayLowerCntr == 1)                      // about to time out
    {
      Enc4DisplayLowerCntr = 0;
      Enc4DisplayLower = false;
      RedrawEncoderString4();
    }
    else
      Enc4DisplayLowerCntr--;                           // just decrement if not expired
  }
#endif
}




#define TXTBUFSIZE 10
#define CMDBUFSIZE 20

//
// display encoder handler
// encoder number 0-7 (normal) 8 (VFO)
//
void DisplayEncoderHandler(unsigned int Encoder, int Count)
{
#ifndef NODISPLAY                               // if defined, no action
  char TxtBuffer[TXTBUFSIZE];                                   // text representation of count vlaue
  if (GDisplayPage == eIOTestPage)
  {
     memset(TxtBuffer, 0, TXTBUFSIZE);
     sprintf(TxtBuffer, "%d", Count);
     switch(Encoder)
     {
       case 0: p1tEnc1.setText(TxtBuffer); break;
       case 1: p1tEnc2.setText(TxtBuffer); break;
       case 2: p1tEnc3.setText(TxtBuffer); break;
       case 3: p1tEnc4.setText(TxtBuffer); break;
       case 4: p1tEnc5.setText(TxtBuffer); break;
       case 5: p1tEnc6.setText(TxtBuffer); break;
       case 6: p1tEnc7.setText(TxtBuffer); break;
       case 7: p1tEnc8.setText(TxtBuffer); break;
       case 8: p1tEnc9.setText(TxtBuffer); break;
     }
  }
#endif
}



//
// display button handler, 
// button = 0-20
// when in I/O test page, light up a text box by changing its background colour
// 
//
void DisplayButtonHandler(unsigned int Button, bool IsPressed)
{
#ifndef NODISPLAY                               // if defined, no action
  char Cmd1Buffer[CMDBUFSIZE];
  char Cmd2Buffer[CMDBUFSIZE];
  if (GDisplayPage == eIOTestPage)
  {
//
// set background colour to say pressed or not.
// the Nextion library doesn't support this directly  but the basic mechanism is to make two calls:
// sendCommand("tenc1.bco=GREEN"); 
// sendCommand("ref tenc1");
//
    strcpy(Cmd1Buffer, BtnObjectNames[Button]);
    if (IsPressed)
      strcat(Cmd1Buffer, ".bco=GREEN");
    else
      strcat(Cmd1Buffer, ".bco=WHITE");

    strcpy (Cmd2Buffer, "ref ");
    strcat (Cmd2Buffer, BtnObjectNames[Button]);

    sendCommand(Cmd1Buffer);
    sendCommand(Cmd2Buffer);
  }
#endif
}


//
// display external MOS input hasndler
// when in I/O test psage, light indicator if pressed
//
void DisplayExtMoxHandler(bool IsPressed)
{
#ifndef NODISPLAY                               // if defined, no action
  if (IsPressed)
    p1tpb25.Set_background_color_bco(NEXGREEN);
  else
    p1tpb25.Set_background_color_bco(NEXWHITE);
#endif
}


/////////////////////////////////////////////////////////////////////////////////////
//
// handlers for CAT messages to save data for display
//
void DisplayShowABState(bool IsA)
{
#ifndef NODISPLAY                               // if defined, no action
  if (DisplayABState != IsA)                      // if different from current settings
  {
    DisplayABState = IsA;                         // save new state
    if(GDisplayPage == eFrontPage)                // redraw main page, if displayed
    {
      if (DisplayABState)
        p0t0.setText("A");  
      else
        p0t0.setText("B");  
    }
  }
#endif
}


//
// need to change the background image
//
void DisplayShowTXState(bool IsTX, bool IsTune)
{
#ifndef NODISPLAY                               // if defined, no action
  if (DisplayTXState != IsTX)                      // if different from current settings
  {
    DisplayTXState = IsTX;                                // save new settings
    DisplayTuneState = IsTune;                            // save new settings
    DisplaySetMeterBackground();
    if (IsTune)
    {
      p0t13.setText("TUNE");
      p0t13.Set_background_color_bco(NEXRED);
      p0t13.Set_font_color_pco(NEXWHITE);
    }
    else if (IsTX)
    {
      p0t13.setText("TX");
      p0t13.Set_background_color_bco(NEXRED);
      p0t13.Set_font_color_pco(NEXWHITE);
    }
    else
    {
      p0t13.setText("RX");
      p0t13.Set_background_color_bco(NEXGREEN);
      p0t13.Set_font_color_pco(NEXBLACK);
    }
  }
#endif
}


void DisplayShowRITState(bool IsRIT)
{
#ifndef NODISPLAY                               // if defined, no action
  if (DisplayRITState != IsRIT)                // if different from current settings
  {
    DisplayRITState = IsRIT;                     // save new settings
    if(GDisplayPage == eFrontPage)                      // redraw main page, if displayed
      RedrawRIT(DisplayRITState);
  }
#endif
}



void DisplayShowLockState(bool IsLock)
{
#ifndef NODISPLAY                               // if defined, no action
  if (DisplayCurrentLockState != IsLock)                // if different from current settings
  {
    DisplayCurrentLockState = IsLock;                     // save new settings
    RedrawVFOStatusBox();
  }
#endif
}


void DisplayShowSplit(bool IsSplit)
{
#ifndef NODISPLAY                               // if defined, no action
  if (DisplayCurrentSplitState != IsSplit)              // if different from current settings
  {
    DisplayCurrentSplitState = IsSplit;                   // save new settings
    RedrawVFOStatusBox();
  }
#endif
}


//
// there is a unit conversion in here
// parameter passed = number from CAT (-140dBm + 0.5N, covering range -140 to -10dBm)
// stored value = gauge angle, 34 to 146 degrees
// to convert: angle = 34 + 0.8N
//
void DisplayShowSMeter(unsigned int Reading)
{
#ifndef NODISPLAY                               // if defined, no action
  unsigned int Angle;                                   // new pointer angle
  Angle = (Reading << 1);
  Angle = (Angle / 5) + VDISPLAYMINANGLE;               // 4N/5+34
  if (DisplayCurrentSReading != Angle)                  // if different from current settings
    if(GDisplayPage == eFrontPage)                      // redraw main page, if displayed
      if (DisplayTXState == false)                      // if RX
        p0z0.setValue(Angle);
  DisplayCurrentSReading = Angle;                       // save new settings
#endif
}



//
// there is a unit conversion in here ONLY APPROXIMATED
// parameter passed = number from CAT (Watts)
// stored value = gauge angle, 34 to 146 degrees
// crude bodge - to convert: angle = 34+1.1n
//
void DisplayShowTXPower(unsigned int Reading)
{
#ifndef NODISPLAY                               // if defined, no action
  unsigned int Angle;                                   // new pointer angle
  Angle = (Reading + Reading/10) + VDISPLAYMINANGLE;
  if (DisplayCurrentPowerReading != Angle)              // if different from current settings
    if(GDisplayPage == eFrontPage)                      // redraw main page, if displayed
      if (DisplayTXState)                               // if TX
        p0z0.setValue(Angle);
  DisplayCurrentPowerReading = Angle;                   // save new settings
#endif
}


//
// display set paramters that are VFO A/B or RX 1/2 dependent.
// the algorithm is more complex because whether the data is displayed 
// depends on the current A/B setting.
//
void DisplayShowFrequency(char* Frequency)         // string with MHz as ASCII
{
#ifndef NODISPLAY                               // if defined, no action
  if (strcmp(DisplayCurrentFrequency, Frequency) != 0)
  {
    strcpy(DisplayCurrentFrequency, Frequency);          // copy new to A
    if(GDisplayPage == eFrontPage)                      // redraw main page, if displayed
      p0t2.setText(Frequency);
  }
#endif
}


//
// set current band
// if correct window, clear the current button band then set the new one
//
void DisplayShowBand(EBand Band)
{
#ifndef NODISPLAY                               // if defined, no action
  uint32_t BandValue;
  
  if (GDisplayPage == eBandPage)
  {
    p4vaband.getValue(&BandValue);                  // find clicked button (if any)
    switch(BandValue)                               // clear clicked button
    {
      case 0: p4bt0.setValue(0); break;       // set button
      case 1: p4bt1.setValue(0); break;       // set button
      case 2: p4bt2.setValue(0); break;       // set button
      case 3: p4bt3.setValue(0); break;       // set button
      case 4: p4bt4.setValue(0); break;       // set button
      case 5: p4bt5.setValue(0); break;       // set button
      case 6: p4bt6.setValue(0); break;       // set button
      case 7: p4bt7.setValue(0); break;       // set button
      case 8: p4bt8.setValue(0); break;       // set button
      case 9: p4bt9.setValue(0); break;       // set button
      case 10: p4bt10.setValue(0); break;     // set button
      case 11: p4bt11.setValue(0); break;     // set button
    }

    switch(Band)                         // now set 1 button to current band
    {
      case e160: p4bt0.setValue(1); break;      // set button
      case e80: p4bt1.setValue(1); break;       // set button
      case e60: p4bt2.setValue(1); break;       // set button
      case e40: p4bt3.setValue(1); break;       // set button
      case e30: p4bt4.setValue(1); break;       // set button
      case e20: p4bt5.setValue(1); break;       // set button
      case e17: p4bt6.setValue(1); break;       // set button
      case e15: p4bt7.setValue(1); break;       // set button
      case e12: p4bt8.setValue(1); break;       // set button
      case e10: p4bt9.setValue(1); break;       // set button
      case e6: p4bt10.setValue(1); break;       // set button
      case eGen: p4bt11.setValue(1); break;     // set button
    }
    p4vaband.setValue(Band);                    // set button number variable
  }
#endif
}


void DisplayShowMode(EMode Mode)
{
#ifndef NODISPLAY                               // if defined, no action
  uint32_t ModeValue;

  if (DisplayCurrentMode != Mode)
  {
    DisplayCurrentMode = Mode;                          // copy new to A
    GValidFilterLow = false;                            // invalidate the filter values
    GValidFilterHigh = false;                           // invalidate the filter values
    RedrawFilterPassband();
    if(GDisplayPage == eFrontPage)                      // redraw main page, if displayed
      p0t8.setText(ModeStrings[DisplayCurrentMode]);

//
// if the mode page is open: clear currently active button then
// set new button and clicked button variable
//
    else if (GDisplayPage == eModePage)
    {
      p5vamode.getValue(&ModeValue);
      switch(ModeValue)                         // first clear the set button
      {
        case 0: p5bt0.setValue(0); break;      // unset button
        case 1: p5bt1.setValue(0); break;      // unset button
        case 2: p5bt2.setValue(0); break;      // unset button
        case 3: p5bt3.setValue(0); break;      // unset button
        case 4: p5bt4.setValue(0); break;      // unset button
        case 5: p5bt5.setValue(0); break;      // unset button
        case 6: p5bt6.setValue(0); break;      // unset button
        case 7: p5bt10.setValue(0); break;     // unset button
        case 8: p5bt8.setValue(0); break;      // unset button
        case 9: p5bt9.setValue(0); break;      // unset button
        case 10: p5bt7.setValue(0); break;     // unset button
        case 11: p5bt11.setValue(0); break;    // unset button
      }
  
      switch(DisplayCurrentMode)                         // now set 1 button to current mode
      {
        case eLSB: p5bt0.setValue(1); break;      // set button
        case eUSB: p5bt1.setValue(1); break;      // set button
        case eDSB: p5bt2.setValue(1); break;      // set button
        case eCWL: p5bt3.setValue(1); break;      // set button
        case eCWU: p5bt4.setValue(1); break;      // set button
        case eFM: p5bt5.setValue(1); break;       // set button
        case eAM: p5bt6.setValue(1); break;       // set button
        case eDIGU: p5bt10.setValue(1); break;    // set button
        case eSPEC: p5bt8.setValue(1); break;     // set button
        case eDIGL: p5bt9.setValue(1); break;     // set button
        case eSAM: p5bt7.setValue(1); break;      // set button
        case eDRM: p5bt11.setValue(1); break;     // set button
      }
      p5vamode.setValue((int)DisplayCurrentMode);              // finally set the "which button is pressed" variable
    }
  }
#endif
}



//
// set current NR state
//
void DisplayShowNRState(ENRState State)
{
#ifndef NODISPLAY                               // if defined, no action
  uint32_t NRValue;
  if(DisplayCurrentNRState != State)                        // if data different from current
  {
    DisplayCurrentNRState = State;                          // set new data
    RedrawRXStatusBox();                                    // update page0 text if main page visible

    if (GDisplayPage == eNRPage)                            // if noise screen showing
    {
      p6vanr.getValue(&NRValue);                                // read current button, then clear it
      switch(NRValue)                                           // set new NR state
      {
        case 0: p6bt2.setValue(0); break;
        case 1: p6bt3.setValue(0); break;
        case 2: p6bt4.setValue(0); break;
      }
      switch(DisplayCurrentNRState)                             // set new NR state
      {
        case 0: p6bt2.setValue(1); break;
        case 1: p6bt3.setValue(1); break;
        case 2: p6bt4.setValue(1); break;
      }
      p6vanr.setValue(State);                                   // set the "which button pressed" variable
    }
  }
#endif
}


//
// set current NB state
//
void DisplayShowNBState(ENBState State)
{
#ifndef NODISPLAY                               // if defined, no action
  uint32_t NBValue;
  if(DisplayCurrentNBState != State)                        // if data different from current
  {
    DisplayCurrentNBState = State;                          // set new data
    RedrawRXStatusBox();                                    // update page0 text if main page visible
    if (GDisplayPage == eNRPage)                            // if noise screen showing
    {
      p6vanb.getValue(&NBValue);                                // read current button, thne clear it
      switch(NBValue)                                           // set new NB state
      {
        case 0: p6bt5.setValue(0); break;
        case 1: p6bt6.setValue(0); break;
        case 2: p6bt7.setValue(0); break;
      }
    
      DisplayCurrentNBState = State;
      switch(DisplayCurrentNBState)                             // set new NB state
      {
        case 0: p6bt5.setValue(1); break;
        case 1: p6bt6.setValue(1); break;
        case 2: p6bt7.setValue(1); break;
      }
      p6vanb.setValue(State);                                   // set the "which button pressed" variable
    }
  }
#endif
}

//
// show SNB
//
void DisplayShowSNBState(bool SNBState)
{
#ifndef NODISPLAY                               // if defined, no action
  if(DisplayCurrentSNBState != SNBState)
  {
    DisplayCurrentSNBState = SNBState;
    RedrawRXStatusBox();                                    // update page0 text if main page visible
    if (GDisplayPage == eNRPage)                            // if noise screen showing
    {
      p6bt0.setValue((unsigned int)DisplayCurrentSNBState);     // set SNB initial state  
    }
  }
#endif
}

//
// currently this will NOT update the display if the "noise" page is already open
//
void DisplayShowANFState(bool ANFState)
{
#ifndef NODISPLAY                               // if defined, no action
  if(DisplayCurrentANFState != ANFState)
  {
    DisplayCurrentANFState = ANFState;
    RedrawRXStatusBox();                                    // update page0 text if main page visible
    if (GDisplayPage == eNRPage)                            // if noise screen showing
    {
      p6bt1.setValue((unsigned int)DisplayCurrentANFState);     // ANF initial state
    }
  }
#endif
}



//
// update AGC speed, and controls if the window is opwn
//
void DisplayShowAGCSpeed(EAGCSpeed Speed)
{
#ifndef NODISPLAY                               // if defined, no action
  uint32_t AGCValue;
  if (DisplayCurrentAGCSpeed != Speed)
  {
    DisplayCurrentAGCSpeed = Speed;                             // copy new value
    if(GDisplayPage == eRFPage)                                 // update control if on the page
    {
      p7vaagc.getValue(&AGCValue);                              // read the current pressed button
      switch(AGCValue)                                          // clear the current pressed button
      {
        case 0: p7bt4.setValue(0); break;
        case 1: p7bt5.setValue(0); break;
        case 2: p7bt6.setValue(0); break;
        case 3: p7bt7.setValue(0); break;
        case 4: p7bt8.setValue(0); break;
      }
      // now set new button
      switch(DisplayCurrentAGCSpeed)                            // AGC speed initial state
      {
        case 0: p7bt4.setValue(1); break;
        case 1: p7bt5.setValue(1); break;
        case 2: p7bt6.setValue(1); break;
        case 3: p7bt7.setValue(1); break;
        case 4: p7bt8.setValue(1); break;
      }
      p7vaagc.setValue(DisplayCurrentAGCSpeed);                 // set the "current pressed button"
    }
  }
#endif
}


//
// update the display if the "RF" page is already open
// note we don't store the AGC threshold value locally because it isn't routinely polled.
// note the range: CAT value -20 to 120; slide 0 to 140
//
void DisplayShowAGCThreshold(int Threshold)
{
#ifndef NODISPLAY                               // if defined, no action
  if(GDisplayPage == eRFPage)                           // update controls if on the screen
    p7h0.setValue(Threshold+20);               // set slider
#endif
}


//
// update the local atten variable, and control if the RF page is open
//
void DisplayShowAtten(EAtten Attenuation)
{
#ifndef NODISPLAY                               // if defined, no action
  uint32_t AttenValue;

  if(DisplayCurrentAtten != Attenuation)                  // update if changed
  {
    DisplayCurrentAtten = Attenuation;
    RedrawRXStatusBox();                                    // update page0 text if main page visible
    if(GDisplayPage == eRFPage)                           // update controls if on the screen
    {
      p7vaatten.getValue(&AttenValue);                    // read the current pressed control
      switch(AttenValue)                                  // clear the current pressed control
      {
        case 0: p7bt0.setValue(0); break;
        case 1: p7bt1.setValue(0); break;
        case 2: p7bt2.setValue(0); break;
        case 3: p7bt3.setValue(0); break;
      }
// now set the new button state
      switch(DisplayCurrentAtten)                          // attenuation initial state
      {
        case 0: p7bt0.setValue(1); break;
        case 1: p7bt1.setValue(1); break;
        case 2: p7bt2.setValue(1); break;
        case 3: p7bt3.setValue(1); break;
      }
      p7vaatten.setValue(DisplayCurrentAtten);             // set the "current pressed control" variable
    }
  }
#endif
}



//
// set filter low value.
// if we have a new value, redraw the passband
//
void DisplayShowFilterLow(int Freq)
{
#ifndef NODISPLAY                               // if defined, no action
  GValidFilterLow = true;
  if (Freq != DisplayCurrentFilterLow)
  {
    DisplayCurrentFilterLow = Freq;
    RedrawFilterPassband();
  }
#endif
}




//
// set filter high value
// if we have a new value, redraw the passband
//
void DisplayShowFilterHigh(int Freq)
{
#ifndef NODISPLAY                               // if defined, no action
  GValidFilterHigh = true;
  if (Freq != DisplayCurrentFilterHigh)
  {
    DisplayCurrentFilterHigh = Freq;
    RedrawFilterPassband();
  }
#endif
}



//
// set display of encoder actions. Currently we have placeholders to show 0,2,4 & 6
// this is only called for a multifunction encoder, or a single shaft encoder when its "dual function" is enabled
//
void DisplaySetEncoderAction(unsigned int EncoderNumber, EEncoderActions Action, bool IsMulti)         // set to assigned actions
{
#ifndef NODISPLAY                               // if defined, no action
  if (EncoderNumber == 0)                                           // update display position 0
  {
    Enc1IsMulti = IsMulti;
    if (Action != DisplayCurrentAction1)
    {
      DisplayCurrentAction1 = Action;
      RedrawEncoderString1();
    }
  }
  else if (EncoderNumber == 2)                                      // update display position 2
  {
    Enc2IsMulti = IsMulti;
    if (Action != DisplayCurrentAction2)
    {
      DisplayCurrentAction2 = Action;
      RedrawEncoderString2();
    }
  }
  else if (EncoderNumber == 4)                                      // update display position 4
  {
    Enc3IsMulti = IsMulti;
    if (Action != DisplayCurrentAction3)
    {
      DisplayCurrentAction3 = Action;
      RedrawEncoderString3();
    }
  }
  else if (EncoderNumber == 6)                                      // update display position 6
  {
    Enc4IsMulti = IsMulti;
    if (Action != DisplayCurrentAction4)
    {
      DisplayCurrentAction4 = Action;
      RedrawEncoderString4();
    }
  }
#endif
}



//
// note for legend display that an encoder has been turned. Used to determine which encoder should be displayed.
// for each encoder:
//   get the current state;
//   determine the new state;
//   if new state != current state, redraw
//
void DisplayEncoderTurned(unsigned int EncoderNumber)
{
#ifndef NODISPLAY                               // if defined, no action
  bool StringCurrentDisplayState;                      // true if string should display lower encoder(red); false if top encoder (blue)

  switch(EncoderNumber)
  {
    case 0:                                                      // 1st encoder, upper knob.
      StringCurrentDisplayState = Enc1DisplayLower;
      Enc1DisplayLower = false;
      if (Enc1DisplayLower != StringCurrentDisplayState)
        RedrawEncoderString1();
      break;


    case 1:                                                      // 1st encoder, lower knob.
      StringCurrentDisplayState = Enc1DisplayLower;
      Enc1DisplayLower = true;
      Enc1DisplayLowerCntr = VDISPLAYLOWERFNTICKS;
      if (Enc1DisplayLower != StringCurrentDisplayState)
        RedrawEncoderString1();
      break;

    
    case 2:                                                      // 2nd encoder, upper knob.
      StringCurrentDisplayState = Enc2DisplayLower;
      Enc2DisplayLower = false;
      if (Enc2DisplayLower != StringCurrentDisplayState)
        RedrawEncoderString2();
      break;

    
    case 3:                                                      // 2nd encoder, lower knob.
      StringCurrentDisplayState = Enc2DisplayLower;
      Enc2DisplayLower = true;
      Enc2DisplayLowerCntr = VDISPLAYLOWERFNTICKS;
      if (Enc2DisplayLower != StringCurrentDisplayState)
        RedrawEncoderString2();
      break;

    
    case 4:                                                      // 3rd encoder, upper knob.
      StringCurrentDisplayState = Enc3DisplayLower;
      Enc3DisplayLower = false;
      if (Enc3DisplayLower != StringCurrentDisplayState)
        RedrawEncoderString3();
      break;

    
    case 5:                                                      // 3rd encoder, lower knob.
      StringCurrentDisplayState = Enc3DisplayLower;
      Enc3DisplayLower = true;
      Enc3DisplayLowerCntr = VDISPLAYLOWERFNTICKS;
      if (Enc3DisplayLower != StringCurrentDisplayState)
        RedrawEncoderString3();
      break;

    
    case 6:                                                      // 4th encoder, upper knob.
      StringCurrentDisplayState = Enc4DisplayLower;
      Enc4DisplayLower = false;
      if (Enc4DisplayLower != StringCurrentDisplayState)
        RedrawEncoderString4();
      break;

    
    case 7:                                                      // 4th encoder, lower knob.
      StringCurrentDisplayState = Enc4DisplayLower;
      Enc4DisplayLower = true;
      Enc4DisplayLowerCntr = VDISPLAYLOWERFNTICKS;
      if (Enc4DisplayLower != StringCurrentDisplayState)
        RedrawEncoderString4();
      break;
  }
#endif
}
