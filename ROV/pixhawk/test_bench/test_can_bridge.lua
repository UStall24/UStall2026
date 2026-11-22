-- test_can_bridge.lua
-- Standalone Pure-Lua Test Runner for can_bridge.lua
-- Target: Pixhawk 4 / ArduSub 4.7.2+
-- Usage: lua test_can_bridge.lua (or inside ArduPilot SITL)
-- Project: UStall ROV 2026

local script_dir = "./"
local info = debug.getinfo(1, "S")
if info and info.source and info.source:sub(1,1) == "@" then
    script_dir = info.source:sub(2):match("(.*[/\\])") or "./"
end
local mock = dofile(script_dir .. "mock_ardupilot.lua")

local passed = 0
local failed = 0

local function run_case(name, fn)
    io.write(string.format("  [TEST] %-55s ... ", name))
    mock.reset_state()
    dofile(script_dir .. "../can_bridge.lua")
    update() -- run initial tick for lazy init
    CAN.dev1.tx_queue = {}
    CAN.dev2.tx_queue = {}

    local ok, err = pcall(fn)
    if ok then
        print("\27[32mPASS\27[0m")
        passed = passed + 1
    else
        print("\27[31mFAIL\27[0m\n         Error: " .. tostring(err))
        failed = failed + 1
    end
end

local function assert_eq(actual, expected, msg)
    if actual ~= expected then
        error(string.format("%s (Expected: %s, Actual: %s)", msg or "Assertion failed", tostring(expected), tostring(actual)), 2)
    end
end

local function assert_true(cond, msg)
    if not cond then
        error(msg or "Assertion failed", 2)
    end
end

print(string.rep("=", 72))
print("  UStall ROV 2026 - ArduSub CAN Bridge Pure Lua Verification Suite")
print(string.rep("=", 72))

run_case("Health Ping (ID 40 -> 41 [1, armed, mode])", function()
    CAN.dev1:push_rx(40, {})
    local _, delay = update()
    assert_eq(delay, 10, "Cycle delay")
    local f = table.remove(CAN.dev1.tx_queue, 1)
    assert_true(f ~= nil, "Response must exist")
    assert_eq(f:id_signed(), 41, "Response ID")
    assert_eq(f:dlc(), 3, "DLC")
    assert_eq(f:data(0), 1, "Marker")
    assert_eq(f:data(1), 0, "Armed state (disarmed)")
    assert_eq(f:data(2), vehicle:get_mode(), "Flight mode")
end)

run_case("Init Connection (ID 50 -> 51 [1])", function()
    CAN.dev1:push_rx(50, {})
    update()
    local f = table.remove(CAN.dev1.tx_queue, 1)
    assert_true(f ~= nil)
    assert_eq(f:id_signed(), 51)
    assert_eq(f:data(0), 1)
end)

run_case("Init Thrusters Arming (ID 60 -> 61, Mode 0)", function()
    CAN.dev1:push_rx(60, {})
    update()
    assert_true(arming:is_armed(), "Vehicle should be armed")
    assert_eq(vehicle:get_mode(), 0, "Mode should be 0 (STABILIZE)")
    local f = table.remove(CAN.dev1.tx_queue, 1)
    assert_eq(f:id_signed(), 61)
    assert_eq(f:data(0), 1)
end)

run_case("Pilot Drive Static Throttle (ID 100 -> RC Overrides)", function()
    CAN.dev1:push_rx(60, {})
    update()
    CAN.dev1.tx_queue = {}

    -- Forward=255, Lateral=0, Throttle=127, Yaw=191, Pitch=127, Roll=127
    CAN.dev1:push_rx(100, {255, 0, 127, 191, 127, 127})
    update()

    assert_eq(vehicle:get_mode(), 0, "Flight mode")
    assert_eq(rc:get_channel(5).override_pwm, 1900, "RC5 Forward")
    assert_eq(rc:get_channel(6).override_pwm, 1100, "RC6 Lateral")
    assert_eq(rc:get_channel(3).override_pwm, 1500, "RC3 Throttle")
    assert_true(math.abs(rc:get_channel(4).override_pwm - 1700) <= 2, "RC4 Yaw")
end)

run_case("Pilot Drive Dynamic Throttle (ID 110 -> Mode 2 ALT_HOLD)", function()
    CAN.dev1:push_rx(60, {})
    update()
    CAN.dev1.tx_queue = {}

    CAN.dev1:push_rx(110, {127, 127, 255, 127, 127, 127})
    update()

    assert_eq(vehicle:get_mode(), 2, "Flight mode must switch to ALT_HOLD")
    assert_eq(rc:get_channel(3).override_pwm, 1900, "RC3 Throttle Heave")
end)

run_case("Pilot Drive Manual Mode (ID 90 -> Mode 19 MANUAL)", function()
    CAN.dev1:push_rx(60, {})
    update()
    CAN.dev1.tx_queue = {}

    CAN.dev1:push_rx(90, {255, 127, 127, 127, 127, 127})
    update()

    assert_eq(vehicle:get_mode(), 19, "Flight mode must switch to MANUAL")
    assert_eq(rc:get_channel(5).override_pwm, 1900, "RC5 Forward")
end)

run_case("Watchdog Timeout Neutralizes Controls (500ms)", function()
    CAN.dev1:push_rx(60, {})
    update()
    CAN.dev1:push_rx(100, {255, 127, 127, 127, 127, 127})
    update()
    assert_eq(rc:get_channel(5).override_pwm, 1900)

    mock.advance_time(600)
    update()

    for ch = 1, 6 do
        assert_eq(rc:get_channel(ch).override_pwm, 1500, "RC" .. ch .. " must neutralize")
    end
end)

run_case("Auto-Surface on 2 Missed Health Pings & Recovery (ID 41/40 [2])", function()
    CAN.dev1:push_rx(60, {})
    update()
    CAN.dev1:push_rx(90, {255, 127, 127, 127, 127, 127})
    update()
    assert_eq(vehicle:get_mode(), 19)
    CAN.dev1.tx_queue = {}

    -- Ping #1 at 2000ms (ID 41, payload [2])
    mock.advance_time(2000)
    update()
    local p1 = table.remove(CAN.dev1.tx_queue, 1)
    assert_true(p1 ~= nil and p1:id_signed() == 41 and p1:data(0) == 2, "Ping #1 sent on ID 41 [2]")

    -- Ping #2 at 4000ms (1 missed)
    mock.advance_time(2000)
    update()
    local p2 = table.remove(CAN.dev1.tx_queue, 1)
    assert_true(p2 ~= nil and p2:id_signed() == 41 and p2:data(0) == 2, "Ping #2 sent on ID 41 [2]")
    assert_eq(rc:get_channel(3).override_pwm, 1500, "Still neutral after 1 missed ping")

    -- At 6000ms (2 missed) -> Auto-Surface in STABILIZE (0) at 1540 us
    mock.advance_time(2000)
    update()
    assert_eq(vehicle:get_mode(), 0, "Switched to STABILIZE")
    assert_eq(rc:get_channel(3).override_pwm, 1540, "RC3 gentle upward thrust 1540 us")

    -- Topside replies ID 40 [2] -> cancels auto-surface without echoing ID 41
    CAN.dev1.tx_queue = {}
    CAN.dev1:push_rx(40, {2})
    update()
    assert_eq(rc:get_channel(3).override_pwm, 1500, "Returned to neutral on link recovery")
    assert_eq(#CAN.dev1.tx_queue, 0, "No echo on ID 40 [2]")
end)

run_case("Deinit Thrusters Disarms & Releases RC Overrides (ID 70)", function()
    CAN.dev1:push_rx(60, {})
    update()
    CAN.dev1.tx_queue = {}

    CAN.dev1:push_rx(70, {})
    update()

    assert_eq(arming:is_armed(), false, "Must be disarmed")
    for ch = 1, 6 do
        assert_eq(rc:get_channel(ch).override_pwm, 0, "RC" .. ch .. " must release override (0)")
    end
    local f = table.remove(CAN.dev1.tx_queue, 1)
    assert_eq(f:id_signed(), 71)
    assert_eq(f:data(0), 1)
end)

run_case("Direct Motor Test Execution & Safety Restore (ID 170)", function()
    assert_eq(arming:is_armed(), false)
    -- Motor 1: 145 (1560 us), Duration 50 (500 ms)
    CAN.dev1:push_rx(170, {145, 128, 128, 128, 128, 128, 50})
    update()

    assert_eq(arming:is_armed(), true, "Must temporarily arm")
    assert_eq(vehicle:get_mode(), 19, "Must switch to MANUAL mode 19")
    assert_true(math.abs(SRV_Channels.outputs[0].pwm - 1560) <= 5, "M1 PWM")

    -- Advance time past duration (550ms)
    mock.advance_time(550)
    update()

    assert_eq(SRV_Channels.outputs[0].pwm, 1500, "M1 restored to 1500")
    assert_eq(arming:is_armed(), false, "Vehicle restored to disarmed")
    local f = table.remove(CAN.dev1.tx_queue, 1)
    assert_true(f ~= nil)
    assert_eq(f:id_signed(), 171)
    assert_eq(f:data(0), 1)
end)

run_case("Dual CAN Port Redundancy (CAN1 <-> CAN2)", function()
    CAN.dev2:push_rx(40, {})
    update()
    local f2 = table.remove(CAN.dev2.tx_queue, 1)
    assert_true(f2 ~= nil, "Reply on CAN2")
    assert_eq(f2:id_signed(), 41)
    assert_eq(#CAN.dev1.tx_queue, 0, "CAN1 unaffected")
end)

run_case("DroneCAN Extended 29-bit & Error Frame Filtering", function()
    CAN.dev1:push_rx(0x1F001234, {1,2,3,4}, true, false, false) -- ext
    CAN.dev1:push_rx(40, {}, false, true, false) -- err
    CAN.dev1:push_rx(40, {}, false, false, true) -- rtr
    update()
    assert_eq(#CAN.dev1.tx_queue, 0, "All filtered out")
end)

run_case("Arm in Requested Mode & Global Speed Limit (ID 60 [19], 120)", function()
    CAN.dev1:push_rx(120, {50})
    update()
    local f = table.remove(CAN.dev1.tx_queue, 1)
    assert_true(f ~= nil and f:id_signed() == 121 and f:data(0) == 50, "Speed limit echo 50%")
    assert_eq(param:get("MOT_PWM_MIN"), 1300, "MOT_PWM_MIN saved to 1300")
    assert_eq(param:get("MOT_PWM_MAX"), 1700, "MOT_PWM_MAX saved to 1700")

    -- Empty Get returns persisted 50%
    CAN.dev1:push_rx(120, {})
    update()
    local fg = table.remove(CAN.dev1.tx_queue, 1)
    assert_true(fg ~= nil and fg:id_signed() == 121 and fg:data(0) == 50, "Speed limit Get returns 50%")

    -- Bench motor test (ID 170) is scaled by 50% (1900 -> 1700)
    CAN.dev1:push_rx(170, {255, 128, 128, 128, 128, 128, 50})
    update()
    assert_eq(SRV_Channels.outputs[0].pwm, 1700, "Bench motor test scaled to 50% (1700 us)")
    mock.advance_time(550)
    update()
    CAN.dev1.tx_queue = {}

    CAN.dev1:push_rx(60, {19})
    update()
    assert_eq(arming:is_armed(), true)
    assert_eq(vehicle:get_mode(), 19, "Armed in MANUAL")
    CAN.dev1.tx_queue = {}

    -- RC override is NOT double-scaled (MOT_PWM_MIN/MAX scales all ArduSub motor outputs globally)
    CAN.dev1:push_rx(90, {255, 0, 127, 127, 127, 127})
    update()
    assert_eq(rc:get_channel(5).override_pwm, 1900, "RC5 full range (global cap via MOT_PWM_MAX)")
    assert_eq(rc:get_channel(6).override_pwm, 1100, "RC6 full range (global cap via MOT_PWM_MIN)")

    -- 0% speed limit saves valid MOT_PWM_MIN=1499 / MOT_PWM_MAX=1501 and neutralizes RC
    CAN.dev1:push_rx(120, {0})
    update()
    assert_eq(param:get("MOT_PWM_MIN"), 1499, "MOT_PWM_MIN at 0% is 1499")
    assert_eq(param:get("MOT_PWM_MAX"), 1501, "MOT_PWM_MAX at 0% is 1501")
    CAN.dev1:push_rx(90, {255, 0, 127, 127, 127, 127})
    update()
    assert_eq(rc:get_channel(5).override_pwm, 1500, "RC5 at 0% is neutral 1500")

    -- Restore speed limit to 100% for subsequent tests (shared Lua state)
    CAN.dev1:push_rx(120, {100})
    update()
end)

run_case("Emergency Disarm Cancels Active Motor Test (ID 70)", function()
    CAN.dev1:push_rx(170, {145, 128, 128, 128, 128, 128, 100})
    update()
    assert_eq(arming:is_armed(), true)
    CAN.dev1.tx_queue = {}

    mock.advance_time(200)
    CAN.dev1:push_rx(70, {})
    update()
    assert_eq(arming:is_armed(), false, "Must disarm immediately")
    assert_eq(SRV_Channels.outputs[0].pwm, 1500, "Motor 1 neutralized")
    local f = table.remove(CAN.dev1.tx_queue, 1)
    assert_true(f ~= nil and f:id_signed() == 71 and f:data(0) == 1)

    mock.advance_time(1000)
    update()
    assert_eq(#CAN.dev1.tx_queue, 0, "No stale ID 171 after cancelled test")
end)

run_case("Empty Cycle Time Frame Ignores Stale Buffer (ID 160)", function()
    CAN.dev1:push_rx(120, {50})
    update()
    CAN.dev1.tx_queue = {}

    CAN.dev1:push_rx(160, {})
    local _, delay = update()
    assert_eq(delay, 10, "Must keep 10 ms cycle time")
    local f = table.remove(CAN.dev1.tx_queue, 1)
    assert_true(f ~= nil and f:id_signed() == 161 and f:data(0) == 10)
end)

run_case("Motor Config Directions & ESC Port Mapping (ID 130 -> 131)", function()
    -- Reverse M1 & M4 (mask=9), swap M1/M2 ports
    CAN.dev1:push_rx(130, {9, 2, 1, 3, 4, 5, 6})
    update()
    local f = table.remove(CAN.dev1.tx_queue, 1)
    assert_true(f ~= nil and f:id_signed() == 131 and f:dlc() == 7)
    assert_eq(f:data(0), 9, "dir_mask")
    assert_eq(f:data(1), 2, "M1 -> SERVO2")
    assert_eq(f:data(2), 1, "M2 -> SERVO1")
    assert_eq(param:get("MOT_1_DIRECTION"), -1)
    assert_eq(param:get("SERVO2_FUNCTION"), 33)

    -- Writes while armed must be rejected and echo existing config
    CAN.dev1:push_rx(60, {0})
    update()
    CAN.dev1.tx_queue = {}
    CAN.dev1:push_rx(130, {0, 1, 2, 3, 4, 5, 6})
    update()
    local f2 = table.remove(CAN.dev1.tx_queue, 1)
    assert_true(f2 ~= nil and f2:id_signed() == 131 and f2:dlc() == 7)
    assert_eq(f2:data(0), 9, "dir_mask unchanged while armed")
    assert_eq(f2:data(1), 2, "M1 port unchanged while armed")
end)

run_case("Per-Axis Gains Scaling & Persistence (ID 140 -> 141)", function()
    CAN.dev1:push_rx(140, {50, 25, 100, 50, 0, 100})
    update()
    local f = table.remove(CAN.dev1.tx_queue, 1)
    assert_true(f ~= nil and f:id_signed() == 141 and f:dlc() == 6)
    assert_eq(f:data(0), 50)
    assert_eq(f:data(1), 25)
    assert_eq(param:get("SCR_USER1"), 51, "SCR_USER1 saved as gain+1")
    assert_eq(param:get("SCR_USER5"), 1, "SCR_USER5 saved 0% as 1")

    CAN.dev1:push_rx(60, {0})
    update()
    CAN.dev1:push_rx(100, {255, 255, 255, 127, 255, 127})
    update()
    assert_eq(rc:get_channel(5).override_pwm, 1700, "RC5 Forward 50%")
    assert_eq(rc:get_channel(6).override_pwm, 1600, "RC6 Lateral 25%")
    assert_eq(rc:get_channel(2).override_pwm, 1500, "RC2 Pitch 0%")
end)

run_case("Non-Bridge Traffic on CAN2 Does Not Hijack Active Port", function()
    CAN.dev1:push_rx(60, {0})
    update()
    CAN.dev1.tx_queue = {}
    CAN.dev2.tx_queue = {}

    -- Push Gripper (0x301) and DroneCAN extended frame on CAN2
    CAN.dev2:push_rx(0x301, {0, 45, 0})
    CAN.dev2:push_rx(0x18000001, {1, 2, 3}, true, false, false)
    update()

    mock.advance_time(2000)
    update()
    local hb1 = table.remove(CAN.dev1.tx_queue, 1)
    assert_true(hb1 ~= nil and hb1:id_signed() == 41 and hb1:data(0) == 2, "Heartbeat stays on CAN1")
    assert_eq(#CAN.dev2.tx_queue, 0, "CAN2 must not receive replies")
end)

run_case("Rapid Flight Mode Switching & Arm Guard During Motor Test", function()
    CAN.dev1:push_rx(60, {19})
    update()
    assert_eq(vehicle:get_mode(), 19)

    mock.advance_time(50)
    CAN.dev1:push_rx(100, {127, 127, 127, 127, 127, 127})
    update()
    assert_eq(vehicle:get_mode(), 0, "Immediate switch to STABILIZE")

    mock.advance_time(50)
    CAN.dev1:push_rx(110, {127, 127, 127, 127, 127, 127})
    update()
    assert_eq(vehicle:get_mode(), 2, "Immediate switch to ALT_HOLD")

    -- Disarm, start motor test, verify ID 60 is rejected mid-test
    CAN.dev1:push_rx(70, {})
    update()
    CAN.dev1:push_rx(170, {160, 128, 128, 128, 128, 128, 50})
    update()
    CAN.dev1.tx_queue = {}

    mock.advance_time(100)
    CAN.dev1:push_rx(60, {0})
    update()
    local f = table.remove(CAN.dev1.tx_queue, 1)
    assert_true(f ~= nil and f:id_signed() == 61 and f:data(0) == 0, "Arm rejected mid-test")
    assert_eq(vehicle:get_mode(), 19, "Stays in MANUAL during motor test")
end)

print(string.rep("=", 72))
print(string.format("Pure Lua Test Results: %d PASSED, %d FAILED", passed, failed))
print(string.rep("=", 72))
if failed > 0 then os.exit(1) end
