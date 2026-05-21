/**
********************************************************************************
* @file     filterSwitch.h
* @author   Rene Siroky
* @version  V1.0.0
* @date     8.8.2024
* @brief    Header file for filterSwitch.c
********************************************************************************
* @note
********************************************************************************
*/
/* Define to prevent recursive inclusion -------------------------------------*/
#ifndef __FILTERSWITCH_H__
#define __FILTERSWITCH_H__
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
  void filterSwitchInit(void);
  void filterSwitchWriteProcess(void);
  void filterSwitchReadProcess(void);
  
  /****************************************************************************/
  /* Public Functions Called from Interrupt Service Routines -----------------*/
  /****************************************************************************/
#ifdef __cplusplus
}
#endif
#endif /* __FILTERSWITCH_H__ */
/******************************************************************************/
/**
* End Of File filterSwitch.h
*/