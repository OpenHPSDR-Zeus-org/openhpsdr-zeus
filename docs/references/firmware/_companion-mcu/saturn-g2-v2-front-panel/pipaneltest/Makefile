# Makefile for i2ctest
# *****************************************************
# Variables to control Makefile operation
 
CC = gcc
LD = gcc
CFLAGS = -Wall -Wextra -Wno-unused-function -g -D_GNU_SOURCE
LDFLAGS = -lm -lpthread
LIBS = -lgpiod -li2c
TARGET = i2ctest
VPATH=.:../common
GIT_DATE := $(wordlist 2,5, $(shell git log -1 --format=%cd --date=rfc))
 
# ****************************************************
# Targets needed to bring the executable up to date

OBJS=    $(TARGET).o i2cdriver.o

all: $(OBJS)
	$(LD) -o $(TARGET) $(OBJS) $(LDFLAGS) $(LIBS)
 
 
%.o: %.c
	$(CC) -c -o $(@F) $(CFLAGS) -D GIT_DATE='"$(GIT_DATE)"' $<

clean:
	rm -rf $(TARGET) *.o *.bin
