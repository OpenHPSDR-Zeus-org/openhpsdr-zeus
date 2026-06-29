/////////////////////////////////////////////////////////////////////////
//
// Odin SDR Console by Laurence Barker G8NJJ
//
// this sketch provides a knob and switch interface through USB and CAT
//
// tiger.c
// this file holds the CAT parsing code
// large CAT file = tiger....
/////////////////////////////////////////////////////////////////////////

#include "globalinclude.h"
#include "tiger.h"
#include "display.h"
#include "cathandler.h"
#include "led.h"

//
// input buffer
//
#define CATSERIAL Serial                            // allows easy change to SerialUSB
#define VBUFLENGTH 128
char GCATInputBuffer[VBUFLENGTH];
char* GCATWritePtr;
char Output[40];                                        // TX CAT msg buffer
unsigned int GNumCommands;                              // number of commands in table

//
// lookup initial divisor from number of digits
//
long DivisorTable[] =
{
  0,                                                    // not used
  1,                                                    // 1 digit - already have units
  10,                                                   // 2 digits - tens is first
  100,                                                  // 3 digits - hundreds is 1st
  1000,                                                 // 4 digits - thousands is 1st
  10000,                                                // 5 digits - ten thousands is 1st
  100000,                                               // 6 digits - hundred thousands
  1000000,                                              // millions
  10000000,                                             // 10 millions
  100000000,                                            // 100 millions
  1000000000,                                           // 1000 millions
  10000000000,                                          // 10000 millions
  100000000000,                                         // 100000 millions
  1000000000000,                                        // 1000000 millions
  10000000000000                                        // 10000000 millions
};


//
// array of debug command records. This must exactly match the enum EDebugCATCommands in tiger.h
// and the number of commands defined here must be correct
//
#ifdef SENSORDEBUG
#define VNUMDEBUGCATCMDS 3
SCATCommands GCATDebugCommands[VNUMDEBUGCATCMDS] = 
{
  {"LED+", eNum, 1, 9, 2, false},                         // master AG gain
  {"LED-", eNum, 1, 9, 2, false},                         // RX1 AF gain
  {"IDNT", eNone, 0, 0, 0, false}                         // RX2 AF gain
};
#endif

//
// array of records. This must exactly match the enum ECATCommands in tiger.h
// and the number of commands defined here must be correct
// (not including the final eNoCommand)
#define VNUMCATCMDS 104

SCATCommands GCATCommands[VNUMCATCMDS] = 
{
  {"ZZAG", eNum, 0, 100, 3, false},                         // master AG gain                 1
  {"ZZLA", eNum, 0, 100, 3, false},                         // RX1 AF gain
  {"ZZLE", eNum, 0, 100, 3, false},                         // RX2 AF gain
  {"ZZPA", eNum, 0, 4, 1, false},                           // RX1 attenuation
  {"ZZPB", eNum, 0, 4, 1, false},                           // RX2 attenuation
  {"ZZAR", eNum, -20, 120, 4, false},                       // RX1 AGC threshold
  {"ZZAS", eNum, -20, 120, 4, false},                       // RX2 AGC threshold
  {"ZZGT", eNum, 0, 5, 1, false},                           // RX1 AGC speed
  {"ZZGU", eNum, 0, 5, 1, false},                           // RX2 AGC speed
  {"ZZFL", eNum, -9999, 9999, 5, true},                     // RX1 filter low cut
  
  {"ZZFS", eNum, -9999, 9999, 5, true},                     // RX2 filter low cut             11
  {"ZZFH", eNum, -9999, 9999, 5, true},                     // RX1 filter high cut
  {"ZZFR", eNum, -9999, 9999, 5, true},                     // RX2 filter high cut
  {"ZZPC", eNum, 0, 100, 3, false},                         // TX drive
  {"ZZMG", eNum, -50, 70, 3, false},                        // TX mic gain
  {"ZZFA", eStr, 0, 0, 11, false},                          // set VFO A absolute freq (special case generate - string)
  {"ZZFB", eStr, 0, 0, 11, false},                          // set VFO B absolute freq (special case generate - string)
  {"ZZAE", eNum, 0, 99, 2, false},                          // VFO A down n steps
  {"ZZAF", eNum, 0, 99, 2, false},                          // VFO A up n steps
  {"ZZBE", eNum, 0, 99, 2, false},                          // VFO B down n steps
  
  {"ZZBF", eNum, 0, 99, 2, false},                          // VFO B up n steps               21
  {"ZZVG", eNum, 0, 1000, 4, false},                        // VOX gain
  {"ZZXH", eNum, 0, 2000, 4, false},                        // VOX delay
  {"ZZVE", eBool, 0, 1, 1, false},                          // VOX on/off
  {"ZZCL", eNum, 200, 2250, 4, false},                      // CW sidetone
  {"ZZCS", eNum, 1, 60, 2, false},                          // CW speed
  {"ZZTX", eBool, 0, 1, 1, false},                          // MOX state
  {"ZZTU", eBool, 0, 1, 1, false},                          // TUNE state
  {"ZZBD", eNone, 0, 0, 0, false},                          // RX1 band down
  {"ZZBU", eNone, 0, 0, 0, false},                          // RX1 band up
  
  {"ZZBS", eStr, 0, 999, 3, false},                         // RX1 set/read band (special case parse - transverter may include non numeric values)    31
  {"ZZBA", eNone, 0, 0, 0, false},                          // RX2 band down
  {"ZZBB", eNone, 0, 0, 0, false},                          // RX2 band up
  {"ZZBT", eStr, 0, 999, 3, false},                         // RX2 set/read band (special case parse- transverter may include non numeric values)
  {"ZZMD", eNum, 0, 11, 2, false},                          // RX1 mode
  {"ZZME", eNum, 0, 11, 2, false},                          // RX2 mode
  {"ZZRT", eBool, 0, 1, 1, false},                          // RIT state
  {"ZZRU", eNone, 0, 0, 0, false},                          // RIT step up (ignore "set RIT to NNNNN" variant)
  {"ZZRD", eNone, 0, 0, 0, false},                          // RIT step down (ignore "set RIT to NNNNN" variant)
  {"ZZSP", eBool, 0, 1, 1, false},                          // SPLIT state
  
  {"ZZCN", eBool, 0, 1, 1, false},                          // RX1 click tune                 41
  {"ZZCO", eBool, 0, 1, 1, false},                          // RX2 click tune
  {"ZZUX", eBool, 0, 1, 1, false},                          // VFO A LOCK state
  {"ZZUY", eBool, 0, 1, 1, false},                          // VFO B LOCK state
  {"ZZSM", eNum, 0, 1260, 1, false},                         // display S meter (Special case parse - needs 0 or 1 digit inserting)
  {"ZZRM", eStr, 0, 9999, 1, false},                        // display TX metering (special case parse - response is param dependent)
  {"ZZNR", eBool, 0, 1, 1, false},                          // RX1 NR mode
  {"ZZNS", eBool, 0, 1, 1, false},                          // RX1 enhanced NR mode
  {"ZZNV", eBool, 0, 1, 1, false},                          // RX2 NR mode
  {"ZZNW", eBool, 0, 1, 1, false},                          // RX2 enhanced NR mode

  {"ZZNA", eBool, 0, 1, 1, false},                          // RX1 NB mode                    51
  {"ZZNB", eBool, 0, 1, 1, false},                          // RX1 enhanced NB mode
  {"ZZNC", eBool, 0, 1, 1, false},                          // RX2 NB mode
  {"ZZND", eBool, 0, 1, 1, false},                          // RX2 enhanced NB mode
  {"ZZNN", eBool, 0, 1, 1, false},                          // RX1 SNB
  {"ZZNO", eBool, 0, 1, 1, false},                          // RX2 SNB
  {"ZZNT", eBool, 0, 1, 1, false},                          // RX1 ANF
  {"ZZNU", eBool, 0, 1, 1, false},                          // RX2 ANF
  {"ZZMA", eBool, 0, 1, 1, false},                          // RX1 mute
  {"ZZMB", eBool, 0, 1, 1, false},                          // RX2 mute

  {"ZZPS", eBool, 0, 1, 1, false},                          // radio START                    61
  {"ZZSQ", eNum, 0, 160, 3, false},                         // RX1 squelch level
  {"ZZSX", eNum, 0, 160, 3, false},                         // RX2 squelch level
  {"ZZSO", eBool, 0, 1, 1, false},                          // RX1 squelch on/off
  {"ZZSV", eBool, 0, 1, 1, false},                          // RX2 squelch on/off
  {"ZZVS", eNone, 0, 2, 1, false},                          // VFO swap/copy
  {"ZZXN", eNum, 0, 8191, 4, false},                        // RX1 combined status
  {"ZZXO", eNum, 0, 8191, 4, false},                        // RX2 combined status
  {"ZZXV", eNum, 0, 1023, 4, false},                        // VFO combined status
  {"ZZAC", eNum, 0, 24, 2, false},                          // VFO step size

  {"ZZDB", eBool, 0, 1, 1, false},                          // diversity reference source     71
  {"ZZDC", eNum, 0, 5000, 4, false},                        // diversity RX2 gain
  {"ZZDD", eNum, -18000, 18000, 6, true},                   // diversity phase
  {"ZZDE", eBool, 0, 1, 1, false},                          // diversity enable
  {"ZZDG", eNum, 0, 5000, 4, false},                        // diversity RX1 gain
  {"ZZDH", eNum, 0, 2, 1, false},                           // diversity receiver source
  {"ZZSW", eBool, 0, 1, 1, false},                          // VFO A/B enabled for TX
  {"ZZRX", eNum, 0, 31, 2, false},                          // RX1 step atten
  {"ZZRY", eNum, 0, 31, 2, false},                          // RX2 step atten
  {"ZZCP", eBool, 0, 1, 1, false},                          // compander enable

  {"ZZCT", eNum, 0, 20, 2, false},                          // compander threshold            81
  {"ZZLI", eBool, 0, 1, 1, false},                          // puresignal on/off
  {"ZZUT", eBool, 0, 1, 1, false},                          // puresignal 2 tone test
  {"ZZUS", eNone, 0, 0, 0, false},                          // puresignal single cal
  {"ZZMO", eBool, 0, 1, 1, false},                          // MON on/off
  {"ZZXD", eNone, 0, 0, 0, false},                          // XIT step down (ignore "set RIT to NNNNN" variant)
  {"ZZXU", eNone, 0, 0, 0, false},                          // XIT step up (ignore "set RIT to NNNNN" variant)
  {"ZZXS", eBool, 0, 1, 1, false},                          // XIT on/off state
  {"ZZXC", eNone, 0, 0, 0, false},                          // XIT clear
  {"ZZRC", eNone, 0, 0, 0, false},                          // RIT clear

  {"ZZSY", eBool, 0, 1, 1, false},                          // VFO Sync                       91
  {"ZZFI", eNum, 0, 11, 2, false},                          // RX1 filter select
  {"ZZFJ", eNum, 0, 11, 2, false},                          // RX2 filter select
  {"ZZYR", eBool, 0, 1, 1, false},                          // RX1/RX2 radio button toggle
  {"ZZVA", eBool, 0, 1, 1, false},                          // VAC1 button toggle
  {"ZZVK", eBool, 0, 1, 1, false},                          // VAC2 button toggle
  {"ZZPD", eNone, 0, 0, 0, false},                          // display centre button
  {"ZZPE", eNum, 0, 1000, 4, false},                        // display pan
  {"ZZPY", eNum, 10, 240, 3, false},                        // display zoom
  {"ZZLC", eNum, 0, 100, 3, false},                         // sub-RX AF gain
  
  {"ZZLD", eNum, 0, 100, 3, false},                         // sub-RX stereo balance          101
  {"ZZLB", eNum, 0, 100, 3, false},                         // RX1 stereo balance
  {"ZZLF", eNum, 0, 100, 3, false},                         // RX2 stereo balance
  {"ZZZP", eNum, 0, 999, 3, false}                          // Andromeda front panel keypress
};



// this array holds a 32 bit representation of the CAT command
// to so a compare in a single test
unsigned long GCATMatch[VNUMCATCMDS];


//
// Make32BitStr
// simply a 4 char CAT command in a single 32 bit word for easy compare
//
unsigned long Make32BitStr(char* Input)
{
  unsigned long Result;
  int CharCntr;
  char Ch;
  
  Result = 0;
  for(CharCntr=0; CharCntr < 4; CharCntr++)
  {
    Ch = Input[CharCntr];                                           // input character
    if (isLowerCase(Ch))                                             // force lower case to upper case
      Ch -= 0x20;
    Result = (Result << 8) | Ch;
  }
  return Result;
}

//
// initialise CAT handler
// load up the match strings with either valid commands or debug commands
//
void InitCAT()
{
  int CmdCntr;
  unsigned long MatchWord;
#ifdef SENSORDEBUG
// initialise the matching 32 bit words to hold a version of each debug command
  GNumCommands = VNUMDEBUGCATCMDS;
  for(CmdCntr=0; CmdCntr < VNUMDEBUGCATCMDS; CmdCntr++)
  {
    MatchWord = Make32BitStr(GCATDebugCommands[CmdCntr].CATString);
    GCATMatch[CmdCntr] = MatchWord;
  }
#else
// initialise the matching 32 bit words to hold a version of each CAT command
  GNumCommands = VNUMCATCMDS;
  for(CmdCntr=0; CmdCntr < VNUMCATCMDS; CmdCntr++)
  {
    MatchWord = Make32BitStr(GCATCommands[CmdCntr].CATString);
    GCATMatch[CmdCntr] = MatchWord;
  }
#endif
  GCATWritePtr = GCATInputBuffer;                   // point to start of buffer
}



//
// ScanParseSerial()
// scans input serial stream for characters; parses complete commands
// when it finds one
//
void ScanParseSerial()
{
  int ReadChars;                                  // number of read characters available
  char Ch;
  int Cntr;
  if(CATSERIAL)
  {
    ReadChars = CATSERIAL.available();
    if (ReadChars != 0)
    {
//
// we have input data available, so read it one char at a time and write to buffer
// if we find a terminating semicolon, process the command
//     
      for(Cntr=0; Cntr < ReadChars; Cntr++)
      {
        Ch=CATSERIAL.read();
        *GCATWritePtr++ = Ch;
        if (Ch == ';')
        {
          *GCATWritePtr++ = 0;
#ifdef SENSORDEBUG
          ParseDebugCATCmd();     
#else
          ParseCATCmd();     
#endif
        }
      }
    }
  }
}



//
// helper function
// returns true of the value found would be a legal character in a signed int, including the signs
//
bool isNumeric(char ch)
{
  bool Result = false;

  if (isDigit(ch))
    Result = true;
  else if ((ch == '+') || (ch == '-'))
    Result = true;

  return Result;
}


//
// ParseCATCmd()
// Parse a single command in the local input buffer
// process it if it is a valid command
//
void ParseCATCmd(void)
{
  int CharCnt;                              // number of characters in the buffer (same as length of string)
  unsigned long MatchWord;                  // 32 bit compressed input cmd
  ECATCommands MatchedCAT = eNoCommand;     // CAT command we've matched this to
  int CmdCntr;                              // counts CAT commands
  SCATCommands* StructPtr;                  // pointer to structure with CAT data
  ERXParamType ParsedType;                  // type of parameter actually found
  bool ParsedBool;                          // if a bool expected, it goes here
  int ParsedInt;                            // if int expected, it goes here
  char ParsedString[20];                    // if string expected, it goes here
  int ByteCntr;
  char ch;
  bool ValidResult = true;                  // true if we get a valid parse result

  
  CharCnt = (GCATWritePtr - GCATInputBuffer) - 2;
//
// CharCnt holds the input string length excluding the terminating null and excluding the semicolon
// test minimum length for a valid CAT command: ZZxx; plus terminating 0
//
  if (CharCnt < 4)
    ValidResult = false;
  else
  {
    MatchWord = Make32BitStr(GCATInputBuffer);
    for (CmdCntr=0; CmdCntr < GNumCommands; CmdCntr++)         // loop thro commands we recognise
    {
      if (GCATMatch[CmdCntr] == MatchWord)
      {
        MatchedCAT = (ECATCommands)CmdCntr;                           // if a match, exit loop
        StructPtr = GCATCommands + (int)CmdCntr;
        break;
      }
    }
    if(MatchedCAT == eNoCommand)                                      // if no match was found
      ValidResult = false;
    else
    {
//
// we have recognised a 4 char ZZnn command that is terminated by a semicolon
// now we need to process the parameter bytes (if any) in the middle
// the CAT structs have the required information
// any parameter starts at position 4 and ends at (charcnt-1)
//
      if (CharCnt == 4)
        ParsedType=eNone;
      else
      {
//
// strategy is: first copy just the param to a string
// if required type is not a string, parse to a number
// then if required type is bool, check the value
//        
        ParsedType = eStr;
        for(ByteCntr = 0; ByteCntr < (CharCnt-4); ByteCntr++)
          ParsedString[ByteCntr] = GCATInputBuffer[ByteCntr+4];
        ParsedString[CharCnt - 4] = 0;
// now see if we want a non string type
// for an integer - use atoi, but see if 1st character is numeric, + or -
        if (StructPtr->RXType != eStr)
        {
          ch=ParsedString[0];
          if (isNumeric(ch))
          {
            ParsedType = eNum;
            ParsedInt = atoi(ParsedString);
// finally see if we need a bool
            if (StructPtr->RXType == eBool)
            {
              ParsedType = eBool;
              if (ParsedInt == 1)
                ParsedBool = true;
              else
                ParsedBool = false;
            }
          }
          else
          {
            ParsedType = eNone;
            ValidResult = false;            
          }
        }
      }
    }
  }
  if (ValidResult == true)
  {
//    Serial.print("match= ");
//    Serial.print(GCATCommands[MatchedCAT].CATString);
//    Serial.print("; parameter=");
    switch(ParsedType)
    {
      case eStr: 
        HandleCATCommandStringParam(MatchedCAT, ParsedString);
        break;
      case eNum:
        ParsedInt = constrain(ParsedInt, StructPtr->MinParamValue, StructPtr->MaxParamValue);
        HandleCATCommandNumParam(MatchedCAT, ParsedInt);
        break;
      case eBool:
        HandleCATCommandBoolParam(MatchedCAT, ParsedBool);
        break;
      case eNone:
        HandleCATCommandNoParam(MatchedCAT);
        break;
    }
  }
  else
  {
//    Serial.print("Parse Error - cmd= ");
//    Serial.println(GCATInputBuffer);
  }
//
// finally clear the input buffer to start again for the next command
//
   GCATWritePtr = GCATInputBuffer;                   // point to start of buffer
}



#ifdef SENSORDEBUG
//
// ParseDebugCATCmd()
// Parse a single command in the local input buffer
// process it if it is a valid command
//
void ParseDebugCATCmd(void)
{
  int CharCnt;                              // number of characters in the buffer (same as length of string)
  unsigned long MatchWord;                  // 32 bit compressed input cmd
  EDebugCATCommands MatchedCAT = eNoDebugCommand;     // CAT command we've matched this to
  int CmdCntr;                              // counts CAT commands
  SCATCommands* StructPtr;                  // pointer to structure with CAT data
  ERXParamType ParsedType;                  // type of parameter actually found
  bool ParsedBool;                          // if a bool expected, it goes here
  int ParsedInt;                            // if int expected, it goes here
  char ParsedString[20];                    // if string expected, it goes here
  int ByteCntr;
  char ch;
  bool ValidResult = true;                  // true if we get a valid parse result

  
  CharCnt = (GCATWritePtr - GCATInputBuffer) - 2;
//
// CharCnt holds the input string length excluding the terminating null and excluding the semicolon
// test minimum length for a valid CAT command: ZZxx; plus terminating 0
//
  if (CharCnt < 4)
    ValidResult = false;
  else
  {
    MatchWord = Make32BitStr(GCATInputBuffer);
    for (CmdCntr=0; CmdCntr < GNumCommands; CmdCntr++)         // loop thro commands we recognise
    {
      if (GCATMatch[CmdCntr] == MatchWord)
      {
        MatchedCAT = (EDebugCATCommands)CmdCntr;                           // if a match, exit loop
        StructPtr = GCATDebugCommands + (int)CmdCntr;
        break;
      }
    }
    if(MatchedCAT == eNoDebugCommand)                                      // if no match was found
      ValidResult = false;
    else
    {
//
// we have recognised a 4 char abcd command that is terminated by a semicolon
// now we need to process the parameter bytes (if any) in the middle
// the CAT structs have the required information
// any parameter starts at position 4 and ends at (charcnt-1)
//
      if (CharCnt == 4)
        ParsedType=eNone;
      else
      {
//
// strategy is: first copy just the param to a string
// if required type is not a string, parse to a number
// then if required type is bool, check the value
//        
        ParsedType = eStr;
        for(ByteCntr = 0; ByteCntr < (CharCnt-4); ByteCntr++)
          ParsedString[ByteCntr] = GCATInputBuffer[ByteCntr+4];
        ParsedString[CharCnt - 4] = 0;
// now see if we want a non string type
// for an integer - use atoi, but see if 1st character is numeric, + or -
        if (StructPtr->RXType != eStr)
        {
          ch=ParsedString[0];
          if (isNumeric(ch))
          {
            ParsedType = eNum;
            ParsedInt = atoi(ParsedString);
// finally see if we need a bool
            if (StructPtr->RXType == eBool)
            {
              ParsedType = eBool;
              if (ParsedInt == 1)
                ParsedBool = true;
              else
                ParsedBool = false;
            }
          }
          else
          {
            ParsedType = eNone;
            ValidResult = false;            
          }
        }
      }
    }
  }
  if (ValidResult == true)
  {
//    Serial.print("match= ");
//    Serial.print(GCATCommands[MatchedCAT].CATString);
//    Serial.print("; parameter=");
    switch(MatchedCAT)
    {
      case eLEDOn:                           // turn LED on
        SetLED(ParsedInt-1, true);
        Serial.print("SET led ");
        Serial.print(ParsedInt);
        Serial.println(" on");
        break;

      case eLEDOff:                          // turn LED off
        SetLED(ParsedInt-1, false);
        Serial.print("SET led ");
        Serial.print(ParsedInt);
        Serial.println(" off");
        break;

      case eIdent:                           // identify h/w
#ifdef V3HARDWARE
        Serial.println("ODIN Console, Andromeda 2nd prototype hardware");
#elif defined V2HARDWARE
        Serial.println("ODIN Console, Andromeda 1st prototype hardware");
#else
        Serial.println("ODIN Console, original hardware");
#endif      
        break;
    }
  }
  else
  {
    Serial.print("Parse Error - cmd= ");
    Serial.println(GCATInputBuffer);
  }
//
// finally clear the input buffer to start again for the next command
//
   GCATWritePtr = GCATInputBuffer;                   // point to start of buffer
}
#endif


//
// send a CAT command
//
void SendCATMessage(char* Msg)
{
  CATSERIAL.print(Msg);
}



//
// helper to append a string with a character
//
void Append(char* s, char ch)
{
  int len;

  len = strlen(s);
  s[len++] = ch;
  s[len] = 0;
}


//
// helper to convert int to string
//
void LongToString(long Param, char* Output, int CharCount)
{
  unsigned long Divisor;           // initial divisor to convert to ascii
  unsigned int Digit;             // decimal digit found
  char ASCIIDigit;

  
  Output[0]=0;                      // set output string to zero length
  if (Param < 0)                   // not always signed, but neg so it needs a sign
  {
      Append(Output, '-');
      Param = -Param;      
      CharCount--;                      // make positive
  }
//
// we now have a positive number to fit into <CharCount> digits
// pad with zeros if needed
//
  Divisor = DivisorTable[CharCount];
  while (Divisor > 1)
  {
    Digit = (int) (Param / Divisor);          // get the digit for this decimal position
    ASCIIDigit = (char)(Digit + '0');         // ASCII version - and output it
    Append(Output, ASCIIDigit);
    Param = Param - ((long)Digit * Divisor);  // get remainder
    Divisor = Divisor / 10L;                  // set for next digit
  }
  ASCIIDigit = (char)(Param + '0');           // ASCII version of units digit
  Append(Output, ASCIIDigit);
}


//
// create CAT message:
// this creates a "basic" CAT command with no parameter
// (for example to send a "get" command)
//
void MakeCATMessageNoParam(ECATCommands Cmd)
{
  SCATCommands* StructPtr;

  StructPtr = GCATCommands + (int)Cmd;
  strcpy(Output, StructPtr->CATString);
  strcat(Output, ";");
  SendCATMessage(Output);
}



//
// make a CAT command with a numeric parameter
//
void MakeCATMessageNumeric(ECATCommands Cmd, int Param)
{
  int CharCount;                  // character count to add
  unsigned int Divisor;           // initial divisor to convert to ascii
  unsigned int Digit;             // decimal digit found
  char ASCIIDigit;
  SCATCommands* StructPtr;

  StructPtr = GCATCommands + (int)Cmd;
  strcpy(Output, StructPtr->CATString);
  CharCount = StructPtr->NumParams;
//
// clip the parameter to the allowed numeric range
//
  if (Param > StructPtr->MaxParamValue)
    Param = StructPtr->MaxParamValue;
  else if (Param < StructPtr->MinParamValue)
    Param = StructPtr->MinParamValue;
//
// now add sign if needed
//
  if (StructPtr -> AlwaysSigned)
  {
    if (Param < 0)
    {
      strcat(Output, "-");
      Param = -Param;                   // make positive
    }
    else
      strcat(Output, "+");
    CharCount--;
  }
  else if (Param < 0)                   // not always signed, but neg so it needs a sign
  {
      strcat(Output, "-");
      Param = -Param;      
      CharCount--;                      // make positive
  }
//
// we now have a positive number to fit into <CharCount> digits
// pad with zeros if needed
//
  Divisor = DivisorTable[CharCount];
  while (Divisor > 1)
  {
    Digit = Param / Divisor;                  // get the digit for this decimal position
    ASCIIDigit = (char)(Digit + '0');         // ASCII version - and output it
    Append(Output, ASCIIDigit);
    Param = Param - (Digit * Divisor);        // get remainder
    Divisor = Divisor / 10;                   // set for next digit
  }
  ASCIIDigit = (char)(Param + '0');           // ASCII version of units digit
  Append(Output, ASCIIDigit);
  strcat(Output, ";");
  SendCATMessage(Output);
}


//
// make a CAT command with a bool parameter
//
void MakeCATMessageBool(ECATCommands Cmd, bool Param) 
{
  SCATCommands* StructPtr;

  StructPtr = GCATCommands + (int)Cmd;
  strcpy(Output, StructPtr->CATString);               // copy the base message
  if (Param)
    strcat(Output, "1;");
  else
    strcat(Output, "0;");
  SendCATMessage(Output);
}



//
// make a CAT command with a string parameter
// the string is truncated if too long, or padded with spaces if too short
//
void MakeCATMessageString(ECATCommands Cmd, char* Param) 
{
  int ParamLength, ReqdLength;                        // string lengths
  SCATCommands* StructPtr;
  int Cntr;

  StructPtr = GCATCommands + (int)Cmd;
  ParamLength = strlen(Param);                        // length of input string
  ReqdLength = StructPtr->NumParams;                  // required length of parameter "nnnn" string not including semicolon
  
  strcpy(Output, StructPtr->CATString);               // copy the base message
  if(ParamLength > ReqdLength)                        // if string too long, truncate it
    Param[ReqdLength]=0; 
  strcat(Output, Param);                              // append the string
//
// now see if we need to pad
//
  if (ParamLength < ReqdLength)
  for (Cntr=0; Cntr < (ReqdLength-ParamLength); Cntr++)
    strcat(Output, " ");
//
// finally terminate and send  
//
  strcat(Output, ";");                                // add the terminating semicolon
  SendCATMessage(Output);
}


#define VENCODERSTRINGLENGTH 15
//
// special function to make the CAT message that shows the current multifunction encoder action
// parameter is the int action number; string looked up from a table
//
void MakeEncoderActionCAT(int MultiFunction)
{
  int ParamLength, ReqdLength;                        // string lengths
  char EncoderText[30];                               // text we want to copy 
  int Cntr;
  char ch;
  char DigitStr[5];

//
// first get the right string, and get to exactly correct length
//
  ReqdLength = VENCODERSTRINGLENGTH;                  // required length of parameter "nnnn" string not including semicolon
  strcpy(EncoderText, MultiEncoderCATStrings[MultiFunction]);
  ParamLength = strlen(EncoderText);                  // length of input string
  
  if(ParamLength > ReqdLength)                        // if string too long, truncate it
    EncoderText[ReqdLength]=0; 
//
// now see if we need to pad
//
  if (ParamLength < ReqdLength)
  for (Cntr=0; Cntr < (ReqdLength-ParamLength); Cntr++)
    strcat(EncoderText, " ");
//
// we now have a string of the correct length. 
// get each character to a string with the ascii value, minus 32
//
  strcpy(Output, "ZZMF");                             // copy the base message
  for (Cntr=0; Cntr < ReqdLength; Cntr++)
  {
    ch = EncoderText[Cntr] - 32;
    LongToString(ch, DigitStr, 2);
    strcat(Output, DigitStr);
  }
//
// finally terminate and send  
//
  strcat(Output, ";");                                // add the terminating semicolon
  SendCATMessage(Output);


  
}
