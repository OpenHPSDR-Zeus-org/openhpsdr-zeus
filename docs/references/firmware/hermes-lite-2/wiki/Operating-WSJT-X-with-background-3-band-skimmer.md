
Here is the setup I'm using to operate FT8/FT4 with Hermes-lite.

The main program is SparkSDR which I run 3 receivers on 40m/20m/15m that matches my fan dipole.  I setup FT8/FT4/JT9/PSK skimmers on each of those three bands.  I then use the 4th receiver as the interface to WSJT-X.  With VB-Audio Cable and rigctl WSJT-X is able to control that receiver to tune any frequency or mode.

![SparkSDR with WSJT-X](https://lh3.googleusercontent.com/pw/ACtC-3fM9VbeffLQ9-wLJ8v0Bpq514nw5ESuX0x-oeZGfylIsb3aATYxmbMRt4oDOuxcAhpUHGeM_zMfTDktWG1z00WojCl4mb56XJBlJZ_z-hMDVdVjbOmQkzdQliwoQSKLuD-qHhW_cMjKBDa-HV_Jb5cC=w1858-h1045-no?authuser=0)

To setup SparkSDR pick the transceiver that will be dedicated to WSJT-X.  Set it to DigiU, and then open up the configuration by pressing the ... button in the bottom right.  Inside pick The VB-Audio cable for both input and output, then enable Rigctl CAT.  When it's enabled remember the port for WSJT-X.  In this example the port 51111.

![SparkSDR virtual transceiver DigiU setup](https://lh3.googleusercontent.com/pw/ACtC-3fFaXePMnrx8vE0WDGEr8ChNGCB6mJJk-3dWvta2s2nsP_T74pXgUdSlQbLfSjiSYrYtIssjcwDlfRuZZm7Xg8Yxetm6GDntRD5SrEY84PWUcJIRZIvtyInicm6W7eNvqYjycm2ulLfgR38Z4ThsA_q=w1759-h887-no?authuser=0)


![SparkSDR virtual transceiver setup](https://lh3.googleusercontent.com/pw/ACtC-3fc8wDR5763sJRxGSuDPPijRfsSHnW4faTPyR4pr0Xg55zjlO46Tgf0DCJUP-WySol8eJSWa5GRr-AzxPzJOn-olkcl0BDzPnKRbTYElthGGP5xsAEExiHZazggxjE1C9ASyuL2BwFgxP1iCBgl3gzR=w359-h773-no?authuser=0)

Then setup WSJT-X
* Hamlib NET rigctl
* IP Adress of the machine where SparkSDR is running
* same port as in Spark configured 51111
* VB-Audio cable.

![WSJT settings](https://lh3.googleusercontent.com/pw/ACtC-3fVrz3zGEwspr6d2Yvr3erZXBeK28__tbt1lkxO9y2p2SJbk1eabvsaWsGtm6hw0ZNrXk0xl27IJmISyvQt5efXTfiIq8wD7Z8Y7qfjANvLA-zMpN-aIjhhmAyVa8CA-sN3HdHdaIaRPW9DR8-GKRcO=w649-h595-no?authuser=0)

I find it helpful to use [pskreporter](https://pskreporter.info/) to visualize what is happening on the three different bands.

Links to software used:
* [SparkSDR](https://www.sparksdr.com/)
* [WSJT-X](https://physics.princeton.edu/pulsar/K1JT/wsjtx.html)
* [VB-Cable](https://www.vb-audio.com/Cable/)

