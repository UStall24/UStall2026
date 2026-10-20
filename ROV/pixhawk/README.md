# Pixhawk 4 Integration & ArduSub CAN Bridge

This directory contains the translation layer, test bench, and configuration documentation for integrating the **Pixhawk 4** (running **ArduSub v4.7.2+**) with the UStall ROV topside control system over **CAN bus**.

---

## 1. System Architecture

```
+--------------------------------------------------------------+
| Topside PC (UStall GUI / Windows WPF)                        |
| Sends pilot stick inputs, motor test & configs via CAN       |
+------------------------------+-------------------------------+
                               | USB (Virtual COM)
+------------------------------v-------------------------------+
| Waveshare / USB-CAN Adapter (125 kbps)                       |
+------------------------------+-------------------------------+
                               | CAN_H / CAN_L (Tether)
+------------------------------v-------------------------------+
| Pixhawk 4: CAN Port 1 or CAN Port 2                          |
|   (CAN_P1_DRIVER=1, CAN_D1_PROTOCOL=10, CAN_P1_BITRATE=125k) |
|   (CAN_P2_DRIVER=1, CAN_D2_PROTOCOL=12, CAN_P2_BITRATE=125k) |
|   |                                                          |
|   v                                                          |
| Lua Script: /APM/scripts/can_bridge.lua (v1.0)               |
|   - Auto-detects active CAN port (CAN 1 or CAN 2)            |
|   - Zero-allocation high-frequency CAN parsing (100 Hz)      |
|   - Auto-detects ArduSub RCMAP parameters dynamically        |
|   - Manages Arming / Disarming / Flight Modes (STABILIZE/ALT)|
|   - Direct motor bench test with immediate neutral cutoff    |
|   |                                                          |
|   v                                                          |
| ArduSub Flight Controller Core (STABILIZE / ALT_HOLD)        |
|   - IMU (Gyros + Accelerometers) for auto-leveling (400 Hz)  |
|   - External Bar30 / MS5837 Pressure Sensor for Depth Hold   |
|   - AP_Motors6DOF Thruster Matrix Mixer                      |
|   |                                                          |
|   v                                                          |
| Pixhawk 4 I/O PWM OUT (MAIN 1..6)                            |
+------------------------------+-------------------------------+
                               | Servo PWM (1100..1900 us)
+------------------------------v-------------------------------+
| ESCs (Electronic Speed Controllers) -> 6x Thrusters          |
+------------------------------+-------------------------------+
```

---

## 2. Required Pixhawk / ArduSub Parameters

Set the following parameters using **QGroundControl** or **Mission Planner**:

### CAN & Scripting Configuration
* `SCR_ENABLE` = `1` (Enable Lua scripting engine)
* `SCR_HEAP_SIZE` = `102400` (Allocate 100 KB heap for Lua scripts)
* `CAN_P1_DRIVER` = `1` (Enable CAN Port 1)
* `CAN_D1_PROTOCOL` = `10` (Assign CAN Driver 1 to **Scripting**)
* `CAN_P1_BITRATE` = `125000` (125 kbps matching the UStall USB-CAN adapter)
* `CAN_P2_DRIVER` = `1` *(Optional: Enable CAN Port 2 for tether backup)*
* `CAN_D2_PROTOCOL` = `12` *(Optional: Assign CAN Driver 2 to **Scripting2** - NOT 10)*
* `CAN_P2_BITRATE` = `125000` *(Optional: 125 kbps)*

### Safety & Arming Configuration (Bench Testing without RC/Sensors)
* `ARMING_SKIPCHK` = `1048062` (Skip pre-arm checks in ArduSub during bench test, or `-1` to skip all)
* `BRD_SAFETY_DEFLT` = `0` (Disables physical safety switch button on Pixhawk 4)
* `MOT_SAFE_DISARM` = `0` (Keeps PWM disabled while disarmed)
* `FLTMODE_CH` = `0` (Disables flight mode RC channel so RC overrides cannot accidentally change modes)
* `FS_THR_ENABLE` = `0` (Disables throttle/RC failsafe since no physical RC receiver is plugged in)

### RC Overrides & Dynamic Channel Map
* `RC_OPTIONS` = `0` (Ensure Bit 5 [Ignore Overrides] is NOT set)
* `RC_OVERRIDE_TIME` = `3.0` (Failsafe timeout in seconds for RC overrides)

> [!NOTE]
> In factory default ArduSub: `RCMAP_PITCH = 1` and `RCMAP_ROLL = 2`. In standard ArduCopter: `RCMAP_ROLL = 1` and `RCMAP_PITCH = 2`.
> The Lua translation bridge `can_bridge.lua` dynamically reads `param:get('RCMAP_*')` at boot and auto-adapts to your configuration automatically.
* `RCMAP_FORWARD` = `5` (Surge input)
* `RCMAP_LATERAL` = `6` (Sway input)
* `RCMAP_THROTTLE` = `3` (Heave input)
* `RCMAP_YAW` = `4` (Yaw input)

### External Depth Sensor (Bar30 / MS5837 on I2C Port)
* `BARO_PROBE_EXT` = `1` (Enable probing external I2C bus)
* `BARO_EXT_BUS` = `0` or `1` (I2C bus number corresponding to external I2C port on Pixhawk 4)

---

## 3. Addressing Root Issues from the Old Pico Setup

### Issue 1: "ROV tips over when driving forwards"
* **Cause in old system:** Forward thrust vectors produce a pitch torque around the center of mass/drag that open-loop Pico scripts could not counteract.
* **ArduSub Solution:** In `STABILIZE` mode (mode 0) or `ALT_HOLD` mode (mode 2), ArduSub's internal IMU (gyro + accel) runs a closed-loop PID controller at 400 Hz. When forward thrust causes pitch angle error, ArduSub automatically adds corrective vertical thrust to thrusters, keeping the ROV level.

### Issue 2: "ROV twists with Tether"
* **Cause in old system:** Tether torque rotates the hull along the yaw/roll axis.
* **ArduSub Solution:** ArduSub's rate and heading controllers actively oppose external torque. In `STABILIZE` and `ALT_HOLD`, releasing the yaw stick locks the current heading, continuously applying differential horizontal thrust to prevent tether twisting.

### Issue 3: "Hard to hold depth by hand"
* **Cause in old system:** Manual joystick heave commands without closed-loop pressure feedback.
* **ArduSub Solution:** Connect an external I2C pressure sensor (e.g., **Bar30 / Keller / MS5837**) to the Pixhawk 4 `I2C` port. Switch to `ALT_HOLD` (mode 2, accessible via GUI Dynamic Throttle toggle). ArduSub maintains target depth automatically; pilot stick inputs change target depth at a controlled ascent/descent rate. Returning sticks to center locks the current depth.

### Issue 4: "Not all available power for thrusters utilized"
* **Cause in old system:** Open-loop fixed motor weights and simple clipping in `calculator.py`.
* **ArduSub Solution:** ArduSub includes full 6-DOF dynamic mixer allocation (`AP_Motors6DOF`) that scales and prioritizes attitude stabilization (roll/pitch/yaw) over translation (forward/lateral/throttle) when thrusters saturate, utilizing 100% of available ESC headroom safely.

---

## 4. Recompiling ArduSub with UStall Custom Thruster Layout

ArduSub supports custom thruster geometries by configuring `SUB_FRAME_CUSTOM` (frame type 7) in `libraries/AP_Motors/AP_Motors6DOF.cpp`.



### 3-Vertical Thruster Delta Formation (120°) + 3-Horizontal
*(Described in the Control Strategy Paper, Section 1.5)*

* **Geometry:** Vertical thrusters arranged symmetrically at 120°:
  - **T1 / M5 (Front Center):** Pitch nose UP/DOWN (+1.0 pitch), heave UP (1.0 throttle)
  - **T2 / M6 (Rear Left):** Pitch nose (-0.5 pitch), Roll RIGHT (+0.866 roll), heave UP (1.0 throttle)
  - **T3 / M4 (Rear Right):** Pitch nose (-0.5 pitch), Roll LEFT (-0.866 roll), heave UP (1.0 throttle)
* **Horizontal Thrusters (M1, M2, M3):** Arranged for Forward, Lateral, and Yaw control.

In `libraries/AP_Motors/AP_Motors6DOF.cpp`:
```cpp
case SUB_FRAME_CUSTOM:
    // Format: add_motor_raw_6dof(motor_num, roll, pitch, yaw, throttle, forward, lateral, test_order);
    // --- Horizontal Thrusters ---
    // Motor 1: Forward Left (Forward +, Yaw +)
    add_motor_raw_6dof(AP_MOTORS_MOT_1,  0.0f,  0.0f,  1.0f,  0.0f,  1.0f,  0.0f, 1);
    // Motor 2: Forward Right (Forward +, Yaw -)
    add_motor_raw_6dof(AP_MOTORS_MOT_2,  0.0f,  0.0f, -1.0f,  0.0f,  1.0f,  0.0f, 2);
    // Motor 3: Lateral Strafe (Lateral +)
    add_motor_raw_6dof(AP_MOTORS_MOT_3,  0.0f,  0.0f,  0.0f,  0.0f,  0.0f,  1.0f, 3);

    // --- Vertical Thrusters (120 deg Delta Layout) ---
    // Motor 4: Rear-Right (Roll -, Pitch -0.5, Heave +)
    add_motor_raw_6dof(AP_MOTORS_MOT_4, -0.866f, -0.5f, 0.0f,  1.0f,  0.0f,  0.0f, 4);
    // Motor 5: Front-Center (Roll 0, Pitch +1.0, Heave +)
    add_motor_raw_6dof(AP_MOTORS_MOT_5,  0.0f,    1.0f, 0.0f,  1.0f,  0.0f,  0.0f, 5);
    // Motor 6: Rear-Left (Roll +, Pitch -0.5, Heave +)
    add_motor_raw_6dof(AP_MOTORS_MOT_6,  0.866f, -0.5f, 0.0f,  1.0f,  0.0f,  0.0f, 6);
    break;
```

---

### Compilation & Flashing Steps:
1. Clone ArduSub (v4.7.2 tag or ArduPilot master):
   ```bash
   git clone --recurse-submodules https://github.com/ArduPilot/ardupilot.git
   cd ardupilot
   git checkout Sub-4.7.2
   git submodule update --init --recursive
   ```
2. Modify `libraries/AP_Motors/AP_Motors6DOF.cpp` with your desired `case SUB_FRAME_CUSTOM:` block.
3. Configure and compile for Pixhawk 4:
   ```bash
   ./waf configure --board Pixhawk4
   ./waf sub
   ```
4. Output file: `build/Pixhawk4/bin/ardusub.apj`
5. Connect Pixhawk 4 via USB, open **QGroundControl** or **Mission Planner**, navigate to Firmware Setup, select **Custom Firmware**, and flash `ardusub.apj`.
6. Set parameter `FRAME_CONFIG` = `7` (`SUB_FRAME_CUSTOM`).

---

## 5. File Location on Pixhawk SD Card

Place `can_bridge.lua` on the Pixhawk's micro-SD card at:
```
/APM/scripts/can_bridge.lua
```

Reboot the Pixhawk. Monitor the GCS (QGroundControl / Mission Planner) message window for:
```
ROV CAN bridge v1.0 loaded
CAN bridge: driver ready on CAN1 (buffer 25)
```

---

## 6. Running the Automated Test Bench

An automated test bench has been developed in `ROV/pixhawk/test_bench/` to verify every CAN frame, RC override, motor test, mode switch, and failsafe without needing physical hardware connected.

### Test Bench Files:
* `test_bench/mock_ardupilot.lua`: Comprehensive mock of all ArduSub Lua runtime globals (`CAN`, `CANFrame`, `rc`, `SRV_Channels`, `arming`, `vehicle`, `gcs`, `millis`, `param`).
* `test_bench/run_test_bench.py`: Automated Python runner executing 22 end-to-end unit and stress tests.
* `test_bench/test_can_bridge.lua`: Standalone pure-Lua test runner (for SITL or local Lua CLI).

### Verified Test Cases:
1. **Initial Load & GCS Greeting:** Validates boot sequence and driver discovery.
2. **Health Ping (ID 40 -> 41):** Topside connection probe and ACK payload `[1]`.
3. **Init Connection (ID 50 -> 51):** Handshake confirmation.
4. **Init Thrusters Arming (ID 60 -> 61, Mode 0):** Force-arm and default `STABILIZE` mode entry.
5. **Pilot Drive Static Throttle (ID 100):** Correct RC overrides on channels 1..6, attitude stabilization active.
6. **Pilot Drive Dynamic Throttle (ID 110):** Mode transition to `ALT_HOLD` (mode 2) for automatic depth hold.
7. **Watchdog Timeout Failsafe:** Pilot packet loss > 500ms immediately neutralizes all thrusters to 1500 us.
8. **Watchdog Error Reset & Resume (ID 80 -> 81):** Resumes control on error reset or new stick stream.
9. **Deinit Thrusters Disarming (ID 70 -> 71):** Disarms vehicle and releases RC overrides back to autopilot (0 us).
10. **Direct Motor Bench Test (ID 170 -> 171):** Isolated pulse in `MANUAL` mode (19), automatic neutral restoration and disarm return.
11. **Manual Motor Calibration Timeout:** Auto-disarms after 300ms if GUI calibration slider drops.
12. **Direction Values Table & Delayed ACK (IDs 120-135):** Immediate DoF ACKs + 100ms non-blocking delayed `All_DoF_Saved` (127).
13. **Loop Cycle Time Adjustment (ID 160 -> 161):** Dynamic cycle tuning (clamped to 5..100 ms).
14. **Dual CAN Port Redundancy:** Automatic port switching and reply routing between CAN1 and CAN2.
15. **DroneCAN & RTR Filtering:** Rejection of 29-bit extended frames, RTRs, and error frames.
16. **Long-Run Streaming Heap Stability:** Verifies zero memory leaks or GC churn during high-frequency streaming.
17. **Mode Switch Retry on Sensor Delay:** Recovers gracefully if pressure sensor is delayed during startup.
18. **Isolation of Active Bench Motor Test:** Prevents driving frames from knocking vehicle out of test mode.
19. **All_DoF_Saved Payload Compatibility:** Preserves byte `[1]` payload matching Pi Pico behavior.
20. **Dynamic RCMAP Auto-Detection:** Dynamically adapts to both ArduSub factory defaults (`RCMAP_PITCH=1`, `RCMAP_ROLL=2`) and custom maps.
21. **Multi-Packet Calibration Stream Auto-Disarm:** Prevents state pollution during GUI slider drag streams and guarantees safe disarm.
22. **Concurrent Motor Test Overlap Rejection:** Guards active bench test from accidental overlapping test pulses and mode corruption.
