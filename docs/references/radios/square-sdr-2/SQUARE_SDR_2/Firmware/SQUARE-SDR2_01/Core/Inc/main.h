/* USER CODE BEGIN Header */
/**
  ******************************************************************************
  * @file           : main.h
  * @brief          : Header for main.c file.
  *                   This file contains the common defines of the application.
  ******************************************************************************
  * @attention
  *
  * Copyright (c) 2024 STMicroelectronics.
  * All rights reserved.
  *
  * This software is licensed under terms that can be found in the LICENSE file
  * in the root directory of this software component.
  * If no LICENSE file comes with this software, it is provided AS-IS.
  *
  ******************************************************************************
  */
/* USER CODE END Header */

/* Define to prevent recursive inclusion -------------------------------------*/
#ifndef __MAIN_H
#define __MAIN_H

#ifdef __cplusplus
extern "C" {
#endif

/* Includes ------------------------------------------------------------------*/
#include "stm32g0xx_hal.h"

/* Private includes ----------------------------------------------------------*/
/* USER CODE BEGIN Includes */

/* USER CODE END Includes */

/* Exported types ------------------------------------------------------------*/
/* USER CODE BEGIN ET */

/* USER CODE END ET */

/* Exported constants --------------------------------------------------------*/
/* USER CODE BEGIN EC */

/* USER CODE END EC */

/* Exported macro ------------------------------------------------------------*/
/* USER CODE BEGIN EM */

/* USER CODE END EM */

void HAL_TIM_MspPostInit(TIM_HandleTypeDef *htim);

/* Exported functions prototypes ---------------------------------------------*/
void Error_Handler(void);

/* USER CODE BEGIN EFP */

/* USER CODE END EFP */

/* Private defines -----------------------------------------------------------*/
#define BCD_D_Pin GPIO_PIN_14
#define BCD_D_GPIO_Port GPIOC
#define BCD_A_Pin GPIO_PIN_15
#define BCD_A_GPIO_Port GPIOC
#define EXP_15_Pin GPIO_PIN_0
#define EXP_15_GPIO_Port GPIOA
#define EXP_10_Pin GPIO_PIN_1
#define EXP_10_GPIO_Port GPIOA
#define BCD_B_Pin GPIO_PIN_4
#define BCD_B_GPIO_Port GPIOA
#define BCD_C_Pin GPIO_PIN_5
#define BCD_C_GPIO_Port GPIOA
#define ADC_REV_Pin GPIO_PIN_6
#define ADC_REV_GPIO_Port GPIOA
#define ADC_TMP_Pin GPIO_PIN_7
#define ADC_TMP_GPIO_Port GPIOA
#define ADC_IPA_Pin GPIO_PIN_0
#define ADC_IPA_GPIO_Port GPIOB
#define ADC_FWD_Pin GPIO_PIN_1
#define ADC_FWD_GPIO_Port GPIOB
#define PA_INTTR_Pin GPIO_PIN_8
#define PA_INTTR_GPIO_Port GPIOA
#define PA_INTTR_EXTI_IRQn EXTI4_15_IRQn
#define PWM_FAN_Pin GPIO_PIN_6
#define PWM_FAN_GPIO_Port GPIOC
#define I2C2_ADC_SCL_Pin GPIO_PIN_11
#define I2C2_ADC_SCL_GPIO_Port GPIOA
#define I2C2_ADC_SDA_Pin GPIO_PIN_12
#define I2C2_ADC_SDA_GPIO_Port GPIOA
#define EXP_HPF_Pin GPIO_PIN_15
#define EXP_HPF_GPIO_Port GPIOA
#define EXP_160_Pin GPIO_PIN_3
#define EXP_160_GPIO_Port GPIOB
#define EXP_80_Pin GPIO_PIN_4
#define EXP_80_GPIO_Port GPIOB
#define EXP_40_Pin GPIO_PIN_5
#define EXP_40_GPIO_Port GPIOB
#define I2C1_EXP_SCL_Pin GPIO_PIN_6
#define I2C1_EXP_SCL_GPIO_Port GPIOB
#define I2C1_EXP_SDA_Pin GPIO_PIN_7
#define I2C1_EXP_SDA_GPIO_Port GPIOB
#define EXP_20_Pin GPIO_PIN_8
#define EXP_20_GPIO_Port GPIOB

/* USER CODE BEGIN Private defines */

/* USER CODE END Private defines */

#ifdef __cplusplus
}
#endif

#endif /* __MAIN_H */
