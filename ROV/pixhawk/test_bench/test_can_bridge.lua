-- test_can_bridge.lua
-- Standalone Pure-Lua Test Runner for can_bridge.lua
-- Target: Pixhawk 4 / ArduSub 4.7.2+
-- Usage: lua test_can_bridge.lua (or inside ArduPilot SITL)
-- Project: UStall ROV 2026

local script_dir = "./"
local info = debug.getinfo(1, "S")
if info and info.source and info.source:sub(1,1) == "@" then
    script_dir = info.source:sub(2):match("(.*/)") or "./"
end
local mock = dofile(script_dir .. "mock_ardupilot.lua")
dofile(script_dir .. "../can_bridge.lua")

local passed = 0
local failed = 0

local function run_case(name, fn)
    io.write(string.format("  [TEST] %-55s ... ", name))
    mock.reset_state()
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

run_case("Health Ping (ID 40 -> 41 [1])", function()
    CAN.dev1:push_rx(40, {})
    local delay = update()
    assert_eq(delay, 10, "Cycle delay")
    local f = table.remove(CAN.dev1.tx_queue, 1)
    assert_true(f ~= nil, "Response must exist")
    assert_eq(f:id_signed(), 41, "Response ID")
    assert_eq(f:dlc(), 1, "DLC")
    assert_eq(f:data(0), 1, "Payload")
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

run_case("Manual Slider Calibration Auto-Disarms on Timeout (300ms)", function()
    CAN.dev1:push_rx(90, {127, 127, 200, 127, 127, 127})
    update()
    assert_eq(arming:is_armed(), true)
    assert_eq(vehicle:get_mode(), 19)

    mock.advance_time(350)
    update()

    assert_eq(arming:is_armed(), false, "Auto-disarmed")
    assert_eq(SRV_Channels.outputs[2].pwm, 1500, "M3 neutralized")
end)

run_case("Direction Values RAM Table & Delayed ACK (IDs 120-135)", function()
    local custom = {200, 50, 127, 127, 127, 127}
    for id = 120, 125 do
        CAN.dev1:push_rx(id, custom)
        update()
        local ack = table.remove(CAN.dev1.tx_queue, 1)
        assert_true(ack ~= nil, "Immediate ACK")
        assert_eq(ack:id_signed(), id, "ACK ID")
    end

    assert_eq(#CAN.dev1.tx_queue, 0, "No premature All_DoF_Saved")

    mock.advance_time(120)
    update()
    local saved = table.remove(CAN.dev1.tx_queue, 1)
    assert_true(saved ~= nil, "TX_ALL_DOF_SAVED")
    assert_eq(saved:id_signed(), 127)
    assert_eq(saved:data(0), 1)

    -- Query back Get_FB (130)
    CAN.dev1:push_rx(130, {})
    update()
    local get_resp = table.remove(CAN.dev1.tx_queue, 1)
    assert_eq(get_resp:id_signed(), 131)
    assert_eq(get_resp:data(0), 200)
    assert_eq(get_resp:data(1), 50)
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

print(string.rep("=", 72))
print(string.format("Pure Lua Test Results: %d PASSED, %d FAILED", passed, failed))
print(string.rep("=", 72))
if failed > 0 then os.exit(1) end
