/**
********************************************************************************
* @file     USART2.c
* @author   Rene Siroky
* @version  V1.0.0
* @date     11.8.2024
* @brief    
********************************************************************************
* @note
********************************************************************************
*/
/* Includes ------------------------------------------------------------------*/
#include "USART2.h"
#include "bandVoltage.h"
#include "bcdBandCode.h"

extern UART_HandleTypeDef huart2;

/* Private define ------------------------------------------------------------*/
#define RX_DATA_LENGTH  14

/* Private macro -------------------------------------------------------------*/
/* Private typedef -----------------------------------------------------------*/
/* Private constants ---------------------------------------------------------*/
/* Private variables ---------------------------------------------------------*/
uint8_t rxData;
uint8_t rxIndex;

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
void usart2Init(void)
{
  rxIndex = 0;
  HAL_UART_Receive_IT(&huart2, &rxData, 1);
}

/* Public Functions Called from Interrupt Service Routines -------------------*/
/******************************************************************************/
/**
* @brief  
* @param  None
* @retval None
*/
void HAL_UART_RxCpltCallback(UART_HandleTypeDef *huart)
{
  static uint16_t rxFrq;
  
  if (huart->Instance == USART2)  // Zkontroluj, že pøerušení pøišlo z USART2
  {

    if (rxData == 'F')
    {
      rxIndex = 1;
      rxFrq = 0;
    }

    if (rxIndex)
    {
      if (rxIndex == 6)
      {
        rxFrq =  (rxData - 0x30) * 10000;
      }
      else if (rxIndex == 7)
      {
        rxFrq +=  (rxData - 0x30) * 1000;
      }
      else if (rxIndex == 8)
      {
        rxFrq +=  (rxData - 0x30) * 100;
      }
      else if (rxIndex == 9)
      {
        rxFrq +=  (rxData - 0x30) * 10;
      }
      else if (rxIndex == 10)
      {
        rxFrq +=  (rxData - 0x30);
      }
      
      if (rxData == ';')
      {
        bandVoltageProcess(rxFrq);
        bcdBandCodeProcess(rxFrq);
        rxIndex = 0;
      }
      else
      {
        if (rxIndex++ > RX_DATA_LENGTH)
        {
          rxIndex = 0;
        }
      }
    }

    // Pokraèuj v pøijímání dalších znakù
    HAL_UART_Receive_IT(&huart2, &rxData, 1);
  }
}

/******************************************************************************/
/**
* End Of File USART2.c
*/
