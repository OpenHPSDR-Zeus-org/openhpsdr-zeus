/**
********************************************************************************
* @file     fanControl.c
* @author   Rene Siroky
* @version  V1.0.0
* @date     4.4.2025
* @brief    Control Fan Speed
********************************************************************************
* @note
********************************************************************************
*/
/* Includes ------------------------------------------------------------------*/
#include "fanControl.h"
#include "analogConverter.h"

extern TIM_HandleTypeDef htim3;

/* Private define ------------------------------------------------------------*/
#define FAN_CTRL_SPEED_0    ((uint16_t)0)       /* PWM for Fan OFF  */
#define FAN_CTRL_SPEED_25   ((uint16_t)80)      /* PWM for Fan 25%  */
#define FAN_CTRL_SPEED_50   ((uint16_t)160)     /* PWM for Fan 50%  */
#define FAN_CTRL_SPEED_75   ((uint16_t)240)     /* PWM for Fan 75%  */
#define FAN_CTRL_SPEED_100  ((uint16_t)320)     /* PWM for Fan FULL */

#define FAN_CTRL_TEMP_20C   ((uint16_t)0xF36F)  /* ADC for 20°C */
#define FAN_CTRL_TEMP_25C   ((uint16_t)0xF3AE)  /* ADC for 25°C */
#define FAN_CTRL_TEMP_30C   ((uint16_t)0xF3ED)  /* ADC for 30°C */
#define FAN_CTRL_TEMP_35C   ((uint16_t)0xF42B)  /* ADC for 35°C */
#define FAN_CTRL_TEMP_37C   ((uint16_t)0xF44B)  /* ADC for 37°C */
#define FAN_CTRL_TEMP_40C   ((uint16_t)0xF46B)  /* ADC for 40°C */
#define FAN_CTRL_TEMP_42C   ((uint16_t)0xF48A)  /* ADC for 42°C */
#define FAN_CTRL_TEMP_45C   ((uint16_t)0xF4AA)  /* ADC for 45°C */
#define FAN_CTRL_TEMP_47C   ((uint16_t)0xF4C9)  /* ADC for 47°C */
#define FAN_CTRL_TEMP_50C   ((uint16_t)0xF4E8)  /* ADC for 50°C */
#define FAN_CTRL_TEMP_52C   ((uint16_t)0xF507)  /* ADC for 52°C */
#define FAN_CTRL_TEMP_55C   ((uint16_t)0xF527)  /* ADC for 55°C */
#define FAN_CTRL_TEMP_60C   ((uint16_t)0xF566)  /* ADC for 60°C */

#define FAN_CTRL_CYCLE      10000

/* Private macro -------------------------------------------------------------*/
/* Private typedef -----------------------------------------------------------*/
/* Private constants ---------------------------------------------------------*/
/* Private variables ---------------------------------------------------------*/
uint8_t fanState;
uint32_t counterCycle;
uint16_t actualTemperature;

/* Private function prototypes -----------------------------------------------*/
/* Private functions ---------------------------------------------------------*/
/******************************************************************************/
/**
* @brief  
* @param  None
* @retval None
*/
/* Exported functions --------------------------------------------------------*/
/******************************************************************************/
/**
* @brief  
* @param  None
* @retval None
*/
void fanControlInit(void)
{
  /* Set Fan Speed to FULL - 100% */
  __HAL_TIM_SET_COMPARE(&htim3, TIM_CHANNEL_1, FAN_CTRL_SPEED_100);
  
  /* Start channel 1 */
  if (HAL_TIM_PWM_Start(&htim3, TIM_CHANNEL_1) != HAL_OK)
  {
    /* PWM Generation Error */
    Error_Handler();
  }
  
  fanState = 0;
  counterCycle = 0;
}

/******************************************************************************/
/**
* @brief  
* @param  None
* @retval None
*/
void fanControlProcess(void)
{
  if (counterCycle <= FAN_CTRL_CYCLE)
  {
    counterCycle++;
    return;
  }
  else
  {
    actualTemperature = analogConverterReadChannelTEMP();
    
    switch (fanState)
    {
    case 0: /* Fan OFF */
      __HAL_TIM_SET_COMPARE(&htim3, TIM_CHANNEL_1, FAN_CTRL_SPEED_0);
      if (actualTemperature > FAN_CTRL_TEMP_37C) fanState = 1;
      break;
      
    case 1: /* Fan 25% */
      __HAL_TIM_SET_COMPARE(&htim3, TIM_CHANNEL_1, FAN_CTRL_SPEED_25);
      if (actualTemperature > FAN_CTRL_TEMP_40C) fanState = 2;
      if (actualTemperature < FAN_CTRL_TEMP_35C) fanState = 0;
      break;
      
    case 2: /* Fan 50% */
      __HAL_TIM_SET_COMPARE(&htim3, TIM_CHANNEL_1, FAN_CTRL_SPEED_50);
      if (actualTemperature > FAN_CTRL_TEMP_45C) fanState = 3;
      if (actualTemperature < FAN_CTRL_TEMP_37C) fanState = 1;
      break;
      
    case 3: /* Fan 75% */
      __HAL_TIM_SET_COMPARE(&htim3, TIM_CHANNEL_1, FAN_CTRL_SPEED_75);
      if (actualTemperature > FAN_CTRL_TEMP_55C) fanState = 4;
      if (actualTemperature < FAN_CTRL_TEMP_40C) fanState = 2;
      break;
      
    case 4: /* Fan FULL */
      __HAL_TIM_SET_COMPARE(&htim3, TIM_CHANNEL_1, FAN_CTRL_SPEED_100);
      if (actualTemperature < FAN_CTRL_TEMP_50C) fanState = 3;
      break;
    }   
    
    counterCycle = 0;
  }
  
}

/* Public Functions Called from Interrupt Service Routines -------------------*/
/******************************************************************************/
/**
* @brief  
* @param  None
* @retval None
*/
/******************************************************************************/
/**
* End Of File fanControl.c
*/
