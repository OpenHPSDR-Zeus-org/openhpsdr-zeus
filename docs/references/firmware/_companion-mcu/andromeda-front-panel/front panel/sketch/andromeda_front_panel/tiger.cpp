/////////////////////////////////////////////////////////////////////////
//
// Andromeda front panel controller by Laurence Barker G8NJJ
// this sketch provides a knob and switch interface through USB and CAT
// copyright (c) Laurence Barker G8NJJ 2019
//
// tiger.c
// this file holds the CAT parsing code
// large CAT file = tiger....
/////////////////////////////////////////////////////////////////////////

#include "globalinclude.h"
#include "tiger.h"
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
// array of records. This must exactly match the enum ECATCommands in tiger.h
// and the number of commands defined here must be correct
// (not including the final eNoCommand)
#define VNUMCATCMDS 8

SCATCommands GCATCommands[VNUMCATCMDS] = 
{
  {"ZZZD", eNum, 0, 99, 2, false},                         // VFO down
  {"ZZZU", eNum, 0, 99, 2, false},                         // VFO up
  {"ZZZE", eNum, 0, 999, 3, false},                        // encoder
  {"ZZZP", eNum, 0, 999, 3, false},                        // pushbutton
  {"ZZZI", eNum, 0, 999, 3, false},                        // indicator
  {"ZZZS", eNum, 0, 999, 3, false},                        // s/w version
  {"ZZZH", eNum, 0, 99, 2, false},                         // h/w version
  {"ZZZT", eNum, 0, 99, 2, false}                          // product version
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
// initialise the matching 32 bit words to hold a version of each CAT command
  GNumCommands = VNUMCATCMDS;
  for(CmdCntr=0; CmdCntr < VNUMCATCMDS; CmdCntr++)
  {
    MatchWord = Make32BitStr(GCATCommands[CmdCntr].CATString);
    GCATMatch[CmdCntr] = MatchWord;
  }
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
          ParseCATCmd();     
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
        break;
      case eNum:
        ParsedInt = constrain(ParsedInt, StructPtr->MinParamValue, StructPtr->MaxParamValue);
        HandleCATCommandNumParam(MatchedCAT, ParsedInt);
        break;
      case eBool:
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
