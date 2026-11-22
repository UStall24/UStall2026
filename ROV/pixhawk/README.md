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
|   (CAN_P2_DRIVER=2, CAN_D2_PROTOCOL=12, CAN_P2_BITRATE=125k) |
|   |                                                          |
|   v                                                          |
| Lua Script: /APM/scripts/can_bridge.lua (v1.5)               |
|   - Auto-detects active CAN port (CAN 1 or CAN 2)            |
|   - Zero-allocation high-frequency CAN parsing (100 Hz)      |
|   - Auto-detects ArduSub RCMAP parameters dynamically        |
|   - Manages Arming / Disarming / Flight Modes (MAN/STAB/ALT) |
|   - Direct motor bench test with immediate neutral cutoff    |
|   |                                                          |
|   v                                                          |
| ArduSub Flight Controller Core (MANUAL / STABILIZE / ALT)    |
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
* `CAN_P2_DRIVER` = `2` *(Optional: Assign CAN Port 2 to Driver 2 for tether backup)*
* `CAN_D2_PROTOCOL` = `12` *(Optional: Assign CAN Driver 2 to **Scripting2** - NOT 10)*
* `CAN_P2_BITRATE` = `125000` *(Optional: 125 kbps)*

### Safety & Arming Configuration (Bench Testing without RC/Sensors)
* `ARMING_SKIPCHK` = `1048062` (Skip pre-arm checks in ArduSub during bench test, or `-1` to skip all)
* `BRD_SAFETY_DEFLT` = `0` (Disables physical safety switch button on Pixhawk 4)
* `MOT_SAFE_DISARM` = `1` (Keeps PWM disabled while disarmed; `0` outputs neutral 1500 µs while disarmed)
* `FLTMODE_CH` = `0` (Disables flight mode RC channel so overrides cannot accidentally change modes)
* `FS_THR_ENABLE` = `0` (Disables throttle/RC failsafe since no physical RC receiver is plugged in)
* `FS_GCS_ENABLE` = `0` and `FS_PILOT_INPUT` = `0` (Disabled in `default.params` for CAN-only operation without MAVLink GCS, since `can_bridge.lua` provides the 500 ms pilot watchdog and 2-missed-ping auto-surface failsafe)

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
* `BARO_PROBE_EXT` = `768` (Bitmask `256 | 512` enabling MS5837 + MS5803 external I2C pressure sensors for Bar30)
* `BARO_EXT_BUS` = `1` (I2C bus number corresponding to external I2C port on Pixhawk 4)

---

## 3. Recompiling ArduSub with UStall Custom Thruster Layout

ArduSub supports custom thruster geometries by configuring `SUB_FRAME_CUSTOM` (frame type 7) in `libraries/AP_Motors/AP_Motors6DOF.cpp`.



### 3-Vertical Thruster Delta Formation (120°) + 3-Horizontal (Clockwise 1–6)
*(Described in the Control Strategy Paper, Section 1.5)*

Numbered **clockwise 1–6** looking from above with Front at 12 o'clock:
* **Vertical Thrusters (M1, M3, M5):** Arranged symmetrically at 120°:
  - **M1 (Front Center):** Pitch nose UP/DOWN (`+1.0` pitch), heave UP (`+1.0` throttle)
  - **M3 (Rear Right):** Pitch nose (`-0.5` pitch), Roll LEFT (`-0.866` roll), heave UP (`+1.0` throttle)
  - **M5 (Rear Left):** Pitch nose (`-0.5` pitch), Roll RIGHT (`+0.866` roll), heave UP (`+1.0` throttle)
* **Horizontal Thrusters (M2, M4, M6):**
  - **M2 (Front Right):** Forward/Backward (`+1.0` forward, `-1.0` yaw)
  - **M4 (Rear Center):** Lateral Strafe (`+1.0` lateral)
  - **M6 (Front Left):** Forward/Backward (`+1.0` forward, `+1.0` yaw)

In `libraries/AP_Motors/AP_Motors6DOF.cpp`:
```cpp
case SUB_FRAME_CUSTOM:
    // Format: add_motor_raw_6dof(motor_num, roll, pitch, yaw, throttle, forward, lateral, test_order);
    // Clockwise 1..6 (1, 3, 5 = Vertical 120 deg Delta; 2, 4, 6 = Horizontal)
    // Motor 1: Front-Center (Roll 0, Pitch +1.0, Heave +)
    add_motor_raw_6dof(AP_MOTORS_MOT_1,  0.0f,    1.0f,  0.0f,  1.0f,  0.0f,  0.0f, 1);
    // Motor 2: Forward Right (Forward +, Yaw -)
    add_motor_raw_6dof(AP_MOTORS_MOT_2,  0.0f,    0.0f, -1.0f,  0.0f,  1.0f,  0.0f, 2);
    // Motor 3: Rear-Right (Roll -, Pitch -0.5, Heave +)
    add_motor_raw_6dof(AP_MOTORS_MOT_3, -0.866f, -0.5f,  0.0f,  1.0f,  0.0f,  0.0f, 3);
    // Motor 4: Lateral Strafe (Lateral +)
    add_motor_raw_6dof(AP_MOTORS_MOT_4,  0.0f,    0.0f,  0.0f,  0.0f,  0.0f,  1.0f, 4);
    // Motor 5: Rear-Left (Roll +, Pitch -0.5, Heave +)
    add_motor_raw_6dof(AP_MOTORS_MOT_5,  0.866f, -0.5f,  0.0f,  1.0f,  0.0f,  0.0f, 5);
    // Motor 6: Forward Left (Forward +, Yaw +)
    add_motor_raw_6dof(AP_MOTORS_MOT_6,  0.0f,    0.0f,  1.0f,  0.0f,  1.0f,  0.0f, 6);
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

## 4. File Location on Pixhawk SD Card

Place `can_bridge.lua` on the Pixhawk's micro-SD card at:
```
/APM/scripts/can_bridge.lua
```

Reboot the Pixhawk. Monitor the GCS (QGroundControl / Mission Planner) message window for:
```
ROV CAN bridge v1.5 loaded
CAN bridge: driver ready on CAN1 (buffer 25)
```

---

## 5. CAN ID Protocol Reference

All communication uses **11-bit standard CAN frames** at **125 kbps** between the topside PC (`LCECommunicationHelper.cs`) and `can_bridge.lua`.

| Command | TX ID (GUI -> Pixhawk) | RX ID (Pixhawk -> GUI) | TX Payload | RX Payload | Description / ArduSub Action |
| :--- | :---: | :---: | :--- | :--- | :--- |
| **Health Ping (GUI Probe)** | `40` | `41` | Empty (`0 B`) or `[1]` | `[1, armed, mode]` | Manual connection probe initiated by GUI; returns live armed state (`0`/`1`) and active ArduSub mode (`0`/`2`/`19`). |
| **Health Ping (ROV Heartbeat)** | `40` (ACK) | `41` (Ping) | `[2]` | `[2, armed, mode]` | Sent by Pixhawk every `2 s` while armed with live armed/mode telemetry; GUI auto-replies `40` with `[2]`. After 2 missed pings, ROV auto-surfaces (`1540 µs` gentle heave in `STABILIZE`, capped by global speed limit). |
| **Init Connection** | `50` | `51` | Empty (`0 B`) | `[1]` | Initial topside handshake (leaves vehicle disarmed). |
| **Arm Thrusters** | `60` | `61` | Empty (`0 B`) or `1 B`: `[mode]` | `[1]` = OK, `[0]` = Fail | Neutralizes RC channels, sets requested mode (`0`/`2`/`19`, or `STABILIZE` if empty & in `MANUAL`), and arms (`arm()` / `arm_force()`). |
| **Disarm Thrusters** | `70` | `71` | Empty (`0 B`) | `[1]` = OK, `[0]` = Fail | Disarms vehicle and releases all RC overrides (`0 µs`). |
| **Reset Watchdog** | `80` | `81` | Empty (`0 B`) | `[1]` | Clears pilot timeout watchdog flag and resets timer. |
| **Drive (Manual)** | `90` | — | `6 B`: `[FB, LR, UD, Yaw, Pitch, Roll]` | *None* | Switches to `MANUAL` (mode `19`) for direct open-loop control and updates RC overrides. |
| **Drive (Stabilize)** | `100` | — | `6 B`: `[FB, LR, UD, Yaw, Pitch, Roll]` | *None* | Switches to `STABILIZE` (mode `0`) and updates RC overrides (`1100..1900 µs`). |
| **Drive (Depth Hold)** | `110` | — | `6 B`: `[FB, LR, UD, Yaw, Pitch, Roll]` | *None* | Switches to `ALT_HOLD` (mode `2`) for automatic depth + attitude hold and updates RC overrides. |
| **Speed Limit (Get/Set)** | `120` | `121` | Empty (`0 B`) = Get; `1 B`: `[percent]` (`0..100`) = Set | `[percent]` | Reads or saves global max thruster speed limit (`0..100%`) to `MOT_PWM_MIN` / `MOT_PWM_MAX` (`1500 ± round(400 * pct / 100)`) via `param:set_and_save`, capping all flight modes, stabilization, auto-surface, and bench motor tests across reboots. |
| **Motor Config (Get/Set)** | `130` | `131` | Empty (`0 B`) = Get; `7 B`: `[dir_mask, out_m1..out_m6]` = Set | `7 B`: `[dir_mask, out_m1..out_m6]` | Reads or saves `MOT_1..6_DIRECTION` (`dir_mask` bits `0..5`: `1` = reversed `-1`) and `SERVO1..6_FUNCTION` (`out_m1..out_m6` = unique `1..6`) via `param:set_and_save`. |
| **Axis Gains (Get/Set)** | `140` | `141` | Empty (`0 B`) = Get; `6 B`: `[FB, LR, UD, Yaw, Pitch, Roll]` (`0..100`) = Set | `6 B`: `[FB, LR, UD, Yaw, Pitch, Roll]` | Reads or saves per-axis pilot RC scaling (`0..100%`) to `SCR_USER1..6` (`gain + 1`). |
| **PID Parameter (Get/Set)** | `150` | `151` | `1 B`: `[param_id]` = Get; `6 B`: `[param_id, save_flag, float32_le]` = Set | `6 B`: `[param_id, status, float32_le]` | Reads or saves indexed Attitude (`ATC_ANG_*`, `ATC_RAT_*`) and Depth Hold (`PSC_D_*`) PID parameters (`param_id` `1..22`) via `param:set_and_save` / `param:set` with range validation. |
| **Update Cycle Time** | `160` | `161` | `1 B`: `[ms]` (`5..100`) | `[ms]` | Updates the Lua bridge loop interval in milliseconds (default `10 ms`). |
| **Bench Motor Test** | `170` | `171` | `7 B`: `[M1..M6, dur_10ms]` | `[1]` = Done, `[0]` = Busy/Err | Temporarily switches to `MANUAL` (`19`), pulses selected motor PWM (respecting `SERVO1..6_FUNCTION` port mapping and `MOT_1..6_DIRECTION`) for `dur_10ms * 10 ms` (max `2000 ms`), then restores neutral (`1500 µs`) and prior arm/mode state. |

* **Stick & Motor Byte Encoding:** `0` -> `1100 µs` (full reverse), `127`/`128` -> `1500 µs` (neutral with deadband), `255` -> `1900 µs` (full forward).

---

## 6. Topside GUI Operating Checklist

1. **Connect CAN Adapter:**
   * Plug in the USB-CAN adapter (`USB-SERIAL CH340`) and power on the ROV.
   * In **Pixhawk Quick Setup** (bottom-right of Camera View) or the **Pixhawk Setup** tab, click **Connect CAN**.
   * Verify the console logs `Connected to Pixhawk on COMx (Disarmed - click Arm Thrusters when ready).`
2. **Connect Controller / Enable Input:**
   * Plug in the Xbox controller and click **Connect to Controller** (or enable **Keyboard** in the **Controller Settings** tab).
   * Ensure **Control Input** is set to `Controller`.
3. **Select Flight Mode & Speed Limit:**
   * Choose the desired **Flight Mode**:
     * `STABILIZE` *(Default)*: Active IMU roll/pitch leveling and heading hold.
     * `DEPTH_HOLD`: Automatic depth hold (via external pressure sensor) + attitude hold.
     * `MANUAL`: Direct open-loop thruster control without sensor stabilization.
   * Adjust the **Speed Limit** slider (`0–100%`) to cap maximum thruster output for indoor tank testing or precision maneuvers.
4. **Arm & Drive:**
   * Click **Arm Thrusters** when the water around the ROV is clear.
   * Drive using the controller sticks. If CAN packets stop for `> 500 ms`, the onboard watchdog neutralizes all thrusters to `1500 µs`. If 2 consecutive health pings (`ID 41 [2]` every `2 s`) go unanswered by the topside PC (`ID 40 [2]`), the ROV switches to `STABILIZE` and gently surfaces (`1540 µs` upward throttle, capped by `MOT_PWM_MAX`) for up to `30 s` (or until communication returns).
   * Click **Disarm Thrusters** immediately after surfacing or before handling the ROV.
5. **Thruster Config & Bench Testing (Optional):**
   * Open the **Thruster Config** tab to pull/apply per-thruster directions (`MOT_1..6_DIRECTION`), ESC port mappings (`OUT1–OUT6`), and per-axis gains (`Forward/Lateral/Heave/Yaw/Pitch/Roll`), or enter a target PWM (`1100–1900 µs`, default `1560 µs`) and click **Test** on `M1`–`M6` to pulse individual thrusters for `1.5 s`.

---

## 7. Running the Automated Test Bench

An automated test bench is located in `ROV/pixhawk/test_bench/` to verify every CAN frame, RC override, motor test, mode switch, and failsafe without needing physical hardware connected.

### How to Run:

From the repository root, run the Python test harness (requires 64-bit Lua 5.5 DLL at `C:\Program Files\lua-5.5.0_Win64_bin\lua55.dll`):
```powershell
python ROV/pixhawk/test_bench/run_test_bench.py
```

Or run the standalone pure-Lua suite directly with a standard `lua` interpreter:
```powershell
lua ROV/pixhawk/test_bench/test_can_bridge.lua
```

### Test Bench Files:
* `test_bench/mock_ardupilot.lua`: Comprehensive mock of all ArduSub Lua runtime globals (`CAN`, `CANFrame`, `rc`, `SRV_Channels`, `arming`, `vehicle`, `gcs`, `millis`, `param`).
* `test_bench/run_test_bench.py`: Automated Python runner executing 32 end-to-end unit and stress tests.
* `test_bench/test_can_bridge.lua`: Standalone pure-Lua test runner (for SITL or local Lua CLI).

### Verified Test Cases:
1. **Initial Load & GCS Greeting:** Validates boot sequence and driver discovery.
2. **Health Ping (ID 40 -> 41):** Topside connection probe and ACK payload `[1]`.
3. **Init Connection (ID 50 -> 51):** Handshake confirmation.
4. **Init Thrusters Arming (ID 60 -> 61, Mode 0):** Force-arm and default `STABILIZE` mode entry.
5. **Pilot Drive Stabilize Mode (ID 100):** Correct RC overrides on channels 1..6, attitude stabilization (`STABILIZE`, mode 0) active.
6. **Pilot Drive Depth Hold Mode (ID 110):** Mode transition to `ALT_HOLD` (mode 2) for automatic depth + attitude hold.
7. **Watchdog Timeout Failsafe:** Pilot packet loss > 500ms immediately neutralizes all thrusters to 1500 us.
8. **Watchdog Error Reset & Resume (ID 80 -> 81):** Resumes control on error reset or new stick stream.
9. **Deinit Thrusters Disarming (ID 70 -> 71):** Disarms vehicle and releases RC overrides back to autopilot (0 us).
10. **Direct Motor Bench Test (ID 170 -> 171):** Isolated pulse in `MANUAL` mode (19), automatic neutral restoration and disarm return.
11. **Loop Cycle Time Adjustment (ID 160 -> 161):** Dynamic cycle tuning (clamped to 5..100 ms).
12. **Dual CAN Port Redundancy:** Automatic port switching and reply routing between CAN1 and CAN2.
13. **DroneCAN & RTR Filtering:** Rejection of 29-bit extended frames, RTRs, and error frames.
14. **Long-Run Streaming Heap Stability:** Verifies zero memory leaks or GC churn during high-frequency streaming.
15. **Mode Switch Retry on Sensor Delay:** Recovers gracefully if pressure sensor is delayed during startup.
16. **Isolation of Active Bench Motor Test:** Prevents driving frames from knocking vehicle out of test mode.
17. **Dynamic RCMAP Auto-Detection:** Dynamically adapts to both ArduSub factory defaults (`RCMAP_PITCH=1`, `RCMAP_ROLL=2`) and custom maps.
18. **Concurrent Motor Test Overlap Rejection:** Guards active bench test from accidental overlapping test pulses and mode corruption.
19. **Pilot Drive Manual Mode (ID 90):** Mode transition to `MANUAL` (mode 19) for direct open-loop control without IMU/depth stabilization.
20. **Auto-Surface on 2 Missed Health Pings & Recovery (ID 41 [2] -> 40 [2]):** Switches to `STABILIZE` and applies `1580 µs` upward throttle after 2 unanswered pings, cancelling immediately on link recovery without echoing.
21. **Health ACK Idle Keep-Alive & Max-Surface Disarm:** Verifies `ID 40 [2]` auto-replies prevent surfacing while idle, and verifies automatic disarm after `30 s` of surfacing.
22. **Arm in Requested Mode & Pixhawk Speed Limit (ID 60 [mode], ID 120 -> 121):** Verifies arming directly into `MANUAL`/`STABILIZE`/`ALT_HOLD` and onboard speed-limit scaling (`0–100%`).
23. **Emergency Disarm Cancels Active Motor Test (ID 70):** Verifies disarming mid-test immediately neutralizes motor outputs and suppresses stale `ID 171` completion frames.
24. **Motor Test Failure Recovery:** Verifies arm failure or output error cleanly restores prior flight mode and disarms if previously disarmed.
25. **Empty Cycle Time Frame Guard (ID 160):** Verifies a 0-byte `ID 160` frame does not read stale data from the reusable RX buffer.
26. **External Arm Timer Reset:** Verifies arming from GCS cleanly initializes watchdog and heartbeat timers without immediate false triggers.
27. **External Disarm Cancels Active Motor Test:** Verifies an external GCS disarm mid-test neutralizes motor outputs and cancels pending completion.
28. **Motor Config Directions & ESC Port Mapping (ID 130 -> 131):** Verifies live Get/Set of `MOT_1..6_DIRECTION` and `SERVO1..6_FUNCTION`, remapped motor test routing, and duplicate-port rejection.
29. **Per-Axis Gains Get/Set & Combined Scaling (ID 140 -> 141):** Verifies per-axis RC gain scaling (`0–100%`) and multiplicative combination with global speed limit.
30. **Active CAN Port Isolation:** Verifies non-bridge traffic on the backup CAN port (e.g., DroneCAN extended frames or gripper `0x301` frames) does not hijack reply/heartbeat routing.
31. **Rapid Flight Mode Switching:** Verifies switching between `MANUAL`, `STABILIZE`, and `ALT_HOLD` takes effect immediately without being throttled by the 1 s retry timer.
32. **Arm Guard During Bench Motor Test:** Verifies `ID 60` (Arm) is cleanly rejected while a direct bench motor test is active so test isolation and post-test disarm restoration remain intact.
33. **Attitude & Depth Hold PID Get/Set & Validation (ID 150 -> 151):** Verifies IEEE 754 float32 Get/Set round-trips for indexed `ATC_*` / `PSC_D_*` PID parameters, live tuning while armed, and rejection of out-of-range, `NaN`, or unknown parameter IDs.
