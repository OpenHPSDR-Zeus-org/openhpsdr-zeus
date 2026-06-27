// ----------------------------------------------------------------------------
// Rotary Encoder Driver based on ClickEncoder 
//
// (c) 2010 karl@pitrich.com
// (c) 2014 karl@pitrich.com
// 
// Timer-based rotary encoder logic by Peter Dannegger
// ----------------------------------------------------------------------------

#ifndef __have__NoClickEncoder_h__
#define __have__NoClickEncoder_h__


#include <stdint.h>
#if defined(__AVR__)
#include <avr/io.h>
#include <avr/interrupt.h>
#include <avr/pgmspace.h>
#endif
#include "Arduino.h"

// ----------------------------------------------------------------------------

#define ENC_NORMAL        (1 << 1)   // use Peter Danneger's decoder
#define ENC_FLAKY         (1 << 2)   // use Table-based decoder

// ----------------------------------------------------------------------------

#ifndef ENC_DECODER
#  define ENC_DECODER     ENC_NORMAL
#endif

#if ENC_DECODER == ENC_FLAKY
#  ifndef ENC_HALFSTEP
#    define ENC_HALFSTEP  1        // use table for half step per default
#  endif
#endif

// ----------------------------------------------------------------------------
#if    defined(__arm__) && (defined (__STM32F1__) || defined (__STM32F4__) )
  typedef WiringPinMode pinMode_t;
#else
  typedef uint8_t pinMode_t;
#endif

class NoClickEncoder
{
public:

public:
  NoClickEncoder(int8_t A, int8_t B, uint8_t stepsPerNotch = 4, bool active = LOW);

  void service(void);  
  int16_t getValue(void);


public:
protected:
  int8_t pinA;
  int8_t pinB;
  int8_t pinBTN;
  bool pinsActive;
  volatile int16_t delta;
  volatile int16_t last;
  volatile uint8_t steps;
#if ENC_DECODER != ENC_NORMAL
  static const int8_t table[16];
#endif
};


// ----------------------------------------------------------------------------

#endif // __have__NoClickEncoder_h__

