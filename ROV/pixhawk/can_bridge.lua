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
--   MOT_SAFE_DISARM  = 0       (Keep PWM disabled while disarmed)
--
-- FLIGHT MODES (ArduSub):
--   0  = STABILIZE (Attitude stabilization via IMU: eliminates roll/pitch tipping and tether twist)
--   2  = ALT_HOLD  (Depth hold via external pressure sensor Bar30/MS5837)
--   19 = MANUAL    (Direct open-loop manual thrust, no sensor feedback)
--
-- ==============================================================================

local SCRIPT_NAME    = "ROV CAN bridge"
local SCRIPT_VERSION = "1.3"

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
    RX_MANUAL_CTRL         = 90,
    RX_CTRL_STABILIZE      = 100,
    RX_CTRL_DEPTH_HOLD     = 110,
    RX_SET_FB              = 120,
    RX_SET_PITCH           = 125,
    RX_GET_FB              = 130,
    RX_GET_PITCH           = 135,
    RX_PULL_PID            = 140,
    RX_APPLY_PID           = 150,
    RX_UPDATE_CYCLE        = 160,
    RX_TEST_MOTOR          = 170,
    RX_CTRL_MANUAL         = 180,

    -- IDs sent to PC Topside GUI (Pixhawk -> GUI)
    TX_HEALTH              = 41,
    TX_INIT_CONN           = 51,
    TX_INIT_THRUSTERS      = 61,
    TX_DEINIT_THRUST       = 71,
    TX_RESET_ERR           = 81,
    TX_MANUAL_CTRL         = 91,
    TX_ALL_DOF_SAVED       = 127,
    TX_PULL_PID            = 141,
    TX_APPLY_PID           = 151,
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
    MANUAL_CTRL_TIMEOUT_MS = 300,
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

-- Direction Values storage in RAM (initialized with default ROV layout from directionValuesDefault.json)
-- Byte values: 0 = -1.0 thrust, 127 = 0.0 thrust, 255 = +1.0 thrust
local received_dofs = {false, false, false, false, false, false}
local dof_table = {
    {255,   0, 127, 127, 127, 127}, -- Forward/back: M1=+1.0, M2=-1.0
    {127, 127, 255,   0, 127, 127}, -- Left/right:   M3=+1.0, M4=-1.0
    {127, 127, 127, 127, 255,   0}, -- Up/down:      M5=+1.0, M6=-1.0
    {  0, 255, 127, 127, 127, 127}, -- Yaw:          M1=-1.0, M2=+1.0
    {127, 127,   0, 255, 127, 127}, -- Roll:         M3=-1.0, M4=+1.0
    {127, 127, 127, 127,   0, 255}, -- Pitch:        M5=-1.0, M6=+1.0
}

-- Reusable static buffer for frame reception (zero heap allocations during streaming)
local rx_data = {0, 0, 0, 0, 0, 0, 0, 0}

-- Pre-allocated static response buffers (zero heap allocations during TX)
local tx_ack_payload  = {}
local tx_resp_single  = {0}
local tx_resp_pid     = {5, 2, 1}

-- Delayed frame scheduling (prevents race condition with GUI callback registration)
local dofs_saved_pending  = false
local dofs_saved_timer_ms = 0

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
local manual_ctrl_active        = false
local manual_ctrl_last_ms       = 0
local was_armed_before_manual   = false
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
        pcall(function() tx_frame = CANFrame() end)
        if not tx_frame then return false end
    end

    local ok, result = pcall(raw_write_frame, dev, tx_frame, id, payload, len)
    return ok and result ~= false
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

-- Convert 0..255 byte value to 1100..1900 RC PWM (127/128 -> 1500 neutral)
local function byte_to_rc_pwm(value)
    if not value then return 1500 end
    local clamped = math.max(0, math.min(255, value))
    local norm = (clamped / 127.5) - 1.0
    if norm < -1.0 then norm = -1.0 end
    if norm > 1.0 then norm = 1.0 end
    if math.abs(norm) < 0.015 then
        return 1500
    end
    return math.floor(1500 + (norm * 400) + 0.5)
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
        set_override(control_channels[i], byte_to_rc_pwm(data[i]))
    end
    last_ctrl_ms = now_ms()
    watchdog_tripped = false
    watchdog_warned = false
end

-- Direct motor test completion
local function end_motor_test()
    motor_test_active = false
    last_ctrl_ms = now_ms()

    -- Actively force all motor channels back to 1500 neutral immediately
    for channel = 0, 5 do
        local out_ch = motor_channels[channel + 1]
        SRV_Channels:set_output_pwm_chan_timeout(out_ch, 1500, 50)
    end

    -- Safety restoration: If vehicle was disarmed before test, disarm it now.
    -- If it was armed, restore the previous flight mode.
    if not was_armed_before_test then
        pcall(function() arming:disarm() end)
    else
        pcall(function() vehicle:set_mode(saved_mode_before_test) end)
        neutralize_controls()
    end

    tx_resp_single[1] = 1
    send_can(C.TX_TEST_MOTOR, tx_resp_single)
    gcs:send_text(6, "CAN bridge: motor test complete")
end

-- Handle direct motor test command from GUI
local function handle_motor_test(data)
    if motor_test_active then
        gcs:send_text(4, "CAN bridge: motor test already active")
        tx_resp_single[1] = 0
        send_can(C.TX_TEST_MOTOR, tx_resp_single)
        return
    end

    local duration_ms = data[7] * 10
    if duration_ms < 10 or duration_ms > C.MAX_MOTOR_TEST_MS then
        gcs:send_text(3, "CAN bridge: motor test duration invalid")
        tx_resp_single[1] = 0
        send_can(C.TX_TEST_MOTOR, tx_resp_single)
        return
    end

    init_motor_channels()
    was_armed_before_test = arming:is_armed()
    saved_mode_before_test = vehicle:get_mode() or 0

    -- Always switch to MANUAL mode (19) during bench motor tests so IMU attitude controller
    -- does not fight the test or drive other thrusters
    pcall(function()
        vehicle:set_mode(19)
        if not was_armed_before_test then
            if not arming:arm() then
                arming:arm_force()
            end
        end
    end)

    if not arming:is_armed() then
        gcs:send_text(3, "CAN bridge: motor test arm failed")
        tx_resp_single[1] = 0
        send_can(C.TX_TEST_MOTOR, tx_resp_single)
        return
    end

    local ok = pcall(function()
        for channel = 0, 5 do
            local out_ch = motor_channels[channel + 1]
            SRV_Channels:set_output_pwm_chan_timeout(
                out_ch,
                byte_to_motor_pwm(data[channel + 1]),
                duration_ms + 50
            )
        end
    end)

    if ok then
        motor_test_active = true
        motor_test_start_ms = now_ms()
        motor_test_dur_ms = duration_ms
    else
        gcs:send_text(3, "CAN bridge: motor test output failed")
        tx_resp_single[1] = 0
        send_can(C.TX_TEST_MOTOR, tx_resp_single)
    end
end

local function all_dofs_received()
    for i = 1, 6 do
        if not received_dofs[i] then
            return false
        end
    end
    return true
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
            tx_resp_single[1] = C.HEALTH_GUI_PROBE
            send_can(C.TX_HEALTH, tx_resp_single)
        end

    -- 2. Init Connection to Frontend
    elseif rx_id == C.RX_INIT_CONN then
        on_topside_activity()
        tx_resp_single[1] = 1
        send_can(C.TX_INIT_CONN, tx_resp_single)

    -- 3. Arm Thrusters / Init Motors
    elseif rx_id == C.RX_INIT_THRUSTERS then
        on_topside_activity()
        neutralize_controls()
        pcall(function()
            -- Switch to STABILIZE mode (0) by default upon arming if currently MANUAL
            local mode = vehicle:get_mode()
            if not mode or mode == 19 then
                vehicle:set_mode(0) -- STABILIZE mode
            end
            if not arming:arm() then
                -- Fallback to force-arming if bench checks fail
                arming:arm_force()
            end
        end)

        local armed = arming:is_armed()
        if armed then
            local now = now_ms()
            last_ctrl_ms = now
            last_heartbeat_tx_ms = now
            missed_heartbeats = 0
            heartbeat_awaiting = false
            auto_surface_active = false
            watchdog_tripped = false
            watchdog_warned = false
            was_armed = true
            gcs:send_text(6, "CAN bridge: armed in mode " .. tostring(vehicle:get_mode()))
        else
            gcs:send_text(3, "CAN bridge: arming rejected (check ARMING_SKIPCHK & safety)")
        end
        tx_resp_single[1] = armed and 1 or 0
        send_can(C.TX_INIT_THRUSTERS, tx_resp_single)

    -- 4. Disarm Thrusters
    elseif rx_id == C.RX_DEINIT_THRUST then
        on_topside_activity()
        neutralize_controls()
        pcall(function()
            arming:disarm()
        end)
        local disarmed = not arming:is_armed()
        if disarmed then
            clear_control_overrides()
            was_armed = false
        else
            neutralize_controls()
        end
        watchdog_tripped = true
        tx_resp_single[1] = disarmed and 1 or 0
        send_can(C.TX_DEINIT_THRUST, tx_resp_single)
        gcs:send_text(disarmed and 6 or 3, disarmed and "CAN bridge: disarmed" or "CAN bridge: disarm failed")

    -- 5. Reset Error
    elseif rx_id == C.RX_RESET_ERR then
        on_topside_activity()
        watchdog_tripped = false
        watchdog_warned = false
        last_ctrl_ms = now_ms()
        tx_resp_single[1] = 1
        send_can(C.TX_RESET_ERR, tx_resp_single)

    -- 6. Manual Individual Motor Control (Setup / Calibration view)
    elseif rx_id == C.RX_MANUAL_CTRL then
        on_topside_activity()
        if dlc >= 6 then
            init_motor_channels()
            if not manual_ctrl_active then
                manual_ctrl_active = true
                was_armed_before_manual = arming:is_armed()
                -- Ensure outputs are enabled for bench calibration in MANUAL mode (mode 19)
                if not was_armed_before_manual then
                    pcall(function()
                        vehicle:set_mode(19) -- MANUAL mode prevents IMU closed-loop fighting
                        if not arming:arm() then
                            arming:arm_force()
                        end
                    end)
                end
            end
            manual_ctrl_last_ms = now_ms()

            for channel = 0, 5 do
                local out_ch = motor_channels[channel + 1]
                SRV_Channels:set_output_pwm_chan_timeout(
                    out_ch,
                    byte_to_motor_pwm(rx_data[channel + 1]),
                    150
                )
            end
            tx_resp_single[1] = 1
            send_can(C.TX_MANUAL_CTRL, tx_resp_single)
        end

    -- 7. Driving / Controller Values (Manual, Stabilize, or Depth Hold)
    elseif rx_id == C.RX_CTRL_STABILIZE or rx_id == C.RX_CTRL_DEPTH_HOLD or rx_id == C.RX_CTRL_MANUAL then
        on_topside_activity()
        if arming:is_armed() and not motor_test_active and not manual_ctrl_active and dlc >= C.CONTROL_DLC then
            -- Flight mode selection from GUI:
            -- RX_CTRL_MANUAL     (180) switches to MANUAL (direct open-loop, mode 19)
            -- RX_CTRL_STABILIZE  (100) switches to STABILIZE (attitude hold, mode 0)
            -- RX_CTRL_DEPTH_HOLD (110) switches to ALT_HOLD (depth + attitude hold, mode 2)
            local target_mode = 0
            if rx_id == C.RX_CTRL_DEPTH_HOLD then
                target_mode = 2
            elseif rx_id == C.RX_CTRL_MANUAL then
                target_mode = 19
            end
            local cur_mode = vehicle:get_mode()

            if target_mode ~= cur_mode then
                if elapsed_ms(last_mode_change_ms) >= C.MODE_RETRY_INTERVAL_MS then
                    last_mode_change_ms = now_ms()
                    pcall(function() vehicle:set_mode(target_mode) end)
                end
            end

            handle_controller_values(rx_data)
        end

    -- 8. Direct Motor Test (Bench Thruster Test)
    elseif rx_id == C.RX_TEST_MOTOR then
        if dlc == C.MOTOR_TEST_DLC then
            handle_motor_test(rx_data)
        else
            gcs:send_text(3, "CAN bridge: motor test payload must be 7 bytes")
            tx_resp_single[1] = 0
            send_can(C.TX_TEST_MOTOR, tx_resp_single)
        end

    -- 9. Set Direction Values (RAM table compatibility with GUI)
    elseif rx_id >= C.RX_SET_FB and rx_id <= C.RX_SET_PITCH then
        if dlc >= C.CONTROL_DLC then
            local idx = rx_id - C.RX_SET_FB + 1
            for motor = 1, 6 do
                dof_table[idx][motor] = rx_data[motor]
            end
            received_dofs[idx] = true

            -- GUI awaits identical ID as ACK for each DoF
            send_can(rx_id, tx_ack_payload)

            if all_dofs_received() then
                -- Schedule TX_ALL_DOF_SAVED after 100ms non-blocking delay so C# GUI has time
                -- to complete its DoF loop and register the callback for ID 127
                dofs_saved_pending = true
                dofs_saved_timer_ms = now_ms()
                for i = 1, 6 do
                    received_dofs[i] = false
                end
            end
        end

    -- 10. Get Direction Values
    elseif rx_id >= C.RX_GET_FB and rx_id <= C.RX_GET_PITCH then
        local idx = rx_id - C.RX_GET_FB + 1
        send_can(rx_id + 1, dof_table[idx])

    -- 11. Pull PID Values (Return default/current values so GUI reports success)
    elseif rx_id == C.RX_PULL_PID then
        -- Returns Kp=0.05 (byte 5), Kd=0.02 (byte 2), GyroEnabled=1
        send_can(C.TX_PULL_PID, tx_resp_pid)

    -- 12. Apply PID Values
    elseif rx_id == C.RX_APPLY_PID then
        tx_resp_single[1] = 1
        send_can(C.TX_APPLY_PID, tx_resp_single)

    -- 13. Update Cycle Time
    elseif rx_id == C.RX_UPDATE_CYCLE then
        local candidate = (dlc >= 2) and rx_data[2] or rx_data[1]
        if candidate and candidate >= C.MIN_CYCLE_TIME_MS and candidate <= C.MAX_CYCLE_TIME_MS then
            cycle_time_ms = candidate
        end
        tx_resp_single[1] = cycle_time_ms
        send_can(C.TX_UPDATE_CYCLE, tx_resp_single)
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
        pcall(function() can_dev1 = CAN:get_device(C.CAN_BUFFER_SIZE) end)
        pcall(function() can_dev2 = CAN:get_device2(C.CAN_BUFFER_SIZE) end)
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

    -- Check if manual control slider sequence timed out (auto-disarm safety)
    if manual_ctrl_active and elapsed_ms(manual_ctrl_last_ms) > C.MANUAL_CTRL_TIMEOUT_MS then
        manual_ctrl_active = false
        for channel = 0, 5 do
            local out_ch = motor_channels[channel + 1]
            SRV_Channels:set_output_pwm_chan_timeout(out_ch, 1500, 50)
        end
        if not was_armed_before_manual then
            pcall(function() arming:disarm() end)
            gcs:send_text(6, "CAN bridge: manual calibration stopped, disarmed")
        else
            neutralize_controls()
        end
    end

    -- Check if delayed TX_ALL_DOF_SAVED is ready to transmit
    if dofs_saved_pending and elapsed_ms(dofs_saved_timer_ms) >= 100 then
        dofs_saved_pending = false
        tx_resp_single[1] = 1
        send_can(C.TX_ALL_DOF_SAVED, tx_resp_single)
    end

    -- Process pending incoming CAN frames from both drivers up to device limit
    drain_device(can_dev1)
    drain_device(can_dev2)

    -- Safety Watchdog, Periodic Topside Heartbeat & Auto-Surface Failsafe
    -- (Suppressed during direct motor tests or manual calibration to prevent override conflicts)
    local is_armed = arming:is_armed()
    if is_armed then
        was_armed = true
        if not motor_test_active and not manual_ctrl_active then
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
                        pcall(function() vehicle:set_mode(0) end) -- STABILIZE to keep level while surfacing
                        gcs:send_text(4, "CAN failsafe: link lost (" .. tostring(missed_heartbeats) .. " missed pings), auto-surfacing")
                    end
                end
                tx_resp_single[1] = C.HEALTH_ROV_PING
                send_can(C.TX_HEALTH, tx_resp_single)
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
                    pcall(function() arming:disarm() end)
                    clear_control_overrides()
                    was_armed = false
                    watchdog_tripped = true
                    gcs:send_text(4, "CAN failsafe: auto-surface max duration reached, disarmed")
                else
                    if vehicle:get_mode() ~= 0 and elapsed_ms(last_mode_change_ms) >= C.MODE_RETRY_INTERVAL_MS then
                        last_mode_change_ms = now_ms()
                        pcall(function() vehicle:set_mode(0) end)
                    end
                    apply_surface_controls()
                end
            elseif watchdog_tripped then
                neutralize_controls()
            end
        end
    else
        -- If disarmed externally (e.g. from GCS or failsafe), ensure overrides are cleared
        if was_armed then
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
