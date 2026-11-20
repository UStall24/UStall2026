-- /APM/scripts/can_bridge.lua
-- ==============================================================================
-- ArduSub Raw CAN-to-RC Bridge & Command Translation Layer
-- Target: Pixhawk 4 running ArduSub v4.7.2+
-- Project: UStall ROV 2026
-- ==============================================================================
--
-- PURPOSE:
--   Translates raw CAN frames from the topside PC GUI (via USB-CAN adapter)
--   into ArduSub RC channel overrides, motor test commands, and arm/disarm actions.
--   Enables closed-loop attitude stabilization (STABILIZE) and depth hold (ALT_HOLD)
--   directly inside ArduSub while keeping topside CAN protocol 100% backwards-compatible.
--
-- REQUIRED ARDUSUB PARAMETERS (Set in QGroundControl / Mission Planner):
--   SCR_ENABLE       = 1       (Enable Lua scripting)
--   SCR_HEAP_SIZE    = 102400  (Allocate 100 KB heap for Lua scripts)
--   CAN_P1_DRIVER    = 1       (Enable CAN Port 1)
--   CAN_D1_PROTOCOL  = 10      (Set CAN Driver 1 protocol to 10 = Scripting)
--   CAN_P1_BITRATE   = 125000  (Match USB-CAN adapter bitrate: 125000 bps)
--   CAN_P2_DRIVER    = 1       (Optional: Enable CAN Port 2 for tether backup)
--   CAN_D2_PROTOCOL  = 12      (Optional: Set CAN Driver 2 protocol to 12 = Scripting2)
--   CAN_P2_BITRATE   = 125000  (Optional: CAN Port 2 bitrate: 125000 bps)
--   BRD_SAFETY_DEFLT = 0       (Disable Pixhawk physical safety switch button)
--   ARMING_SKIPCHK   = 1048062 (Skip pre-arm checks in ArduSub for bench testing, or -1 to skip all)
--   FLTMODE_CH       = 0       (Disable RC flight mode channel so overrides don't conflict)
--   FS_THR_ENABLE    = 0       (Disable throttle/radio failsafe when no physical RC receiver)
--   RC_OPTIONS       = 0       (Ensure bit 5 is 0 so RC overrides are NOT ignored)
--   RC_OVERRIDE_TIME = 3.0     (Timeout in seconds before ArduSub releases RC override)
--   MOT_SAFE_DISARM  = 1       (Keep PWM disabled while disarmed)
--
-- FLIGHT MODES (ArduSub):
--   0  = STABILIZE (Attitude stabilization via IMU: eliminates roll/pitch tipping and tether twist)
--   2  = ALT_HOLD  (Depth hold via external pressure sensor Bar30/MS5837)
--   19 = MANUAL    (Direct open-loop manual thrust, no sensor feedback)
--
-- ==============================================================================

local SCRIPT_NAME    = "ROV CAN bridge"
local SCRIPT_VERSION = "1.5"

-- CAN Driver handles (lazy initialized in update loop to prevent boot race conditions)
local can_dev1       = nil
local can_dev2       = nil
local active_can_dev = nil
local tx_frame       = nil

-- ==============================================================================
-- CAN Arbitration IDs & Safety Constants
-- (Grouped in a single table to stay well below ArduPilot Lua's 100-local limit)
-- ==============================================================================
local C = {
    CAN_BUFFER_SIZE        = 25,

    -- IDs received from PC Topside GUI (GUI -> Pixhawk)
    RX_HEALTH              = 40,
    RX_INIT_CONN           = 50,
    RX_INIT_THRUSTERS      = 60,
    RX_DEINIT_THRUST       = 70,
    RX_RESET_ERR           = 80,
    RX_CTRL_MANUAL         = 90,
    RX_CTRL_STABILIZE      = 100,
    RX_CTRL_DEPTH_HOLD     = 110,
    RX_SET_SPEED_LIMIT     = 120,
    RX_MOTOR_CONFIG        = 130,
    RX_AXIS_GAINS          = 140,
    RX_UPDATE_CYCLE        = 160,
    RX_TEST_MOTOR          = 170,

    -- IDs sent to PC Topside GUI (Pixhawk -> GUI)
    TX_HEALTH              = 41,
    TX_INIT_CONN           = 51,
    TX_INIT_THRUSTERS      = 61,
    TX_DEINIT_THRUST       = 71,
    TX_RESET_ERR           = 81,
    TX_SET_SPEED_LIMIT     = 121,
    TX_MOTOR_CONFIG        = 131,
    TX_AXIS_GAINS          = 141,
    TX_UPDATE_CYCLE        = 161,
    TX_TEST_MOTOR          = 171,

    -- Health payload markers (avoids ping-pong loops on IDs 40/41)
    HEALTH_GUI_PROBE       = 1,
    HEALTH_ROV_PING        = 2,

    -- Timing & Safety Limits
    CONTROL_DLC            = 6,
    MOTOR_TEST_DLC         = 7,
    MAX_FRAMES_PER_DEVICE  = 25,
    WATCHDOG_TIMEOUT_MS    = 500,
    HEARTBEAT_INTERVAL_MS  = 2000,
    MAX_MISSED_HEARTBEATS  = 2,
    SURFACE_THROTTLE_PWM   = 1580,
    MAX_SURFACE_DURATION_MS= 30000,
    MAX_MOTOR_TEST_MS      = 2000,
    MIN_CYCLE_TIME_MS      = 5,
    MAX_CYCLE_TIME_MS      = 100,
    MODE_RETRY_INTERVAL_MS = 1000,
}

-- ==============================================================================
-- Channel Mapping: CAN Payload Bytes -> ArduSub RC Channels
-- ==============================================================================
-- In UStall GUI (ControllerHandlerViewModel.cs):
--   data[1] = Forward / Backward
--   data[2] = Left / Right (Lateral)
--   data[3] = Up / Down (Throttle / Heave)
--   data[4] = Yaw
--   data[5] = Pitch  (from ControllerMappings[4])
--   data[6] = Roll   (from ControllerMappings[5])
--
-- In ArduSub standard RC Channels (RCMAP defaults):
--   RC1 = Roll or Pitch depending on ArduSub parameter RCMAP_ROLL / RCMAP_PITCH
--   RC2 = Pitch or Roll
--   RC3 = Throttle (RCMAP_THROTTLE = 3)
--   RC4 = Yaw      (RCMAP_YAW = 4)
--   RC5 = Forward  (RCMAP_FORWARD = 5)
--   RC6 = Lateral  (RCMAP_LATERAL = 6)
--
-- This script dynamically queries ArduSub parameters on boot and auto-adapts!
-- Default fallback mapping:
--   data[1] (FB)      -> RC5
--   data[2] (LR)      -> RC6
--   data[3] (UD)      -> RC3
--   data[4] (Yaw)     -> RC4
--   data[5] (Pitch)   -> RC2
--   data[6] (Roll)    -> RC1
local control_channels = {5, 6, 3, 4, 2, 1}
local rc_channels_initialized = false

local function init_rc_channels()
    if rc_channels_initialized then return true end
    if param and param.get then
        local ch_fwd   = param:get('RCMAP_FORWARD')
        local ch_lat   = param:get('RCMAP_LATERAL')
        local ch_thr   = param:get('RCMAP_THROTTLE')
        local ch_yaw   = param:get('RCMAP_YAW')
        local ch_pitch = param:get('RCMAP_PITCH')
        local ch_roll  = param:get('RCMAP_ROLL')
        if ch_fwd and ch_lat and ch_thr and ch_yaw and ch_pitch and ch_roll then
            control_channels = {
                math.floor(ch_fwd),
                math.floor(ch_lat),
                math.floor(ch_thr),
                math.floor(ch_yaw),
                math.floor(ch_pitch),
                math.floor(ch_roll)
            }
            rc_channels_initialized = true
            return true
        end
    end
    return false
end

-- Cache RC_Channel userdata to avoid per-tick memory allocations and GC spikes
local rc_channels = {}
local function get_rc_channel(ch_num)
    if not rc_channels[ch_num] then
        rc_channels[ch_num] = rc:get_channel(ch_num)
    end
    return rc_channels[ch_num]
end

-- Pre-cached physical servo output channels for Motors 1..6 (0-indexed)
-- Maps function k_motor1 (33) .. k_motor6 (38) to actual SERVO output index
local motor_channels = {0, 1, 2, 3, 4, 5}
local motor_channels_initialized = false

local function init_motor_channels()
    if motor_channels_initialized then return true end
    if SRV_Channels and SRV_Channels.find_channel then
        local any_found = false
        for i = 0, 5 do
            local found = SRV_Channels:find_channel(33 + i)
            if found then
                motor_channels[i + 1] = found
                any_found = true
            else
                motor_channels[i + 1] = i
            end
        end
        if any_found then
            motor_channels_initialized = true
            return true
        end
    end
    return false
end

-- Reusable static buffer for frame reception (zero heap allocations during streaming)
local rx_data = {0, 0, 0, 0, 0, 0, 0, 0}

-- Pre-allocated static response buffers (zero heap allocations during TX)
local tx_ack_payload  = {}
local tx_resp_single  = {0}
local tx_motor_cfg    = {0, 1, 2, 3, 4, 5, 6}
local axis_gains      = {100, 100, 100, 100, 100, 100} -- [FB, LR, UD, Yaw, Pitch, Roll] in %

-- ==============================================================================
-- Helper Functions & State
-- ==============================================================================
local function now_ms()
    local m = millis()
    if type(m) == "number" then
        return math.floor(m)
    elseif m and m.toint then
        return m:toint()
    end
    return 0
end

-- Safe elapsed time calculation avoiding 32-bit unsigned wrap-around issues
local function elapsed_ms(since)
    local now = now_ms()
    if now >= since then
        return now - since
    else
        -- Unsigned 32-bit millisecond counter wrap-around (approx every 49.7 days)
        return (now + 0x100000000 - since)
    end
end

local cycle_time_ms             = 10
local max_speed_percent         = 100
local last_ctrl_ms              = now_ms()
local watchdog_tripped          = true
local watchdog_warned           = false
local last_heartbeat_tx_ms      = 0
local missed_heartbeats         = 0
local heartbeat_awaiting        = false
local auto_surface_active       = false
local auto_surface_start_ms     = 0
local motor_test_active         = false
local motor_test_start_ms       = 0
local motor_test_dur_ms         = 0
local saved_mode_before_test    = 0
local was_armed_before_test     = false
local was_armed                 = false
local last_mode_change_ms       = 0

local function to_uint32(val)
    if uint32_t then
        return uint32_t(val)
    end
    return val
end

-- File-scoped CAN frame writer helper (zero closure heap allocation)
local function raw_write_frame(dev, frame, id, payload, len)
    frame:id(to_uint32(id))
    for i = 1, len do
        frame:data(i - 1, payload[i])
    end
    frame:dlc(len)
    return dev:write_frame(frame, 5000)
end

-- Transmit standard 11-bit CAN frame (reuses tx_frame to avoid GC allocations)
local function send_can(id, payload)
    payload = payload or tx_ack_payload
    local len = #payload
    if len > 8 then
        return false
    end

    local dev = active_can_dev or can_dev1 or can_dev2
    if not dev then
        return false
    end

    if not tx_frame then
        local ok, f = pcall(CANFrame)
        if not ok or not f then return false end
        tx_frame = f
    end

    local ok, result = pcall(raw_write_frame, dev, tx_frame, id, payload, len)
    return ok and result ~= false
end

-- Transmit a 1-byte CAN response using the pre-allocated static buffer
local function send_u8(id, val)
    tx_resp_single[1] = val
    return send_can(id, tx_resp_single)
end

-- Zero-allocation protected wrappers for ArduSub API calls
local function safe_set_mode(mode)
    return pcall(vehicle.set_mode, vehicle, mode)
end

local function safe_disarm()
    return pcall(arming.disarm, arming)
end

local function safe_arm()
    local ok, armed = pcall(arming.arm, arming)
    if ok and armed then
        return true
    end
    local fok, forced = pcall(arming.arm_force, arming)
    return fok and forced
end

-- Reset armed-session heartbeat & watchdog timers
local function reset_armed_timers(now)
    last_ctrl_ms = now
    last_heartbeat_tx_ms = now
    missed_heartbeats = 0
    heartbeat_awaiting = false
    auto_surface_active = false
    watchdog_tripped = false
    watchdog_warned = false
end

-- Override RC channel PWM (0 to release override)
local function set_override(channel, pwm)
    local ch = get_rc_channel(channel)
    if ch then
        ch:set_override(pwm)
    end
end

-- Neutralize all control channels to 1500 (stopped/center)
local function neutralize_controls()
    for i = 1, #control_channels do
        set_override(control_channels[i], 1500)
    end
end

-- Apply gentle upward heave (SURFACE_THROTTLE_PWM) while keeping all other axes neutral
local function apply_surface_controls()
    init_rc_channels()
    for i = 1, #control_channels do
        if i == 3 then
            set_override(control_channels[i], C.SURFACE_THROTTLE_PWM)
        else
            set_override(control_channels[i], 1500)
        end
    end
end

-- Record valid communication from topside PC and cancel auto-surface if active
local function on_topside_activity()
    heartbeat_awaiting = false
    missed_heartbeats = 0
    if auto_surface_active then
        auto_surface_active = false
        neutralize_controls()
        gcs:send_text(6, "CAN failsafe: link restored, auto-surface cancelled")
    end
end

-- Release all RC overrides back to autopilot control
local function clear_control_overrides()
    for i = 1, #control_channels do
        set_override(control_channels[i], 0)
    end
end

-- Convert 0..255 byte value to 1100..1900 RC PWM (126..129 -> 1500 neutral), scaled by max_speed_percent and axis gain
local function byte_to_rc_pwm(value, axis_gain_pct)
    if not value or (value >= 126 and value <= 129) then
        return 1500
    end
    local clamped = math.max(0, math.min(255, value))
    local norm = (clamped / 127.5) - 1.0
    local scale = (max_speed_percent / 100.0) * ((axis_gain_pct or 100) / 100.0)
    return math.floor(1500 + (norm * 400 * scale) + 0.5)
end

-- Convert 0..255 byte value to 1100..1900 motor PWM for direct output with deadzone
local function byte_to_motor_pwm(value)
    if not value or (value >= 126 and value <= 129) then
        return 1500
    end
    local clamped = math.max(0, math.min(255, value))
    return math.floor(1100 + (clamped * 800 / 255) + 0.5)
end

-- Process incoming pilot controller inputs
local function handle_controller_values(data)
    init_rc_channels()
    for i = 1, C.CONTROL_DLC do
        set_override(control_channels[i], byte_to_rc_pwm(data[i], axis_gains[i]))
    end
    last_ctrl_ms = now_ms()
    watchdog_tripped = false
    watchdog_warned = false
end

-- Force all 6 direct motor channels back to 1500 neutral immediately
local function neutralize_motor_outputs()
    for channel = 0, 5 do
        pcall(SRV_Channels.set_output_pwm_chan_timeout, SRV_Channels, motor_channels[channel + 1], 1500, 50)
    end
end

-- Cancel an active bench motor test (used on emergency disarm or external disarm)
local function cancel_motor_test()
    if not motor_test_active then return end
    motor_test_active = false
    neutralize_motor_outputs()
end

-- Direct motor test completion
local function end_motor_test()
    motor_test_active = false
    last_ctrl_ms = now_ms()
    neutralize_motor_outputs()

    -- Safety restoration: If vehicle was disarmed before test, disarm it now.
    -- If it was armed, restore the previous flight mode.
    if not was_armed_before_test then
        safe_disarm()
    else
        safe_set_mode(saved_mode_before_test)
        neutralize_controls()
    end

    send_u8(C.TX_TEST_MOTOR, 1)
    gcs:send_text(6, "CAN bridge: motor test complete")
end

-- Apply direct motor test outputs (file-scoped to avoid closure allocation in pcall)
local function apply_motor_test_outputs(data, duration_ms)
    for channel = 0, 5 do
        SRV_Channels:set_output_pwm_chan_timeout(
            motor_channels[channel + 1],
            byte_to_motor_pwm(data[channel + 1]),
            duration_ms + 50
        )
    end
end

-- Handle direct motor test command from GUI
local function handle_motor_test(data)
    on_topside_activity()
    if motor_test_active then
        gcs:send_text(4, "CAN bridge: motor test already active")
        send_u8(C.TX_TEST_MOTOR, 0)
        return
    end

    local duration_ms = data[7] * 10
    if duration_ms < 10 or duration_ms > C.MAX_MOTOR_TEST_MS then
        gcs:send_text(3, "CAN bridge: motor test duration invalid")
        send_u8(C.TX_TEST_MOTOR, 0)
        return
    end

    init_motor_channels()
    was_armed_before_test = arming:is_armed()
    saved_mode_before_test = vehicle:get_mode() or 0

    -- Always switch to MANUAL mode (19) during bench motor tests so IMU attitude controller
    -- does not fight the test or drive other thrusters
    safe_set_mode(19)
    if not was_armed_before_test then
        safe_arm()
    end

    if not arming:is_armed() then
        safe_set_mode(saved_mode_before_test)
        gcs:send_text(3, "CAN bridge: motor test arm failed")
        send_u8(C.TX_TEST_MOTOR, 0)
        return
    end

    local ok = pcall(apply_motor_test_outputs, data, duration_ms)
    if ok then
        motor_test_active = true
        motor_test_start_ms = now_ms()
        motor_test_dur_ms = duration_ms
    else
        neutralize_motor_outputs()
        if not was_armed_before_test then
            safe_disarm()
        end
        safe_set_mode(saved_mode_before_test)
        gcs:send_text(3, "CAN bridge: motor test output failed")
        send_u8(C.TX_TEST_MOTOR, 0)
    end
end

-- Get/Set motor directions (MOT_1..6_DIRECTION) and ESC port assignments (SERVO1..6_FUNCTION)
-- Frame format (7 bytes): [dir_mask, out_m1, out_m2, out_m3, out_m4, out_m5, out_m6]
--   dir_mask bit (m-1) = 1 means MOT_<m>_DIRECTION = -1 (reversed), 0 means +1 (normal)
--   out_m<m> (1..6)    = physical SERVO port assigned to k_motor<m> (function 32 + m)
local function handle_motor_config(data, dlc)
    on_topside_activity()
    if dlc >= 7 and param then
        -- Validate that all 6 output ports are in 1..6 and unique
        local used = {}
        local valid = true
        for m = 1, 6 do
            local port = math.floor(data[m + 1] or 0)
            if port < 1 or port > 6 or used[port] then
                valid = false
                break
            end
            used[port] = true
        end

        if valid then
            local save_fn = param.set_and_save or param.set
            local mask = math.floor(data[1] or 0)
            for m = 1, 6 do
                local bit = 2 ^ (m - 1)
                local reversed = (math.floor(mask / bit) % 2) == 1
                pcall(save_fn, param, "MOT_" .. tostring(m) .. "_DIRECTION", reversed and -1 or 1)
            end
            for m = 1, 6 do
                local port = math.floor(data[m + 1])
                pcall(save_fn, param, "SERVO" .. tostring(port) .. "_FUNCTION", 32 + m)
                motor_channels[m] = port - 1
            end
            motor_channels_initialized = true
            gcs:send_text(6, "CAN bridge: motor directions & ESC ports updated")
        else
            gcs:send_text(3, "CAN bridge: invalid motor config (ports must be unique 1..6)")
        end
    end

    -- Read back live parameters into static reply buffer
    local dir_mask = 0
    for m = 1, 6 do
        local dir = param and param:get("MOT_" .. tostring(m) .. "_DIRECTION") or 1
        if dir < 0 then
            dir_mask = dir_mask + (2 ^ (m - 1))
        end
        tx_motor_cfg[m + 1] = motor_channels[m] + 1
    end
    if param and param.get then
        for port = 1, 6 do
            local fn = param:get("SERVO" .. tostring(port) .. "_FUNCTION")
            if fn and fn >= 33 and fn <= 38 then
                local m = math.floor(fn) - 32
                tx_motor_cfg[m + 1] = port
                motor_channels[m] = port - 1
            end
        end
    end
    tx_motor_cfg[1] = math.floor(dir_mask)
    send_can(C.TX_MOTOR_CONFIG, tx_motor_cfg)
end

-- ==============================================================================
-- Frame Dispatcher
-- ==============================================================================
local function handle_frame(frame)
    -- Filter out extended 29-bit frames (DroneCAN traffic), error frames, and RTRs
    if frame:isExtended() or frame:isErrorFrame() or frame:isRemoteTransmissionRequest() then
        return
    end

    local rx_id = frame:id_signed()
    if rx_id < 0 or rx_id > 0x7FF then
        return
    end

    local dlc = frame:dlc()
    if dlc > 8 then dlc = 8 end
    for i = 0, dlc - 1 do
        rx_data[i + 1] = frame:data(i)
    end

    -- 1. Health Ping / Heartbeat ACK / Connection Check
    if rx_id == C.RX_HEALTH then
        on_topside_activity()
        -- Only reply with TX_HEALTH (41) if this is a GUI-initiated probe (empty or payload ~= 2).
        -- Payload [2] is the GUI's ACK to a Pixhawk-initiated heartbeat ping and must not echo back.
        if dlc == 0 or rx_data[1] ~= C.HEALTH_ROV_PING then
            send_u8(C.TX_HEALTH, C.HEALTH_GUI_PROBE)
        end

    -- 2. Init Connection to Frontend
    elseif rx_id == C.RX_INIT_CONN then
        on_topside_activity()
        send_u8(C.TX_INIT_CONN, 1)

    -- 3. Arm Thrusters / Init Motors
    elseif rx_id == C.RX_INIT_THRUSTERS then
        on_topside_activity()
        neutralize_controls()
        if dlc >= 1 then
            local req_mode = rx_data[1]
            if req_mode == 0 or req_mode == 2 or req_mode == 19 then
                safe_set_mode(req_mode)
            end
        else
            -- Legacy empty-payload arm defaults to STABILIZE (0) if currently MANUAL
            local mode = vehicle:get_mode()
            if not mode or mode == 19 then
                safe_set_mode(0)
            end
        end
        safe_arm()

        local armed = arming:is_armed()
        if armed then
            reset_armed_timers(now_ms())
            was_armed = true
            gcs:send_text(6, "CAN bridge: armed in mode " .. tostring(vehicle:get_mode()))
        else
            gcs:send_text(3, "CAN bridge: arming rejected (check ARMING_SKIPCHK & safety)")
        end
        send_u8(C.TX_INIT_THRUSTERS, armed and 1 or 0)

    -- 4. Disarm Thrusters
    elseif rx_id == C.RX_DEINIT_THRUST then
        on_topside_activity()
        cancel_motor_test()
        neutralize_controls()
        safe_disarm()
        local disarmed = not arming:is_armed()
        if disarmed then
            clear_control_overrides()
            was_armed = false
        else
            neutralize_controls()
        end
        watchdog_tripped = true
        send_u8(C.TX_DEINIT_THRUST, disarmed and 1 or 0)
        gcs:send_text(disarmed and 6 or 3, disarmed and "CAN bridge: disarmed" or "CAN bridge: disarm failed")

    -- 5. Reset Error
    elseif rx_id == C.RX_RESET_ERR then
        on_topside_activity()
        watchdog_tripped = false
        watchdog_warned = false
        last_ctrl_ms = now_ms()
        send_u8(C.TX_RESET_ERR, 1)

    -- 6. Driving / Controller Values (Manual, Stabilize, or Depth Hold)
    elseif rx_id == C.RX_CTRL_STABILIZE or rx_id == C.RX_CTRL_DEPTH_HOLD or rx_id == C.RX_CTRL_MANUAL then
        on_topside_activity()
        if not motor_test_active and dlc >= C.CONTROL_DLC then
            -- Flight mode selection from GUI:
            -- RX_CTRL_MANUAL     (90) switches to MANUAL (direct open-loop, mode 19)
            -- RX_CTRL_STABILIZE  (100) switches to STABILIZE (attitude hold, mode 0)
            -- RX_CTRL_DEPTH_HOLD (110) switches to ALT_HOLD (depth + attitude hold, mode 2)
            local target_mode = 0
            if rx_id == C.RX_CTRL_DEPTH_HOLD then
                target_mode = 2
            elseif rx_id == C.RX_CTRL_MANUAL then
                target_mode = 19
            end

            if target_mode ~= vehicle:get_mode() and elapsed_ms(last_mode_change_ms) >= C.MODE_RETRY_INTERVAL_MS then
                last_mode_change_ms = now_ms()
                safe_set_mode(target_mode)
            end

            if arming:is_armed() then
                handle_controller_values(rx_data)
            end
        end

    -- 7. Set Max Thruster Speed Limit (0..100%)
    elseif rx_id == C.RX_SET_SPEED_LIMIT then
        on_topside_activity()
        if dlc >= 1 then
            max_speed_percent = math.max(0, math.min(100, math.floor(rx_data[1])))
            gcs:send_text(6, "CAN bridge: max speed limit set to " .. tostring(max_speed_percent) .. "%")
        end
        send_u8(C.TX_SET_SPEED_LIMIT, max_speed_percent)

    -- 8. Get/Set Motor Directions & ESC Port Mapping (ID 130 -> 131)
    elseif rx_id == C.RX_MOTOR_CONFIG then
        handle_motor_config(rx_data, dlc)

    -- 9. Get/Set Per-Axis Gains (ID 140 -> 141, 6 bytes: FB, LR, UD, Yaw, Pitch, Roll in 0..100%)
    elseif rx_id == C.RX_AXIS_GAINS then
        on_topside_activity()
        if dlc >= C.CONTROL_DLC then
            for i = 1, C.CONTROL_DLC do
                axis_gains[i] = math.max(0, math.min(100, math.floor(rx_data[i])))
            end
            gcs:send_text(6, "CAN bridge: axis gains updated")
        end
        send_can(C.TX_AXIS_GAINS, axis_gains)

    -- 10. Direct Motor Test (Bench Thruster Test)
    elseif rx_id == C.RX_TEST_MOTOR then
        if dlc == C.MOTOR_TEST_DLC then
            handle_motor_test(rx_data)
        else
            gcs:send_text(3, "CAN bridge: motor test payload must be 7 bytes")
            send_u8(C.TX_TEST_MOTOR, 0)
        end

    -- 11. Update Cycle Time
    elseif rx_id == C.RX_UPDATE_CYCLE then
        on_topside_activity()
        if dlc >= 1 then
            local candidate = (dlc >= 2) and rx_data[2] or rx_data[1]
            if candidate and candidate >= C.MIN_CYCLE_TIME_MS and candidate <= C.MAX_CYCLE_TIME_MS then
                cycle_time_ms = candidate
            end
        end
        send_u8(C.TX_UPDATE_CYCLE, cycle_time_ms)
    end
end

-- File-scoped CAN drainage helper (avoids allocating function closures in fast tick loop)
local function drain_device(dev)
    if not dev then return end
    local count = 0
    while count < C.MAX_FRAMES_PER_DEVICE do
        local frame = dev:read_frame()
        if not frame then
            break
        end
        count = count + 1
        active_can_dev = dev -- remember which port is actively communicating
        local ok, err = pcall(handle_frame, frame)
        if not ok then
            gcs:send_text(3, "CAN frame error: " .. tostring(err))
        end
    end
end

-- ==============================================================================
-- Main Script Loop
-- ==============================================================================
local function update_body()
    -- Lazy initialization of CAN scripting devices (supports both CAN1 and CAN2)
    if not can_dev1 and not can_dev2 then
        local ok1, d1 = pcall(CAN.get_device, CAN, C.CAN_BUFFER_SIZE)
        local ok2, d2 = pcall(CAN.get_device2, CAN, C.CAN_BUFFER_SIZE)
        if ok1 then can_dev1 = d1 end
        if ok2 then can_dev2 = d2 end
        if not can_dev1 and not can_dev2 then
            return 1000 -- Retry every 1000ms until a CAN driver is ready
        end
        init_motor_channels()
        init_rc_channels()
        local ports_str = (can_dev1 and "CAN1 " or "") .. (can_dev2 and "CAN2" or "")
        gcs:send_text(6, "CAN bridge: driver ready on " .. ports_str .. " (buffer " .. tostring(C.CAN_BUFFER_SIZE) .. ")")
    end

    -- Check if active direct motor test has completed
    if motor_test_active and elapsed_ms(motor_test_start_ms) >= motor_test_dur_ms then
        end_motor_test()
    end

    -- Process pending incoming CAN frames from both drivers up to device limit
    drain_device(can_dev1)
    drain_device(can_dev2)

    -- Safety Watchdog, Periodic Topside Heartbeat & Auto-Surface Failsafe
    -- (Suppressed during direct motor tests to prevent override conflicts)
    local is_armed = arming:is_armed()
    if is_armed then
        if not was_armed then
            was_armed = true
            reset_armed_timers(now_ms())
        end
        if not motor_test_active then
            -- 1. Pilot Input Watchdog (500ms): Neutralize controls on stick stream stop
            if not watchdog_tripped and elapsed_ms(last_ctrl_ms) > C.WATCHDOG_TIMEOUT_MS then
                watchdog_tripped = true
                if not watchdog_warned then
                    watchdog_warned = true
                    gcs:send_text(4, "CAN watchdog: pilot timeout, controls neutralized")
                end
            end

            -- 2. Periodic Topside Heartbeat Ping (every 2000ms) & Missed-Ping Counter
            if elapsed_ms(last_heartbeat_tx_ms) >= C.HEARTBEAT_INTERVAL_MS then
                if heartbeat_awaiting then
                    missed_heartbeats = missed_heartbeats + 1
                    if missed_heartbeats >= C.MAX_MISSED_HEARTBEATS and not auto_surface_active then
                        auto_surface_active = true
                        auto_surface_start_ms = now_ms()
                        safe_set_mode(0) -- STABILIZE to keep level while surfacing
                        gcs:send_text(4, "CAN failsafe: link lost (" .. tostring(missed_heartbeats) .. " missed pings), auto-surfacing")
                    end
                end
                send_u8(C.TX_HEALTH, C.HEALTH_ROV_PING)
                heartbeat_awaiting = true
                last_heartbeat_tx_ms = now_ms()
            end

            -- 3. Apply Failsafe Output (Auto-Surface or Neutral Hold)
            if auto_surface_active then
                if elapsed_ms(auto_surface_start_ms) >= C.MAX_SURFACE_DURATION_MS then
                    auto_surface_active = false
                    missed_heartbeats = 0
                    heartbeat_awaiting = false
                    neutralize_controls()
                    safe_disarm()
                    clear_control_overrides()
                    was_armed = false
                    watchdog_tripped = true
                    gcs:send_text(4, "CAN failsafe: auto-surface max duration reached, disarmed")
                else
                    if vehicle:get_mode() ~= 0 and elapsed_ms(last_mode_change_ms) >= C.MODE_RETRY_INTERVAL_MS then
                        last_mode_change_ms = now_ms()
                        safe_set_mode(0)
                    end
                    apply_surface_controls()
                end
            elseif watchdog_tripped then
                neutralize_controls()
            end
        end
    else
        -- If disarmed externally (e.g. from GCS or failsafe), cancel any motor test and clear overrides
        if was_armed or motor_test_active then
            cancel_motor_test()
            clear_control_overrides()
            was_armed = false
            watchdog_tripped = true
            watchdog_warned = false
            auto_surface_active = false
            missed_heartbeats = 0
            heartbeat_awaiting = false
        end
    end

    return cycle_time_ms
end

-- Protected top-level runner
function update()
    local ok, delay = pcall(update_body)
    if not ok then
        gcs:send_text(3, "CAN bridge error: " .. tostring(delay))
        pcall(neutralize_controls)
        return update, 1000
    end
    return update, delay
end

gcs:send_text(6, string.format("%s v%s loaded", SCRIPT_NAME, SCRIPT_VERSION))
-- Return update() immediately so the script starts without an initial 1000ms dead delay
return update()
