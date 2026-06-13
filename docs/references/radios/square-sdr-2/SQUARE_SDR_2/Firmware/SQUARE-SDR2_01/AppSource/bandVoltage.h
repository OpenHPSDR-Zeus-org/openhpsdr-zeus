/**
********************************************************************************
* @file     bandVoltage.h
* @author   Rene Siroky
* @version  V1.0.0
* @date     11.8.2024
* @brief    Header file for bandVoltage.c
********************************************************************************
* @note
********************************************************************************
*/
/* Define to prevent recursive inclusion -------------------------------------*/
#ifndef __BANDVOLTAGE_H__
#define __BANDVOLTAGE_H__
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
  void bandVoltageInit(void);
  void bandVoltageProcess(uint16_t frq);
  void bandVoltageWriteSetting(void);
  void bandVoltageReadSetting(void);
  
  /****************************************************************************/
  /* Public Functions Called from Interrupt Service Routines -----------------*/
  /****************************************************************************/
#ifdef __cplusplus
}
#endif
#endif /* __BANDVOLTAGE_H__ */
/******************************************************************************/
/**
* End Of File bandVoltage.h
*/