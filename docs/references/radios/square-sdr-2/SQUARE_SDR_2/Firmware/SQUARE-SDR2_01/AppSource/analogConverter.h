/**
********************************************************************************
* @file     analogConverter.h
* @author   Rene Siroky
* @version  V1.0.0
* @date     26.2.2024
* @brief    Header file for analogConverter.c
********************************************************************************
* @note
********************************************************************************
*/
/* Define to prevent recursive inclusion -------------------------------------*/
#ifndef __ANOLOGCONVERTER_H__
#define __ANOLOGCONVERTER_H__
#ifdef __cplusplus
extern "C" {
#endif
  /* Includes ----------------------------------------------------------------*/
#include "main.h"
  /* User define -------------------------------------------------------------*/
  /* Exported types ----------------------------------------------------------*/
  /* Exported constants ------------------------------------------------------*/
  /* Exported macro ----------------------------------------------------------*/
  /* Exported functions ------------------------------------------------------*/
  void analogConverterInit(void);
  void analogConverterProcess(void);
  uint16_t analogConverterReadChannelTEMP(void);
  uint16_t analogConverterReadInternalVREF(void);
  
  /****************************************************************************/
  /* Public Functions Called from Interrupt Service Routines -----------------*/
  /****************************************************************************/
#ifdef __cplusplus
}
#endif
#endif /* __ANOLOGCONVERTER_H__ */
/******************************************************************************/
/**
* End Of File analogConverter.h
*/