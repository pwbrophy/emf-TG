# Thundergeddon

## What is Thundergeddon?

**Thundergeddon** is a hybrid tabletop/RC tank battle game: real, small robotic tanks fight in the physical world, while a **Unity companion app** runs the match logic and controls the robots over the local network.

* **Physical side:**
  
  * Custom ESP32 electronics, motor + turret control, and an IR “shooting / hit detection” system plus FPV camera streaming
  
  * Robots measure about 13cm long, 8cm wide and 8cm high
  * Robots body is 3d printed PLA, created in OnShape
  * Electronics designed in KiCad, manufactured by JLC PCB
  
  The prototype electronics are designed to be simple, rather than fully featured e.g. I have left out charging.
  
* **Digital side (Unity app):** discovers/hosts robots on Wi-Fi (UDP discovery + WebSocket control), lets you select and drive a tank with an on-screen joystick, shows a live video feed per robot, and runs **realtime** or **turn-based rules** like player/alliance turns, action points, and a coordinated “shoot” sequence.

## To-do

V0.2

- [x] Test reverse polarity protection
- [x] Add hull board LED on spare GPIO
- [x] Hull: Battery terminal holder tighten
- [x] Hull: Remove bit between battery terminals
- [ ] Turret Gear Tighter on Motor
- [x] Turret->Hull screws length compensation
- [x] Camera Holder
- [x] Turret Slip Ring Wires Holder
- [x] Move Antenna
- [x] NFC reader
- [x] Redesign tracks
- [ ] Order Batteries
- [ ] To order from Aliexpress:
  - [ ] Cameras
  - [ ] Motors
  - [ ] Antenna and connector

- [ ] Check qty
  - [ ] LED strip
  - [ ] IR LEDs

- [ ] Order Battery Charger

V0.3

- [ ] Polyfuse on battery
- [ ] Stable voltage for motors - consistent tank speed as battery voltage drops
- [x] NFC reader for capture points
- [ ] Battery charging via USB
- [ ] Turret 5V for LEDs
- [ ] More robust USB power input
- [ ] Speaker sound instead of buzzer: I2S + MAX98357A
- [ ] Vertical turret motor; move turret backwards
- [ ] IR code transmission/decoding for robot ID on hit 



## Hardware

I have designed two boards: hull and turret.  Both boards are standard 2-sided, default JLC PCB boards.  The hull will have two sided assembly, while the turret will only be assembled on the top side.  Both boards have a GND pour on the bottom layer.  

Each board is a separate KiCad file.  

I have also attached an off-the-shelf board for RFID tag recognition for gameplay capture points.  

------

### Hull Board

The hull board connects to the battery, motors, IR receivers and turret board via a slip-ring.

#### **Power**

Two 18350 li-ion batteries power the robot

* 2S Pack (7.4V nominal)
* Connected via a JST XH 2-pin connector (J2 C158012)
* Positive terminal connected to Q2 drain (pad 3) via 1.2mm width copper trace
* Negative connects to GND

#### **Reverse Polarity Protection**

I use a P-channel MOSFET to protect the board from reversed batteries (Q2 C15127)

+ **Drain** (pad 3) connects to VBAT_RAW/J2 (from battery)

- **Source**  (pad 2) connects to C15 via 1.2mm width copper trace (to +BATT  and robot components)
- **Gate** (pad 1) connects to GND via inline 100kΩ resistor (R4 C25803)
- Source and drain both have an an area of copper under them around 5mm×5mm with several vias to a similar size area on the other side of the board

#### **Bulk Capacitor**

I'm using a 220uF bulk capacitor (C15 C2887273). 

* Anode connects to:
  * Q1 Drain
  * +BATT
    * PS1 EN and IN
    * U2 and U3 VM 
    * Slip Ring
* Cathode connects to GND

#### 3.3V Voltage Step-down

* Step down regulator SY8113BADC (PS1 C78989)
* EN and IN connect to C15 with a 10uF decoupling capacitor to GND (C10 C15850)
* FB connects to:
  * 22kΩ to GND (R16 C31850)
  * 100kΩ to +3V3 (R15 C25803)
  * 22pF to +3V3 (C14 C1653) 
* GND connected to GND
* BS connects to 100nf (C13 C14663) inline to LX
* LX connects
  * to 4.7uH inductor (L1 C167220)
  * Two decoupling capacitors 100nf (C7 C14663) and 47uF (C9 C16780)
  * Out to +3V3_HULL

#### I2C Expander

PCA9555PW is used to provide GPIOs for motor control and IR receivers (U8 C128392).  

* SCL and SDA connect to slip ring (pull ups are on the turret board)
* INT connects to GPIO14 on the ESP32 in the turret via the slip ring.  
* A0, A1, A2 connect to GND
* VSS to GND

| IO   | Connects to            | Pull up to +3V3_HULL |
| ---- | ---------------------- | -------------------- |
| 0_0  | IR Receive N           | 10kΩ (R8 C25804)     |
| 0_1  | IR Receive NE          | 10kΩ (R9 C25804)     |
| 0_2  | IR Receive E           | 10kΩ (R13 C25804)    |
| 0_3  | IR Receive SE          | 10kΩ (R14 C25804)    |
| 0_4  | IR Receive S           | 10kΩ (R19 C25804)    |
| 0_5  | IR Receive SW          | 10kΩ (R18 C25804)    |
| 0_6  | IR Receive W           | 10kΩ (R11 C25804)    |
| 0_7  | IR Receive NW          | 10kΩ (R10 C25804)    |
| 1_0  | Left Motor Control 1   | n/a                  |
| 1_1  | Left Motor Control 2   | n/a                  |
| 1_2  | Turret Motor Control 1 | n/a                  |
| 1_3  | Turret Motor Control 2 | n/a                  |
| 1_4  | Motors Sleep           | n/a                  |
| 1_5  | Right Motor Control 2  | n/a                  |
| 1_6  | Right Motor Control 1  | n/a                  |
| 1_7  | LED                    | n/a                  |

The LED (D4 C2297) on 1_7 will enable testing the PCA9555 and has an inline 220Ω (R28 C22962).  

#### Motor Drivers

##### Right Track

Motor Controller DRV8833PWP (U2 C50506) controls the right track.  

* VM connects to +BATT
  * Decoupling capacitors 100nf (C4 C14663) and 10uF (C5 C15850)
* VINT bypassed to GND with 2.2uF (C3 C23630)
* FAULT, AOUT1, AOUT2: no connection
* AIN1, AIN2 tied to GND
* BIN1 connected to Right Motor Control 2
  * 10kΩ Pull down (R26 C25804)
* BIN2 connected to Right Motor Control 1
  * 10kΩ Pull down (R25 C25804)
* BOUT1 connected to Right Drive Motor 1
* BOUT2 connected to Right Drive Motor 2
* GND, BISEN and AISEN connected to GND
* VCP High-side gate drive voltage connects to VM via inline 0.01uF (C1 C57112)
* SLEEP connected to MOTORS_SLEEP

Right Drive Motor 1 and 2 connect to 2-pin JST PH (J3 C131337) which connects to a 050 motor with a 118:1 right-angle gearbox for driving the right track.  

##### Left Track and Turret

Motor Controller DRV8833PWP (U3 C50506) controls the left track and turret.  

* VM connects to +BATT
  * Decoupling capacitors 100nf (C11 C14663) and 10uF (C12 C15850)
* VINT bypassed to GND with 2.2uF (C8 C23630)
* FAULT: no connection
* AIN1 connected to Turret Motor Control 1
  * 10kΩ Pull down (R3 C25804)
* AIN2 connected to Turret Motor Control 2
  * 10kΩ Pull down (R24 C25804)
* AOUT1 connected to Turret Motor 2
* AOUT2 connected to Turret Motor 1
* BIN1 connected to Left Motor Control 1
  * 10kΩ Pull down (R23 C25804)
* BIN2 connected to Left Motor Control 2
  * 10kΩ Pull down (R7 C25804)
* BOUT1 connected to Left Drive Motor 1
* BOUT2 connected to Left Drive Motor 2
* GND, BISEN and AISEN connected to GND
* VCP High-side gate drive voltage connects to VM via inline 0.01uF (C6 C57112)
* SLEEP connected to MOTORS_SLEEP

Left Drive Motor 1 and 2 connect to 2-pin JST PH (J5 C131337) which connects to a 050 motor with a 118:1 right-angle gearbox for driving the left track.  

Turret Motor 1 and 2 connect to 2-pin JST PH (J4 C131337) which connects to a N20 motor with a 1030:1 right-angle gearbox for driving the turret.  

##### Motors Sleep

Motors sleep is connected to both motor drivers and pulled down with a 10kΩ  resistor (R27 C25804)

#### IR Receivers

There are eight infrared receivers IRM-V838M3-C/TR1 (C3291489) at 45 degree increments, facing outwards along the edge of the board.  

* OUT pin connects to a GPIO on the PCA9555
* VCC has an inline 47Ω resistor (RXX, C23182)
* VCC is decoupled with 100nF (C14663) and 4.7uF (C1779)

| GPIO Connection | IR Receiver | 100nF | 4.7uF | 47Ω  |
| --------------- | ----------- | ----- | ----- | ---- |
| IR Receive N    | U4          | C16   | C2    | R1   |
| IR Receive NE   | U5          | C18   | C17   | R2   |
| IR Receive E    | U6          | C19   | C26   | R5   |
| IR Receive SE   | U12         | C20   | C27   | R6   |
| IR Receive S    | U11         | C22   | C28   | R12  |
| IR Receive SW   | U10         | C23   | C29   | R20  |
| IR Receive W    | U9          | C24   | C30   | R21  |
| IR Receive NW   | U7          | C25   | C31   | R22  |

#### Slip Ring Connector

The Slip Ring Connector is a JST PH 5-pin connector(J1 C157993) which provides I2C and power to the turret board, through a 5-wire slip ring.  

* SCL (Yellow wire)
* SDA (Green wire)
* INT (Orange wire)
* GND (Black wire)
* +BATT (Red wire)

#### Other

* Power Status LED
  * Red Power Indicator LED (D9 C2286)
  * Connected to through inline 1k2Ω resistor (R17 C22765)
* Expansion
  * Five holes for expansion (J7 C50950): SCL, SDA, BAT, 3V3, GND
  * Can also be used as test points
  * Won't have the connector on it, just using it for the footprint/hole layout
* Test Points
  * IR N (TP8)
  * SLEEP (TP5)
  * L CTRL 1 (TP3)
  * L CTRL 2 (TP4)
  * L DRV 1 (TP6)
  * L DRV 2 (TP7)
  * INT (TP1)
* Mounting Holes
  * 4 × 2.2mm holes for mounting M2 bolts

------

### Turret Board

The Turret Board contains the ESP32, 3.3V step down, USB connector and connectors for external IR LEDs, buzzer and RGB LEDs.  

#### ESP32

I'm using an ESP32-S3-WROOM-1U-N16R8 (U1 C3013946) which has an IPX connector for an external antenna, and no on-board antenna.  

| PIN    | Connections          |
| ------ | -------------------- |
| EN     | EN Button            |
| GPIO00 | BOOT Button          |
| GPIO01 | Piezo Buzzer         |
| GPIO02 | NC                   |
| GPIO03 | NC                   |
| GPIO04 | Camera: SIOD         |
| GPIO05 | Camera: SIOC         |
| GPIO06 | Camera: VSYNC        |
| GPIO07 | Camera: HREF         |
| GPIO08 | Camera: PCLK         |
| GPIO09 | Camera: Y6           |
| GPIO10 | Camera: Y2           |
| GPIO11 | Camera: Y5           |
| GPIO12 | Camera: Y3           |
| GPIO13 | Camera: Y4           |
| GPIO14 | PCA9555PW INT        |
| GPIO15 | Camera: Y9           |
| GPIO16 | Camera: XCLK         |
| GPIO17 | Camera: Y8           |
| GPIO18 | Camera: Y7           |
| GPIO19 | USB_D-               |
| GPIO20 | USB_D+               |
| GPIO21 | I2C: SDA             |
| GPIO35 | NC (Camera: PSRAM)   |
| GPIO36 | NC (Camera: PSRAM)   |
| GPIO37 | NC (Camera: PSRAM)   |
| GPIO38 | External WS2812 LEDs |
| GPIO39 | IR LED #1            |
| GPIO40 | IR LED #2            |
| GPIO41 | NC                   |
| GPIO42 | NC                   |
| GPIO43 | NC                   |
| GPIO44 | NC                   |
| GPIO45 | NC                   |
| GPIO47 | I2C: SCL             |
| GPIO48 | Green Turret LED     |

The +3V3 connection on the ESP32 is decoupled with two capacitors, 10uF (C28 C15850) and 100nF (C27 C14663).  

##### Buttons

There are buttons on EN  and BOOT.  I chose to include buttons so that I can force the ESP32 into programming mode if required.  I hope that I won't need to press the buttons and just program via USB_D+ and USB_D-.  

###### EN Button

The EN switch (SW2 C318884) is connected on one side to GND and the other side to EN on the ESP32.  Between the switch and EN there is a 1uF capacitor (C23 C15849) to GND and a 10k resistor (R16 C25804) to +3V3.  

###### BOOT button

The BOOT switch (SW1 C318884) is connected on one side to GND and the other side to GPIO00/BOOT on the ESP32.  Between the switch and GPIO0 there is a 10k resistor (R15 C25804) to +3V3.  

##### I2C

SDA and SCL are both pulled high with 4.7kΩ resistors (SDA R19, SCL R20 C23162).  They both have 33Ω inline resistors (SDA R21, SCL R22 C25105).  

##### INT

INT connects from GPIO14 to INT on the PCA9555PW on the hull via the Slip Ring.  The line is pulled high with a 4.7k (R25 C23162)

#### Camera Connector

I am to connect an OV2640 camera module via the camera connector (FPC1 C262643). The camera is fed from 3V3_CLEAN which is converted to 2.8V and 1.3V power rails.  

| Pin  | Camera Pins | Connection   |
| ---- | ----------- | ------------ |
| 1    | STROBE      | NC           |
| 2    | AGND        | GND          |
| 3    | SIO_D       | GPIO04       |
| 4    | AVDD        | CAM_AVDD_2V8 |
| 5    | SIO_C       | GPIO05       |
| 6    | RESETB      | CAM_2V8      |
| 7    | VSYNC       | GPIO06       |
| 8    | PWDN        | GND          |
| 9    | HREF        | GPIO07       |
| 10   | DVDD        | CAM_1V3      |
| 11   | DOVDD       | CAM_2V8      |
| 12   | Y9          | GPIO15       |
| 13   | XCLK        | GPIO16       |
| 14   | Y8          | GPIO17       |
| 15   | DGND        | GND          |
| 16   | Y7          | GPIO18       |
| 17   | PCLK        | GPIO08       |
| 18   | Y6          | GPIO09       |
| 19   | Y2          | GPIO10       |
| 20   | Y5          | GPIO11       |
| 21   | Y3          | GPIO12       |
| 22   | Y4          | GPIO13       |
| 23   | Y1          | NC           |
| 24   | Y0          | NC           |

* **+3V3_CLEAN**

  * +3V3 goes through a ferrite bead (FB1 C1002)
  * then has 100nF (C8 C14663) and 1uF (C9 C15849) decoupling capacitors 

* **+CAM_2V8**

  * From +3V3_CLEAN there are 100nF (C17 C1525) and 1uF (C20 C15849) decoupling capacitors 

  * it then enters the 2.8V LDO (U3 C53099)

    | U3 2.8V LDO ME6211C28M5G-N |            |
    | -------------------------- | ---------- |
    | VIN                        | +3V3_CLEAN |
    | VSS                        | GND        |
    | CE                         | +3V3_CLEAN |
    | VOUT                       | +CAM_2V8   |

  * After the LDO there are three decoupling capacitors: 100nF (C10 C1525), 1uF (C12 C15849) and 4.7uF (C13 C1779)

* **+CAM_1V3**

  * From +3V3_CLEAN there are 100nF (C19 C1525) and 1uF (C21 C15849) decoupling capacitors 

  * it then enters the 1.3V LDO (U4 C3008051)

  * | U4 1.3V LDO LN1234B132MR-G |            |
    | -------------------------- | ---------- |
    | VIN                        | +3V3_CLEAN |
    | VSS                        | GND        |
    | CE                         | +3V3_CLEAN |
    | VOUT                       | +CAM_1V3   |

  * After the LDO there are two decoupling capacitors: 100nF (C11 C1525) and 1uF (C14 C15849) 

* **+CAM_AVDD_2V8**

  * +CAM_2V8 enters a ferrite bead (FB2 C1002)
  * then decoupling capacitors 100nf (C15 C14663) and 1uF (C16 C15849)

* **SIOD** is pulled up to +CAM_2V8 with a 4.7kΩ resistor (R7 C23162)

* **SIOC** is pulled up to +CAM_2V8 with a 4.7kΩ resistor (R13 C23162)

* **RESETB** is pulled up  to +CAM_2V8 with a 10k resistor (R10 C25804)

* **PWDN** is pulled to GND with a 10k resistor (R12 C25804)

* **XCLK1** has an inline 33Ω resistor (R11 C23140)

#### 3.3V Voltage Step Down

* Step down regulator SY8113BADC (PS1 C78989)
* EN and IN connect to +BATT a 10uF decoupling capacitor (C5 C15850)
* FB connects to:
  * 22kΩ to GND (R4 C31850)
  * 100kΩ to +3V3 (R3 C25803)
  * 22pF to +3V3 (C7 C1653) 
* GND connected to GND
* BS connects to 100nf (C6 C14663) inline to LX
* LX connects to: 
  * 4.7uH inductor (L1 C167220)
  * Two decoupling capacitors 100nf (C3 C14663) and 47uF (C4 C16780) after inductor
  * Out to +3V3
* +3V3_CLEAN
  * +3V3 branches off through a ferrite bead (FB1 C1002) with decoupling 100nF (C8 C14663) and 1uF (C9 C15849).  

#### USB Connector

USB-C connector (J2 C165948) for reprogramming the ESP32.  

| Pin  | Connected to |
| ---- | ------------ |
| VBUS | +5V_USB      |
| SBU2 | NC           |
| CC1  | GND          |
| DN2  | USB_D-       |
| DP1  | USB_D+       |
| DN1  | USB_D-       |
| DP2  | USB_D+       |
| SBU1 | NC           |
| CC2  | GND          |
| VBUS | +5V_USB      |
| EH   | GND          |

* CC1: connected to GND through 5.1 kΩ (R2 C23186) 

  CC2: connected to GND through 5.1 kΩ  (R1 C23186) 

* VBUS

  * 100nF (C1 C14663) and 10uF (C2 C15850) decoupling capacitors
  * Schottky diode (D1 C8598) to prevent power flow from +BATT into USB connection.
  * Schottky diode is oriented +5V_USB → Anode → Schottky Diode → Cathode → +BATT
  * I have found in testing that the USB is powerful enough to drive the motors, so I'm not worried about USB back powering the whole +BATT net.  
    I will only be using the USB connection for programming the ESP32 at first, then switch to OTA programming.  I will not be running the motors while on USB power.  I am aware that D1 will reduce the voltage potentially as low as 4.5V which may be marginal for the SY8113B input.  I will take that risk to keep things simple.  

* USB Data

  * Data lines USB_D+ and USB_D- are a differential pair

  * ESD protection diode (D2 C5180302) wired as follows:

    | ESD protection diode PRTR5V0U2X pin | Connection | Decoupling         |
    | ----------------------------------- | ---------- | ------------------ |
    | I/01                                | USB_D-     |                    |
    | I/02                                | USB_D+     |                    |
    | VCC                                 | +5V_USB    | 100nF (C26 C14663) |
    | GND                                 | GND        |                    |

  * There are two 22Ω series resistors on the D+ (R24 C25092) and D- (R23 C25092) lines.  


#### Slip Ring Connector

Right angle JST PH 5 pin connector (J1 C157923) allows for connection to the hull board via the slip ring.  

#### IR LEDs

Two external 3mm through-hole 940nm IR leds will be soldered manually via ~10mm wires/or legs.  The 2.54mm 1×02 pin headers are DNP as I am just using the through holes to solder wires.  

| IR LED | Connector footprint (DNP) | Inline Resistor  | Connection | Position in barrel |
| ------ | ------------------------- | ---------------- | ---------- | ------------------ |
| #1     | J4                        | 220Ω (R6 C22962) | GPIO39     | Left               |
| #2     | J5                        | 220Ω (R5 C22962) | GPIO40     | Right              |

**IR LED power tuning summary:**

I have added through hole resistors to both LEDs ti tune the IR power.  Initially, self-triggering was caused by 940 nm IR leaking through the original black PLA turret/hull. Reprinting in **eSun ePLA Matte Black** resolved the leakage issue. With no added resistor, IR range was several metres and could reflect off walls behind a target, allowing indirect hits. Testing with an added **2 kΩ** series resistor reduced range to roughly **50 cm**, which was too short. An added **20 kΩ** resistor prevented hits entirely. Based on these tests, a mid-range reduction was chosen by adding **470 Ω** in series with each IR LED, which gave a good balance between usable range and reduced wall reflections.  This gives a range of about 180cm.  

#### External RGB LED

Six external WS2812B LEDs will be soldered manually via ~30mm wires.  The 2.54mm 1×03 pin header connector (J6) is DNP as I am just using the through holes to solder wires.  

The three pins connect to +3V3, GPIO38 and GND

I have four decoupling capacitors on +3V3:

* 3 × 22uF (C29, C24, C18 C45783)
* 1 × 100nf (C25 C14663)

I have one 330Ω inline resistor (R14 C23138) on GPIO38.  

The WS2812B require 5V, however I have tested the LEDs at 3.3V and they appear to work perfectly well with an excellent level of brightness.  

#### Buzzer

An external 3V piezo buzzer (not inductive/magnetic) will be soldered manually via ~30mm wires.  The  The 2.54mm 1×02 pin header (J3) is DNP as I am just using the through holes to solder wires.  

The two pins are connected to +3V3 and a MMBT3904 transistor (Q1 C20526).  

The transistor pins are connected as follows:

| MMBT3904 Pin |           |       |
| ------------ | --------- | ----- |
| 1            | Base      | GPIO1 |
| 2            | Emitter   | GND   |
| 3            | Collector | J3    |

The connection to GPIO1 has an inline 1KΩ resistor (R8 C21190) and is pulled low with a 100kΩ resistor (R9 C25803)

#### Status LEDs

I have a red LED (D3 C2286) to show battery power. 
+BATT → Inline 2.2kΩ (R17 C25804) → Anode → LED → Cathode → GND

I have a green LED (D4 C2297) to control from GPIO48.  
+3V3 → Inline 1kΩ (R18 C21190) → Anode → LED → Cathode → GPIO48

#### Antenna

I will connect an external antenna via the IPX connector on the ESP32.  

#### Test Points

- TP1 - +3V3
- TP2 - +3V3 CLEAN
- TP3 - +BATT
- TP4 - CAM 2V8
- TP5 - CAM 1V3
- TP6 - GND
- TP7 - USB 5V
- TP8 - GND
- TP10 - CAM XCLK
- TP11 - I2C SDA
- TP12 - I2C SCL
- TP13 - BUZZER
- TP14 - IR LED 1
- TP15 - IR LED 2
- TP16 - TOP LED

### RFID Board

I am using an off-the-shelf RFID sensor module card.  The board is positioned on the bottom of the tank for reading RFID tags when it drives over them, for gameplay capture points, shield recharge etc.  This board has a RC522 on it.  I have connected it to the hull expansion port.  

| Pin  | Connection on hull board expansion                           |
| ---- | ------------------------------------------------------------ |
| IRQ  | NC                                                           |
| SDA  | SDA                                                          |
| SCL  | SCL                                                          |
| RST  | Pulled high with a through hole 10K resistor to 3.3V, soldered to the board |
| GND  | GND                                                          |
| 3.3V | 3V3                                                          |

Aliexpress listing for board:
https://www.aliexpress.com/item/32790073855.html?

------

### Software

#### **Unity program:**

 The Unity app is the **main controller for the game**. It finds the robots, keeps track of players/turns/game state, shows the UI and video feeds, and sends movement, turret, motor, and shooting commands to the robots.

#### **ESP32 code:**

 The ESP32 code is the **robot-side controller**. It connects to Unity over the network, receives commands, drives the hardware (motors, turret, LEDs, camera, IR), and sends status, heartbeats, hit data, and video back to Unity.
