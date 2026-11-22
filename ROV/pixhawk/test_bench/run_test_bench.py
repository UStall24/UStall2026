# run_test_bench.py
# Automated Test Bench for ArduSub CAN Bridge (can_bridge.lua)
# Target: Pixhawk 4 / ArduSub 4.7.2+
# Project: UStall ROV 2026

import ctypes
import os
import sys
import time

LUA_DLL_PATH = r"C:\Program Files\lua-5.5.0_Win64_bin\lua55.dll"
MOCK_LUA_PATH = os.path.join(os.path.dirname(__file__), "mock_ardupilot.lua")
CAN_BRIDGE_LUA_PATH = os.path.join(os.path.dirname(__file__), "..", "can_bridge.lua")

class LuaHarness:
    def __init__(self, dll_path=LUA_DLL_PATH):
        self.dll = ctypes.CDLL(dll_path)
        self._setup_bindings()
        self.L = None
        self.reset()

    def _setup_bindings(self):
        self.dll.luaL_newstate.restype = ctypes.c_void_p
        self.dll.luaopen_base.argtypes = [ctypes.c_void_p]
        self.dll.luaopen_base.restype = ctypes.c_int
        self.dll.luaopen_string.argtypes = [ctypes.c_void_p]
        self.dll.luaopen_string.restype = ctypes.c_int
        self.dll.luaopen_math.argtypes = [ctypes.c_void_p]
        self.dll.luaopen_math.restype = ctypes.c_int
        self.dll.luaopen_table.argtypes = [ctypes.c_void_p]
        self.dll.luaopen_table.restype = ctypes.c_int
        
        self.dll.lua_settop.argtypes = [ctypes.c_void_p, ctypes.c_int]
        self.dll.lua_setglobal.argtypes = [ctypes.c_void_p, ctypes.c_char_p]
        self.dll.lua_getglobal.argtypes = [ctypes.c_void_p, ctypes.c_char_p]
        self.dll.lua_getglobal.restype = ctypes.c_int
        self.dll.luaL_loadstring.argtypes = [ctypes.c_void_p, ctypes.c_char_p]
        self.dll.luaL_loadstring.restype = ctypes.c_int
        self.dll.lua_pcallk.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_void_p, ctypes.c_void_p]
        self.dll.lua_pcallk.restype = ctypes.c_int
        self.dll.lua_close.argtypes = [ctypes.c_void_p]
        self.dll.lua_tolstring.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_void_p]
        self.dll.lua_tolstring.restype = ctypes.c_char_p
        self.dll.lua_tonumberx.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_void_p]
        self.dll.lua_tonumberx.restype = ctypes.c_double
        self.dll.lua_toboolean.argtypes = [ctypes.c_void_p, ctypes.c_int]
        self.dll.lua_toboolean.restype = ctypes.c_int

    def reset(self):
        if self.L:
            self.dll.lua_close(self.L)
        self.L = self.dll.luaL_newstate()
        if not self.L:
            raise RuntimeError("Failed to allocate Lua state")
        self.dll.luaopen_base(self.L)
        self.dll.lua_settop(self.L, 0)
        for name, fn in [('string', self.dll.luaopen_string), ('math', self.dll.luaopen_math), ('table', self.dll.luaopen_table)]:
            fn(self.L)
            self.dll.lua_setglobal(self.L, name.encode('ascii'))

    def eval(self, code_str):
        b = code_str.encode('utf-8')
        res = self.dll.luaL_loadstring(self.L, b)
        if res != 0:
            err = self.dll.lua_tolstring(self.L, -1, None)
            raise SyntaxError(f"Lua compile error: {err.decode('utf-8', errors='ignore')}")
        res = self.dll.lua_pcallk(self.L, 0, -1, 0, None, None)
        if res != 0:
            err = self.dll.lua_tolstring(self.L, -1, None)
            raise RuntimeError(f"Lua runtime error: {err.decode('utf-8', errors='ignore')}")

    def eval_return_number(self, expr_str):
        code = f"__temp_res = ({expr_str})"
        self.eval(code)
        self.dll.lua_getglobal(self.L, b"__temp_res")
        val = self.dll.lua_tonumberx(self.L, -1, None)
        self.dll.lua_settop(self.L, -2)
        return val

    def eval_return_bool(self, expr_str):
        code = f"__temp_res = ({expr_str})"
        self.eval(code)
        self.dll.lua_getglobal(self.L, b"__temp_res")
        val = bool(self.dll.lua_toboolean(self.L, -1))
        self.dll.lua_settop(self.L, -2)
        return val

    def eval_return_string(self, expr_str):
        code = f"__temp_res = tostring({expr_str})"
        self.eval(code)
        self.dll.lua_getglobal(self.L, b"__temp_res")
        s = self.dll.lua_tolstring(self.L, -1, None)
        val = s.decode('utf-8', errors='ignore') if s else ""
        self.dll.lua_settop(self.L, -2)
        return val

    def load_file(self, filepath):
        with open(filepath, 'r', encoding='utf-8') as f:
            code = f.read()
        self.eval(code)

    def step_update(self):
        """Calls the global update() function once and returns the returned delay in ms."""
        code = "__up_fn, __up_delay = update()"
        self.eval(code)
        return int(self.eval_return_number("__up_delay"))

    def push_can_rx(self, dev_name, can_id, data_bytes=None, is_ext=False, is_err=False, is_rtr=False):
        data_str = "{" + ", ".join(str(b) for b in (data_bytes or [])) + "}"
        ext_str = "true" if is_ext else "false"
        err_str = "true" if is_err else "false"
        rtr_str = "true" if is_rtr else "false"
        code = f"CAN.{dev_name}:push_rx({can_id}, {data_str}, {ext_str}, {err_str}, {rtr_str})"
        self.eval(code)

    def pop_can_tx(self, dev_name):
        code = f"""
        if #CAN.{dev_name}.tx_queue > 0 then
            local f = table.remove(CAN.{dev_name}.tx_queue, 1)
            __tx_id = f:id_signed()
            __tx_dlc = f:dlc()
            local d = {{}}
            for i=0, f:dlc()-1 do table.insert(d, f:data(i)) end
            __tx_data = table.concat(d, ',')
        else
            __tx_id = -1
            __tx_dlc = 0
            __tx_data = ''
        end
        """
        self.eval(code)
        tx_id = int(self.eval_return_number("__tx_id"))
        if tx_id == -1:
            return None
        dlc = int(self.eval_return_number("__tx_dlc"))
        data_str = self.eval_return_string("__tx_data")
        data = [int(x) for x in data_str.split(',')] if data_str else []
        return {"id": tx_id, "dlc": dlc, "data": data}

    def clear_tx_queues(self):
        self.eval("CAN.dev1.tx_queue = {}; CAN.dev2.tx_queue = {}")

    def advance_time(self, ms):
        self.eval(f"mock.advance_time({ms})")

    def get_rc_override(self, ch_num):
        return int(self.eval_return_number(f"rc:get_channel({ch_num}).override_pwm"))

    def get_srv_output(self, ch_num):
        # 0-indexed servo channel
        code = f"SRV_Channels.outputs[{ch_num}] and SRV_Channels.outputs[{ch_num}].pwm or 0"
        return int(self.eval_return_number(code))

    def is_armed(self):
        return self.eval_return_bool("arming:is_armed()")

    def get_mode(self):
        return int(self.eval_return_number("vehicle:get_mode()"))


class TestBench:
    def __init__(self):
        self.harness = LuaHarness()
        self.passed = 0
        self.failed = 0
        self.results = []

    def setup(self):
        self.harness.reset()
        self.harness.eval("mock = dofile([[" + MOCK_LUA_PATH.replace("\\", "/") + "]])")
        self.harness.eval("dofile([[" + CAN_BRIDGE_LUA_PATH.replace("\\", "/") + "]])")
        # Run initial tick to complete lazy initialization
        self.harness.step_update()
        self.harness.clear_tx_queues()

    def assert_true(self, condition, message="Assertion failed"):
        if not condition:
            raise AssertionError(message)

    def assert_equal(self, actual, expected, message="Assertion failed"):
        if actual != expected:
            raise AssertionError(f"{message} (Expected: {expected}, Actual: {actual})")

    def run_test(self, test_name, test_fn):
        self.setup()
        sys.stdout.write(f"  [RUN]  {test_name} ... ")
        sys.stdout.flush()
        try:
            test_fn()
            print("\033[92mPASS\033[0m")
            self.passed += 1
            self.results.append((test_name, True, "OK"))
        except Exception as e:
            print(f"\033[91mFAIL\033[0m\n         Error: {e}")
            self.failed += 1
            self.results.append((test_name, False, str(e)))

    # =========================================================================
    # Test Cases
    # =========================================================================

    def test_01_init_and_gcs_greeting(self):
        # Verify ArduPilot embedded Lua local variable limit (LUAI_MAXVARS = 100 in main chunk)
        with open(CAN_BRIDGE_LUA_PATH, 'r', encoding='utf-8') as f:
            top_level_locals = sum(1 for line in f if line.startswith("local "))
        self.assert_true(top_level_locals < 80, f"Too many top-level locals ({top_level_locals}) for ArduPilot Lua (limit 100)")

        # Verify GCS greeting and CAN driver detection
        msg_count = int(self.harness.eval_return_number("#gcs.messages"))
        self.assert_true(msg_count >= 2, "Expected at least 2 GCS greeting messages on boot")
        greeting = self.harness.eval_return_string("gcs.messages[1].text")
        self.assert_true("ROV CAN bridge" in greeting, f"Unexpected greeting: {greeting}")
        dev_ready = self.harness.eval_return_string("gcs.messages[2].text")
        self.assert_true("driver ready" in dev_ready, f"Unexpected driver ready msg: {dev_ready}")
        self.assert_equal(self.harness.is_armed(), False, "Vehicle must start disarmed")

    def test_02_health_ping(self):
        # Topside sends Health ping (ID 40) while disarmed in MANUAL (19)
        self.harness.push_can_rx("dev1", 40, [])
        delay = self.harness.step_update()
        self.assert_equal(delay, 10, "Cycle delay should be 10ms")
        resp = self.harness.pop_can_tx("dev1")
        self.assert_true(resp is not None, "Expected TX_HEALTH response")
        self.assert_equal(resp["id"], 41, "Response ID must be TX_HEALTH (41)")
        self.assert_equal(resp["data"], [1, 0, self.harness.get_mode()], "Response payload must be [1, armed=0, mode]")

    def test_03_init_conn(self):
        # Topside sends Init Conn (ID 50)
        self.harness.push_can_rx("dev1", 50, [])
        self.harness.step_update()
        resp = self.harness.pop_can_tx("dev1")
        self.assert_true(resp is not None, "Expected TX_INIT_CONN response")
        self.assert_equal(resp["id"], 51, "Response ID must be TX_INIT_CONN (51)")
        self.assert_equal(resp["data"], [1], "Payload must be [1]")

    def test_04_init_thrusters_arm(self):
        # Topside arms thrusters (ID 60)
        self.harness.push_can_rx("dev1", 60, [])
        self.harness.step_update()
        self.assert_equal(self.harness.is_armed(), True, "Vehicle must be armed")
        self.assert_equal(self.harness.get_mode(), 0, "Default armed mode must be 0 (STABILIZE)")
        resp = self.harness.pop_can_tx("dev1")
        self.assert_true(resp is not None, "Expected TX_INIT_THRUSTERS response")
        self.assert_equal(resp["id"], 61, "Response ID must be TX_INIT_THRUSTERS (61)")
        self.assert_equal(resp["data"], [1], "Response payload must indicate success [1]")

    def test_05_driving_static_throttle_stabilize(self):
        # Arm first
        self.harness.push_can_rx("dev1", 60, [])
        self.harness.step_update()
        self.harness.clear_tx_queues()

        # Send Static Throttle stick packet (ID 100):
        # data[1] = Forward (255 = +100%)
        # data[2] = Lateral (0 = -100%)
        # data[3] = Throttle (127 = 0 neutral)
        # data[4] = Yaw (191 = +50%)
        # data[5] = Pitch (127 = neutral)
        # data[6] = Roll (127 = neutral)
        payload = [255, 0, 127, 191, 127, 127]
        self.harness.push_can_rx("dev1", 100, payload)
        self.harness.step_update()

        self.assert_equal(self.harness.get_mode(), 0, "Flight mode must be STABILIZE (0)")
        self.assert_equal(self.harness.get_rc_override(5), 1900, "RC5 (Forward) must be 1900 us")
        self.assert_equal(self.harness.get_rc_override(6), 1100, "RC6 (Lateral) must be 1100 us")
        self.assert_equal(self.harness.get_rc_override(3), 1500, "RC3 (Throttle) must be 1500 us")
        self.assert_true(abs(self.harness.get_rc_override(4) - 1700) <= 2, "RC4 (Yaw) must be ~1700 us")
        self.assert_equal(self.harness.get_rc_override(2), 1500, "RC2 (Pitch) must be 1500 us")
        self.assert_equal(self.harness.get_rc_override(1), 1500, "RC1 (Roll) must be 1500 us")

    def test_06_driving_dynamic_throttle_alt_hold(self):
        # Arm first
        self.harness.push_can_rx("dev1", 60, [])
        self.harness.step_update()
        self.harness.clear_tx_queues()

        # Send Dynamic Throttle stick packet (ID 110): Heave UP (+100%)
        payload = [127, 127, 255, 127, 127, 127]
        self.harness.push_can_rx("dev1", 110, payload)
        self.harness.step_update()

        self.assert_equal(self.harness.get_mode(), 2, "Flight mode must switch to ALT_HOLD (2)")
        self.assert_equal(self.harness.get_rc_override(3), 1900, "RC3 (Throttle) must be 1900 us")
        self.assert_equal(self.harness.get_rc_override(5), 1500, "RC5 (Forward) must be neutral 1500 us")

    def test_23_driving_manual_mode(self):
        # Arm first (starts in STABILIZE mode 0)
        self.harness.push_can_rx("dev1", 60, [])
        self.harness.step_update()
        self.harness.clear_tx_queues()

        # Send Manual pilot drive packet (ID 90): Forward (+100%)
        payload = [255, 127, 127, 127, 127, 127]
        self.harness.push_can_rx("dev1", 90, payload)
        self.harness.step_update()

        self.assert_equal(self.harness.get_mode(), 19, "Flight mode must switch to MANUAL (19)")
        self.assert_equal(self.harness.get_rc_override(5), 1900, "RC5 (Forward) must be 1900 us")
        self.assert_equal(self.harness.get_rc_override(3), 1500, "RC3 (Throttle) must be neutral 1500 us")

    def test_07_watchdog_pilot_timeout(self):
        # Arm and stream one packet
        self.harness.push_can_rx("dev1", 60, [])
        self.harness.step_update()
        self.harness.push_can_rx("dev1", 100, [255, 127, 127, 127, 127, 127])
        self.harness.step_update()
        self.assert_equal(self.harness.get_rc_override(5), 1900, "RC5 should be active 1900 us")

        # Advance time by 600ms (exceeds WATCHDOG_TIMEOUT_MS = 500)
        self.harness.advance_time(600)
        self.harness.step_update()

        # All RC overrides must be forced to neutral 1500
        for ch in [1, 2, 3, 4, 5, 6]:
            self.assert_equal(self.harness.get_rc_override(ch), 1500, f"RC{ch} must be neutralized to 1500 us")

        # Verify GCS warning was sent
        last_msg = self.harness.eval_return_string("gcs.messages[#gcs.messages].text")
        self.assert_true("CAN watchdog: pilot timeout" in last_msg, f"Expected watchdog warning, got: {last_msg}")

    def test_08_watchdog_recovery(self):
        # Start armed and trip watchdog
        self.harness.push_can_rx("dev1", 60, [])
        self.harness.step_update()
        self.harness.clear_tx_queues()
        self.harness.advance_time(600)
        self.harness.step_update()
        self.assert_equal(self.harness.get_rc_override(5), 1500, "Watchdog should be tripped")

        # Topside sends Reset Error (ID 80)
        self.harness.push_can_rx("dev1", 80, [])
        self.harness.step_update()
        resp = self.harness.pop_can_tx("dev1")
        self.assert_true(resp is not None, "Expected TX_RESET_ERR response")
        self.assert_equal(resp["id"], 81, "Response ID must be TX_RESET_ERR (81)")

        # Now send new stick packet
        self.harness.push_can_rx("dev1", 100, [127, 255, 127, 127, 127, 127])
        self.harness.step_update()
        self.assert_equal(self.harness.get_rc_override(6), 1900, "RC6 should resume control at 1900 us")

    def test_09_deinit_thrusters_disarm(self):
        # Arm first
        self.harness.push_can_rx("dev1", 60, [])
        self.harness.step_update()
        self.harness.clear_tx_queues()

        # Disarm (ID 70)
        self.harness.push_can_rx("dev1", 70, [])
        self.harness.step_update()
        self.assert_equal(self.harness.is_armed(), False, "Vehicle must be disarmed")
        resp = self.harness.pop_can_tx("dev1")
        self.assert_true(resp is not None, "Expected TX_DEINIT_THRUST response")
        self.assert_equal(resp["id"], 71, "Response ID must be TX_DEINIT_THRUST (71)")
        self.assert_equal(resp["data"], [1], "Payload must be [1]")

        # All overrides should be released (0)
        for ch in [1, 2, 3, 4, 5, 6]:
            self.assert_equal(self.harness.get_rc_override(ch), 0, f"RC{ch} override must be released (0 us)")

    def test_10_direct_motor_bench_test(self):
        # Starting disarmed
        self.assert_equal(self.harness.is_armed(), False)

        # Topside commands Motor 1 test:
        # Motor 1: 145 (1560 us), Motors 2..6: 128 (1500 us), Duration: 50 (500 ms)
        payload = [145, 128, 128, 128, 128, 128, 50]
        self.harness.push_can_rx("dev1", 170, payload)
        self.harness.step_update()

        # Vehicle must be temporarily armed in MANUAL mode (19)
        self.assert_equal(self.harness.is_armed(), True, "Vehicle must arm for motor test")
        self.assert_equal(self.harness.get_mode(), 19, "Vehicle must switch to MANUAL mode (19) during test")
        self.assert_true(abs(self.harness.get_srv_output(0) - 1560) <= 5, "Motor 1 output must be ~1560 us")
        self.assert_equal(self.harness.get_srv_output(1), 1500, "Motor 2 output must be neutral 1500 us")

        # Response 171 must NOT be sent yet (test still executing)
        self.assert_equal(self.harness.pop_can_tx("dev1"), None, "Response must not be sent before duration completes")

        # Advance time by 200ms -> still running
        self.harness.advance_time(200)
        self.harness.step_update()
        self.assert_equal(self.harness.is_armed(), True)

        # Advance time by 350ms (total 550ms > 500ms) -> test completes
        self.harness.advance_time(350)
        self.harness.step_update()

        # Motor outputs forced to neutral 1500 us
        self.assert_equal(self.harness.get_srv_output(0), 1500, "Motor 1 must be returned to 1500 us")
        # Vehicle was disarmed in STABILIZE (0) before test, so must be disarmed and back in STABILIZE now
        self.assert_equal(self.harness.is_armed(), False, "Vehicle must return to disarmed state")
        self.assert_equal(self.harness.get_mode(), 0, "Vehicle must restore prior flight mode (STABILIZE) after disarmed motor test")
        # Response 171 must be sent now
        resp = self.harness.pop_can_tx("dev1")
        self.assert_true(resp is not None, "Expected TX_TEST_MOTOR response")
        self.assert_equal(resp["id"], 171, "Response ID must be 171")
        self.assert_equal(resp["data"], [1], "Response payload must be [1]")

    def test_13_update_cycle_time(self):
        # Update cycle time to 20 ms
        self.harness.push_can_rx("dev1", 160, [20])
        delay = self.harness.step_update()
        self.assert_equal(delay, 20, "Cycle delay must be updated to 20 ms")
        resp = self.harness.pop_can_tx("dev1")
        self.assert_true(resp is not None)
        self.assert_equal(resp["id"], 161, "Response ID must be 161")
        self.assert_equal(resp["data"], [20], "Payload must confirm 20 ms")

        # Test invalid cycle time clamping (< 5 ms)
        self.harness.push_can_rx("dev1", 160, [2])
        delay = self.harness.step_update()
        self.assert_equal(delay, 20, "Cycle delay should not accept values < 5 ms")

    def test_14_dual_can_port_switching(self):
        # Send Health ping on CAN Port 2
        self.harness.push_can_rx("dev2", 40, [])
        self.harness.step_update()
        resp = self.harness.pop_can_tx("dev2")
        self.assert_true(resp is not None, "Response must be sent out on CAN2 when incoming from CAN2")
        self.assert_equal(resp["id"], 41, "Response ID must be 41")
        self.assert_equal(self.harness.pop_can_tx("dev1"), None, "CAN1 must have no frames")

    def test_15_filtering_dronecan_extended_and_rtr_frames(self):
        # Extended 29-bit frame (DroneCAN)
        self.harness.push_can_rx("dev1", 0x1F001234, [1, 2, 3, 4], is_ext=True)
        # RTR frame
        self.harness.push_can_rx("dev1", 40, [], is_rtr=True)
        # Error frame
        self.harness.push_can_rx("dev1", 40, [], is_err=True)
        self.harness.step_update()

        # None of these should generate any TX response or state change
        self.assert_equal(self.harness.pop_can_tx("dev1"), None, "Filtered frames must not generate responses")

    def test_16_long_run_streaming_stability(self):
        # Arm
        self.harness.push_can_rx("dev1", 60, [])
        self.harness.step_update()
        self.harness.clear_tx_queues()

        # Stream 500 control packets at 20ms intervals (10 seconds of continuous pilot drive)
        payload = [200, 150, 180, 140, 130, 120]
        start_mem = self.harness.eval_return_number("collectgarbage('count')")
        for i in range(500):
            self.harness.advance_time(20)
            self.harness.push_can_rx("dev1", 100, payload)
            self.harness.step_update()

        end_mem = self.harness.eval_return_number("collectgarbage('count')")
        mem_growth_kb = end_mem - start_mem
        # Growth should be minimal (< 50 KB over 500 iterations without GC)
        self.assert_true(mem_growth_kb < 100, f"Memory growth too high: {mem_growth_kb:.2f} KB over 500 ticks")
        self.assert_equal(self.harness.is_armed(), True, "Vehicle must stay armed throughout drive")
        self.assert_equal(self.harness.get_mode(), 0, "Flight mode must stay STABILIZE (0)")

    def test_17_mode_switch_retry_when_initially_rejected(self):
        # Arm vehicle in STABILIZE (0)
        self.harness.push_can_rx("dev1", 60, [])
        self.harness.step_update()
        self.assert_equal(self.harness.get_mode(), 0)

        # Simulate barometer not ready: reject mode switch to ALT_HOLD (2)
        self.harness.eval("vehicle.mode_change_allowed = false")

        # Pilot tries to switch to Dynamic Throttle / ALT_HOLD (ID 110)
        self.harness.push_can_rx("dev1", 110, [127, 127, 127, 127, 127, 127])
        self.harness.step_update()
        # Mode switch was rejected, so vehicle remains in mode 0
        self.assert_equal(self.harness.get_mode(), 0, "Mode should still be 0 because switch failed")

        # Now 2 seconds later, barometer completes calibration: mode changes are now allowed
        self.harness.eval("vehicle.mode_change_allowed = true")
        self.harness.advance_time(2000)

        # Pilot continues streaming Dynamic Throttle (ID 110)
        self.harness.push_can_rx("dev1", 110, [127, 127, 127, 127, 127, 127])
        self.harness.step_update()

        # Check: Did it retry and successfully transition to ALT_HOLD (2)?
        self.assert_equal(self.harness.get_mode(), 2, "Vehicle MUST retry mode change and switch to ALT_HOLD (2)")

    def test_18_driving_frame_during_active_motor_test_ignored(self):
        # Arm and apply non-neutral stick override before starting bench test
        self.harness.push_can_rx("dev1", 60, [0])
        self.harness.step_update()
        self.harness.push_can_rx("dev1", 100, [255, 127, 127, 127, 127, 127])
        self.harness.step_update()
        self.assert_equal(self.harness.get_rc_override(5), 1900, "RC5 Forward active before motor test")
        self.harness.clear_tx_queues()

        # Start motor test on Motor 1 for 1000ms
        self.harness.push_can_rx("dev1", 170, [145, 128, 128, 128, 128, 128, 100])
        self.harness.step_update()
        self.assert_equal(self.harness.get_mode(), 19, "Must be in MANUAL mode during motor test")
        self.assert_equal(self.harness.get_rc_override(5), 1500, "Active RC overrides must be neutralized when entering MANUAL for motor test")

        # An accidental controller frame arrives during the bench test
        self.harness.push_can_rx("dev1", 100, [255, 127, 127, 127, 127, 127])
        self.harness.step_update()

        # Vehicle MUST remain in MANUAL mode (19) until motor test is finished
        self.assert_equal(self.harness.get_mode(), 19, "Driving frame must not knock motor test out of MANUAL mode")
        self.assert_true(abs(self.harness.get_srv_output(0) - 1560) <= 5, "Motor 1 must continue test pulse")

    def test_20_dynamic_rc_parameter_auto_detection(self):
        # Re-initialize with ArduSub factory defaults (Pitch=1, Roll=2)
        self.harness.reset()
        self.harness.eval("mock = dofile([[" + MOCK_LUA_PATH.replace("\\", "/") + "]])")
        # Set parameters to factory default: Pitch=1, Roll=2
        self.harness.eval("param.params.RCMAP_PITCH = 1; param.params.RCMAP_ROLL = 2")
        self.harness.eval("dofile([[" + CAN_BRIDGE_LUA_PATH.replace("\\", "/") + "]])")
        self.harness.step_update()

        # Arm
        self.harness.push_can_rx("dev1", 60, [])
        self.harness.step_update()
        self.harness.clear_tx_queues()

        # Send stick values: Pitch nose-up (+100% -> 255), Roll level (127)
        # data[5] = Pitch (255), data[6] = Roll (127)
        payload = [127, 127, 127, 127, 255, 127]
        self.harness.push_can_rx("dev1", 100, payload)
        self.harness.step_update()

        # With RCMAP_PITCH = 1, Channel 1 must receive 1900 us!
        self.assert_equal(self.harness.get_rc_override(1), 1900, "RC1 (Pitch) must be 1900 us under factory RCMAP_PITCH=1")
        self.assert_equal(self.harness.get_rc_override(2), 1500, "RC2 (Roll) must be neutral 1500 us under factory RCMAP_ROLL=2")

    def test_22_concurrent_motor_test_rejection(self):
        # Start a motor test of duration 1000ms
        self.harness.push_can_rx("dev1", 170, [145, 128, 128, 128, 128, 128, 100])
        self.harness.step_update()
        self.assert_equal(self.harness.get_mode(), 19)
        self.assert_equal(self.harness.is_armed(), True)
        self.harness.clear_tx_queues()

        # Send a second concurrent motor test frame while first is still running
        self.harness.advance_time(200)
        self.harness.push_can_rx("dev1", 170, [128, 145, 128, 128, 128, 128, 50])
        self.harness.step_update()

        # Concurrent request must be rejected with payload [0]
        resp = self.harness.pop_can_tx("dev1")
        self.assert_true(resp is not None, "Expected busy rejection response")
        self.assert_equal(resp["id"], 171)
        self.assert_equal(resp["data"], [0], "Busy rejection payload must be [0]")

        # Vehicle must still be executing test 1 on motor 1
        self.assert_true(abs(self.harness.get_srv_output(0) - 1560) <= 5, "Motor 1 must continue its test")
        self.assert_equal(self.harness.is_armed(), True)

        # Advance to completion of test 1 (total 1050 ms)
        self.harness.advance_time(850)
        self.harness.step_update()
        self.assert_equal(self.harness.is_armed(), False, "Must cleanly disarm after test 1 completes")
        self.assert_equal(self.harness.get_srv_output(0), 1500)

    def test_24_auto_surface_on_missed_heartbeats_and_recovery(self):
        # Arm and switch to MANUAL mode (19) with forward thrust
        self.harness.push_can_rx("dev1", 60, [])
        self.harness.step_update()
        self.harness.push_can_rx("dev1", 90, [255, 127, 127, 127, 127, 127])
        self.harness.step_update()
        self.assert_equal(self.harness.get_mode(), 19, "Should be in MANUAL mode (19)")
        self.harness.clear_tx_queues()

        # 1. Advance 2000ms -> Ping #1 (ID 41, payload [2, 1, 19]) sent, watchdog tripped (neutral 1500 us)
        self.harness.advance_time(2000)
        self.harness.step_update()
        ping1 = self.harness.pop_can_tx("dev1")
        self.assert_true(ping1 is not None and ping1["id"] == 41 and ping1["data"] == [2, 1, 19], "Expected Health Ping #1 (ID 41, data [2, 1, 19])")
        self.assert_equal(self.harness.get_rc_override(3), 1500, "Throttle still neutral after Ping #1")

        # 2. Advance another 2000ms without reply -> Ping #1 missed (missed=1), Ping #2 (ID 41, payload [2, 1, 19]) sent
        self.harness.advance_time(2000)
        self.harness.step_update()
        ping2 = self.harness.pop_can_tx("dev1")
        self.assert_true(ping2 is not None and ping2["id"] == 41 and ping2["data"] == [2, 1, 19], "Expected Health Ping #2 (ID 41, data [2, 1, 19])")
        self.assert_equal(self.harness.get_rc_override(3), 1500, "Throttle still neutral before Ping #2 times out")

        # 3. Advance another 2000ms without reply -> Ping #2 missed (missed=2) -> Auto-Surface triggers!
        self.harness.advance_time(2000)
        self.harness.step_update()
        self.assert_equal(self.harness.get_mode(), 0, "Must switch to STABILIZE (0) for level ascent")
        self.assert_equal(self.harness.get_rc_override(3), 1550, "RC3 (Throttle) must command gentle ascent (1550 us)")
        for ch in [1, 2, 4, 5, 6]:
            self.assert_equal(self.harness.get_rc_override(ch), 1500, f"RC{ch} must remain neutral (1500 us)")

        # 4. Topside link recovers and sends Health ACK (ID 40, payload [2]) -> Auto-Surface cancelled immediately
        self.harness.clear_tx_queues()
        self.harness.push_can_rx("dev1", 40, [2])
        self.harness.step_update()
        self.assert_equal(self.harness.get_rc_override(3), 1500, "RC3 must return to neutral 1500 us on link recovery")
        self.assert_equal(self.harness.pop_can_tx("dev1"), None, "ID 40 with payload [2] must not echo back ID 41")

    def test_25_heartbeat_ack_prevents_surface_and_max_duration_disarm(self):
        # Arm
        self.harness.push_can_rx("dev1", 60, [])
        self.harness.step_update()
        self.harness.clear_tx_queues()

        # Simulate 5 heartbeat cycles (10 seconds) with no stick input, but GUI replying ID 40 [2] to each ID 41 [2] ping
        for _ in range(5):
            self.harness.advance_time(2000)
            self.harness.step_update()
            ping = self.harness.pop_can_tx("dev1")
            self.assert_true(ping is not None and ping["id"] == 41 and ping["data"] == [2, 1, 0], "Expected ID 41 [2, 1, 0] ping")
            # Topside GUI auto-replies ID 40 [2]
            self.harness.push_can_rx("dev1", 40, [2])
            self.harness.step_update()
            self.assert_equal(self.harness.pop_can_tx("dev1"), None, "ID 40 [2] must not trigger an echo")
            self.assert_equal(self.harness.get_rc_override(3), 1500, "Must stay neutral when Health ACK is received")

        # Now cut communication completely -> wait for 2 missed pings (3 intervals = 6000 ms) to enter auto-surface
        for _ in range(3):
            self.harness.advance_time(2000)
            self.harness.step_update()
        self.assert_equal(self.harness.get_rc_override(3), 1550, "Auto-surface active at 1550 us")

        # Advance 30,000 ms (MAX_SURFACE_DURATION_MS) -> must auto-disarm safely at surface
        self.harness.advance_time(30000)
        self.harness.step_update()
        self.assert_equal(self.harness.is_armed(), False, "Must auto-disarm after max surface duration (30s)")
        self.assert_equal(self.harness.get_rc_override(3), 0, "RC overrides must be released (0 us) upon disarm")

    def test_26_arm_with_requested_mode_and_speed_limit(self):
        # Set global speed limit to 50% (ID 120 -> 121) and verify MOT_PWM_MIN / MOT_PWM_MAX persistence
        self.harness.push_can_rx("dev1", 120, [50])
        self.harness.step_update()
        resp = self.harness.pop_can_tx("dev1")
        self.assert_true(resp is not None, "Expected TX_SET_SPEED_LIMIT response")
        self.assert_equal(resp["id"], 121, "Response ID must be TX_SET_SPEED_LIMIT (121)")
        self.assert_equal(resp["data"], [50], "Speed limit echo must be [50]")
        self.assert_equal(int(self.harness.eval_return_number("param:get('MOT_PWM_MIN')")), 1300, "MOT_PWM_MIN must be saved to 1300")
        self.assert_equal(int(self.harness.eval_return_number("param:get('MOT_PWM_MAX')")), 1700, "MOT_PWM_MAX must be saved to 1700")

        # Empty GET on ID 120 must return current 50%
        self.harness.push_can_rx("dev1", 120, [])
        self.harness.step_update()
        resp_get = self.harness.pop_can_tx("dev1")
        self.assert_true(resp_get is not None and resp_get["id"] == 121 and resp_get["data"] == [50], "GET ID 120 must return [50]")

        # Bench motor test (ID 170) must also be scaled by global 50% speed limit (255 -> 1900 raw -> 1700 scaled)
        self.harness.push_can_rx("dev1", 170, [255, 128, 128, 128, 128, 128, 50])
        self.harness.step_update()
        self.assert_equal(self.harness.get_srv_output(0), 1700, "Bench motor test must scale 1900 -> 1700 at 50% speed limit")
        self.harness.advance_time(550)
        self.harness.step_update()
        self.harness.clear_tx_queues()

        # Arm directly in MANUAL mode (ID 60 with [19])
        self.harness.push_can_rx("dev1", 60, [19])
        self.harness.step_update()
        self.assert_equal(self.harness.is_armed(), True, "Vehicle must be armed")
        self.assert_equal(self.harness.get_mode(), 19, "Vehicle must arm in requested MANUAL mode (19)")
        self.harness.clear_tx_queues()

        # RC overrides are NOT double-scaled (global cap is applied by ArduSub via MOT_PWM_MIN/MOT_PWM_MAX)
        self.harness.push_can_rx("dev1", 90, [255, 0, 127, 127, 127, 127])
        self.harness.step_update()
        self.assert_equal(self.harness.get_rc_override(5), 1900, "RC5 (Forward) stays full range (global cap via MOT_PWM_MAX)")
        self.assert_equal(self.harness.get_rc_override(6), 1100, "RC6 (Lateral) stays full range (global cap via MOT_PWM_MIN)")

        # Set speed limit to 0% -> MOT_PWM_MIN=1499, MOT_PWM_MAX=1501 (valid min < max) & full stick stays neutral (1500 us)
        self.harness.push_can_rx("dev1", 120, [0])
        self.harness.step_update()
        self.assert_equal(int(self.harness.eval_return_number("param:get('MOT_PWM_MIN')")), 1499, "MOT_PWM_MIN at 0% must be 1499")
        self.assert_equal(int(self.harness.eval_return_number("param:get('MOT_PWM_MAX')")), 1501, "MOT_PWM_MAX at 0% must be 1501")
        self.harness.push_can_rx("dev1", 90, [255, 0, 127, 127, 127, 127])
        self.harness.step_update()
        self.assert_equal(self.harness.get_rc_override(5), 1500, "RC5 at 0% limit must be 1500 us")
        self.assert_equal(self.harness.get_rc_override(6), 1500, "RC6 at 0% limit must be 1500 us")

        # Verify reboot reload: re-loading can_bridge.lua with persisted MOT_PWM_MIN=1300 / MOT_PWM_MAX=1700 restores 50%
        self.harness.eval("param.params.MOT_PWM_MIN = 1300; param.params.MOT_PWM_MAX = 1700")
        self.harness.eval("dofile([[" + CAN_BRIDGE_LUA_PATH.replace("\\", "/") + "]])")
        self.harness.step_update()
        self.harness.clear_tx_queues()
        self.harness.push_can_rx("dev1", 120, [])
        self.harness.step_update()
        resp_boot = self.harness.pop_can_tx("dev1")
        self.assert_true(resp_boot is not None and resp_boot["id"] == 121 and resp_boot["data"] == [50], "Reboot must reload 50% from MOT_PWM_MAX=1700")

    def test_27_emergency_disarm_cancels_active_motor_test(self):
        # Start a 1000 ms motor test on Motor 1
        self.harness.push_can_rx("dev1", 170, [145, 128, 128, 128, 128, 128, 100])
        self.harness.step_update()
        self.assert_equal(self.harness.is_armed(), True)
        self.assert_true(abs(self.harness.get_srv_output(0) - 1560) <= 5)
        self.harness.clear_tx_queues()

        # Emergency Disarm (ID 70) mid-test
        self.harness.advance_time(200)
        self.harness.push_can_rx("dev1", 70, [])
        self.harness.step_update()
        self.assert_equal(self.harness.is_armed(), False, "Vehicle must disarm immediately")
        self.assert_equal(self.harness.get_srv_output(0), 1500, "Motor 1 output must be neutralized on emergency disarm")
        resp = self.harness.pop_can_tx("dev1")
        self.assert_true(resp is not None and resp["id"] == 71 and resp["data"] == [1], "Expected disarm ACK (ID 71 [1])")

        # Advance past original test duration -> must NOT send stale TX_TEST_MOTOR (171)
        self.harness.advance_time(1000)
        self.harness.step_update()
        self.assert_equal(self.harness.pop_can_tx("dev1"), None, "Must not emit stale ID 171 after cancelled motor test")

    def test_28_motor_test_failure_restores_mode_and_disarms(self):
        # Case A: Arming denied during motor test from ALT_HOLD (mode 2) -> mode must be restored to 2
        self.harness.eval("vehicle._mode = 2; arming.arm_allowed = false; arming.arm_force_allowed = false")
        self.harness.push_can_rx("dev1", 170, [145, 128, 128, 128, 128, 128, 50])
        self.harness.step_update()
        resp = self.harness.pop_can_tx("dev1")
        self.assert_true(resp is not None and resp["id"] == 171 and resp["data"] == [0], "Expected motor test fail [0]")
        self.assert_equal(self.harness.is_armed(), False, "Must remain disarmed")
        self.assert_equal(self.harness.get_mode(), 2, "Must restore prior mode (ALT_HOLD) on arm failure")

        # Case B: SRV_Channels output throws after arming -> must disarm & restore prior mode
        self.harness.eval("arming.arm_allowed = true; arming.arm_force_allowed = true; SRV_Channels.fail_output = true")
        self.harness.push_can_rx("dev1", 170, [145, 128, 128, 128, 128, 128, 50])
        self.harness.step_update()
        resp = self.harness.pop_can_tx("dev1")
        self.assert_true(resp is not None and resp["id"] == 171 and resp["data"] == [0], "Expected motor test output fail [0]")
        self.assert_equal(self.harness.is_armed(), False, "Must disarm when motor output fails")
        self.assert_equal(self.harness.get_mode(), 2, "Must restore prior mode (ALT_HOLD) on output failure")

    def test_29_empty_update_cycle_ignores_stale_buffer(self):
        # Send speed limit 50 (populates rx_data[1] = 50)
        self.harness.push_can_rx("dev1", 120, [50])
        self.harness.step_update()
        self.harness.clear_tx_queues()

        # Send 0-byte Update Cycle Time frame (ID 160, dlc = 0) -> must keep 10 ms, NOT read stale 50
        self.harness.push_can_rx("dev1", 160, [])
        delay = self.harness.step_update()
        self.assert_equal(delay, 10, "0-byte ID 160 must not read stale rx_data[1]")
        resp = self.harness.pop_can_tx("dev1")
        self.assert_true(resp is not None and resp["id"] == 161 and resp["data"] == [10], "Echo must remain 10 ms")

    def test_30_external_arm_resets_timers_cleanly(self):
        # Advance time far past heartbeat & watchdog thresholds while disarmed
        self.harness.advance_time(10000)
        self.harness.step_update()
        self.harness.clear_tx_queues()

        # Simulate external GCS arming (not via CAN ID 60)
        self.harness.eval("arming._armed = true")
        self.harness.step_update()

        # Must NOT immediately fire a heartbeat ping or trip watchdog on the very first armed tick
        self.assert_equal(self.harness.pop_can_tx("dev1"), None, "Must not immediately send heartbeat ping on external arm")
        self.assert_equal(self.harness.get_rc_override(3), 0, "Must not immediately trip watchdog on external arm")

        # After 600 ms without pilot input, watchdog should trip normally
        self.harness.advance_time(600)
        self.harness.step_update()
        self.assert_equal(self.harness.get_rc_override(3), 1500, "Watchdog should neutralize after 500 ms")

    def test_31_external_disarm_cancels_active_motor_test(self):
        # Start motor test for 1000 ms
        self.harness.push_can_rx("dev1", 170, [145, 128, 128, 128, 128, 128, 100])
        self.harness.step_update()
        self.assert_equal(self.harness.is_armed(), True)
        self.harness.clear_tx_queues()

        # External disarm (e.g. from QGC)
        self.harness.eval("arming._armed = false")
        self.harness.advance_time(200)
        self.harness.step_update()
        self.assert_equal(self.harness.get_srv_output(0), 1500, "Motor 1 must neutralize on external disarm")

        # Advance past original duration -> must NOT emit stale ID 171
        self.harness.advance_time(1000)
        self.harness.step_update()
        self.assert_equal(self.harness.pop_can_tx("dev1"), None, "Must not emit stale ID 171 after external disarm")

    def test_32_motor_config_directions_and_port_mapping(self):
        # 1. GET default motor config (ID 130 empty -> ID 131 [0, 1, 2, 3, 4, 5, 6])
        self.harness.push_can_rx("dev1", 130, [])
        self.harness.step_update()
        resp = self.harness.pop_can_tx("dev1")
        self.assert_true(resp is not None and resp["id"] == 131, "Expected TX_MOTOR_CONFIG (131)")
        self.assert_equal(resp["data"], [0, 1, 2, 3, 4, 5, 6], "Default motor config must be normal dir, ports 1..6")

        # 2. SET: Reverse M1 (bit 0 = 1) and M4 (bit 3 = 8) => dir_mask = 9, swap ports of M1 and M2 => [9, 2, 1, 3, 4, 5, 6]
        self.harness.push_can_rx("dev1", 130, [9, 2, 1, 3, 4, 5, 6])
        self.harness.step_update()
        resp = self.harness.pop_can_tx("dev1")
        self.assert_equal(resp["data"], [9, 2, 1, 3, 4, 5, 6], "Updated motor config must echo back")
        self.assert_equal(int(self.harness.eval_return_number("param:get('MOT_1_DIRECTION')")), -1, "MOT_1_DIRECTION must be -1")
        self.assert_equal(int(self.harness.eval_return_number("param:get('MOT_2_DIRECTION')")), 1, "MOT_2_DIRECTION must be +1")
        self.assert_equal(int(self.harness.eval_return_number("param:get('MOT_4_DIRECTION')")), -1, "MOT_4_DIRECTION must be -1")
        self.assert_equal(int(self.harness.eval_return_number("param:get('SERVO2_FUNCTION')")), 33, "SERVO2 must be k_motor1 (33)")
        self.assert_equal(int(self.harness.eval_return_number("param:get('SERVO1_FUNCTION')")), 34, "SERVO1 must be k_motor2 (34)")

        # 3. Verify direct motor test for M1 now drives physical servo channel 1 (0-indexed SERVO2)
        #    AND respects MOT_1_DIRECTION = -1 (1560 us inverted around 1500 -> 1440 us)!
        self.harness.push_can_rx("dev1", 170, [145, 128, 128, 128, 128, 128, 50])
        self.harness.step_update()
        self.assert_true(abs(self.harness.get_srv_output(1) - 1440) <= 5, "M1 test must route to remapped SERVO2 (chan 1) and invert for MOT_1_DIRECTION=-1")
        self.assert_equal(self.harness.get_srv_output(0), 1500, "SERVO1 (now M2) must remain neutral 1500 us")

        # 4. Reject invalid duplicate port mapping [0, 1, 1, 3, 4, 5, 6] -> must keep previous valid config
        self.harness.advance_time(600)
        self.harness.step_update()
        self.harness.clear_tx_queues()
        self.harness.push_can_rx("dev1", 130, [0, 1, 1, 3, 4, 5, 6])
        self.harness.step_update()
        resp = self.harness.pop_can_tx("dev1")
        self.assert_equal(resp["data"], [9, 2, 1, 3, 4, 5, 6], "Duplicate port assignment must be rejected")

        # 5. Reject motor config changes while armed -> must keep previous config and echo it back
        self.harness.push_can_rx("dev1", 60, [0])
        self.harness.step_update()
        self.harness.clear_tx_queues()
        self.harness.push_can_rx("dev1", 130, [0, 1, 2, 3, 4, 5, 6])
        self.harness.step_update()
        resp = self.harness.pop_can_tx("dev1")
        self.assert_equal(resp["data"], [9, 2, 1, 3, 4, 5, 6], "Motor config write must be rejected while armed")
        self.assert_equal(int(self.harness.eval_return_number("param:get('MOT_1_DIRECTION')")), -1, "MOT_1_DIRECTION must remain unchanged while armed")

    def test_33_per_axis_gains_get_set_and_scaling(self):
        # 1. GET default axis gains (ID 140 empty -> ID 141 [100, 100, 100, 100, 100, 100])
        self.harness.push_can_rx("dev1", 140, [])
        self.harness.step_update()
        resp = self.harness.pop_can_tx("dev1")
        self.assert_true(resp is not None and resp["id"] == 141, "Expected TX_AXIS_GAINS (141)")
        self.assert_equal(resp["data"], [100, 100, 100, 100, 100, 100], "Default axis gains must all be 100%")

        # 2. SET axis gains: FB=50%, LR=25%, UD=100%, Yaw=50%, Pitch=0%, Roll=100%
        self.harness.push_can_rx("dev1", 140, [50, 25, 100, 50, 0, 100])
        self.harness.step_update()
        resp = self.harness.pop_can_tx("dev1")
        self.assert_equal(resp["data"], [50, 25, 100, 50, 0, 100], "Updated axis gains must echo back")
        self.assert_equal(int(self.harness.eval_return_number("param:get('SCR_USER1')")), 51, "SCR_USER1 must store FB gain+1 (51)")
        self.assert_equal(int(self.harness.eval_return_number("param:get('SCR_USER5')")), 1, "SCR_USER5 must store Pitch 0% as 1")

        # 3. Arm and drive full stick on all axes -> verify per-axis scaling
        self.harness.push_can_rx("dev1", 60, [0])
        self.harness.step_update()
        self.harness.push_can_rx("dev1", 100, [255, 255, 255, 0, 255, 0])
        self.harness.step_update()
        self.assert_equal(self.harness.get_rc_override(5), 1700, "RC5 (Forward 50%) must be 1700 us")
        self.assert_equal(self.harness.get_rc_override(6), 1600, "RC6 (Lateral 25%) must be 1600 us")
        self.assert_equal(self.harness.get_rc_override(3), 1900, "RC3 (Throttle 100%) must be 1900 us")
        self.assert_equal(self.harness.get_rc_override(4), 1300, "RC4 (Yaw 50% rev) must be 1300 us")
        self.assert_equal(self.harness.get_rc_override(2), 1500, "RC2 (Pitch 0%) must be 1500 us")
        self.assert_equal(self.harness.get_rc_override(1), 1100, "RC1 (Roll 100% rev) must be 1100 us")

        # 4. Global speed limit 50% saves MOT_PWM_MIN/MAX without double-scaling RC5 (remains 1700 us for 50% axis gain)
        self.harness.push_can_rx("dev1", 120, [50])
        self.harness.step_update()
        self.assert_equal(int(self.harness.eval_return_number("param:get('MOT_PWM_MAX')")), 1700, "MOT_PWM_MAX must be 1700 at 50% global limit")
        self.harness.push_can_rx("dev1", 100, [255, 127, 127, 127, 127, 127])
        self.harness.step_update()
        self.assert_equal(self.harness.get_rc_override(5), 1700, "RC5 must stay 1700 us (50% axis gain, no double-scaling with MOT_PWM_MAX)")

    def test_34_non_bridge_traffic_does_not_hijack_active_port(self):
        # Establish CAN1 as active port
        self.harness.push_can_rx("dev1", 40, [])
        self.harness.step_update()
        resp = self.harness.pop_can_tx("dev1")
        self.assert_true(resp is not None and resp["id"] == 41, "Expected initial reply on CAN1")

        # Arm on CAN1 so periodic heartbeat will transmit on active_can_dev
        self.harness.push_can_rx("dev1", 60, [0])
        self.harness.step_update()
        self.harness.clear_tx_queues()

        # Inject DroneCAN extended frame and Gripper (0x301) frame on CAN2
        self.harness.push_can_rx("dev2", 0x18000001, [1, 2, 3], is_ext=True)
        self.harness.push_can_rx("dev2", 0x301, [0, 45, 0])
        self.harness.step_update()

        # Advance 2000ms to trigger ROV heartbeat -> must still transmit on CAN1, NOT CAN2
        self.harness.advance_time(2000)
        self.harness.step_update()
        hb1 = self.harness.pop_can_tx("dev1")
        hb2 = self.harness.pop_can_tx("dev2")
        self.assert_true(hb1 is not None and hb1["id"] == 41 and hb1["data"] == [2, 1, 0], "Heartbeat must remain on CAN1")
        self.assert_true(hb2 is None, "Non-bridge traffic on CAN2 must not hijack active_can_dev")

    def test_35_rapid_flight_mode_switching_without_throttle(self):
        # Arm in MANUAL (19)
        self.harness.push_can_rx("dev1", 60, [19])
        self.harness.step_update()
        self.assert_equal(self.harness.get_mode(), 19, "Must start in MANUAL (19)")

        # Switch to STABILIZE (0) after only 50ms
        self.harness.advance_time(50)
        self.harness.push_can_rx("dev1", 100, [127, 127, 127, 127, 127, 127])
        self.harness.step_update()
        self.assert_equal(self.harness.get_mode(), 0, "Must switch immediately to STABILIZE (0)")

        # Switch to ALT_HOLD (2) after only 50ms more (well inside 1000ms retry interval)
        self.harness.advance_time(50)
        self.harness.push_can_rx("dev1", 110, [127, 127, 127, 127, 127, 127])
        self.harness.step_update()
        self.assert_equal(self.harness.get_mode(), 2, "Must switch immediately to ALT_HOLD (2)")

        # Switch back to MANUAL (19) after only 50ms more
        self.harness.advance_time(50)
        self.harness.push_can_rx("dev1", 90, [127, 127, 127, 127, 127, 127])
        self.harness.step_update()
        self.assert_equal(self.harness.get_mode(), 19, "Must switch immediately back to MANUAL (19)")

    def test_36_arm_rejected_during_active_motor_test(self):
        # Start motor test while disarmed (500ms duration)
        self.assert_equal(self.harness.is_armed(), False, "Must start disarmed")
        self.harness.push_can_rx("dev1", 170, [160, 128, 128, 128, 128, 128, 50])
        self.harness.step_update()
        self.assert_equal(self.harness.is_armed(), True, "Motor test must arm temporarily")
        self.assert_equal(self.harness.get_mode(), 19, "Motor test must be in MANUAL (19)")
        self.harness.clear_tx_queues()

        # Send Arm (ID 60 [0]) mid-test -> must be rejected with [0] and stay in MANUAL
        self.harness.advance_time(100)
        self.harness.push_can_rx("dev1", 60, [0])
        self.harness.step_update()
        resp = self.harness.pop_can_tx("dev1")
        self.assert_true(resp is not None and resp["id"] == 61 and resp["data"] == [0], "Arm during motor test must return [0]")
        self.assert_equal(self.harness.get_mode(), 19, "Mode must remain MANUAL (19) during active motor test")

        # Complete motor test -> must disarm cleanly and send ID 171 [1]
        self.harness.advance_time(450)
        self.harness.step_update()
        self.assert_equal(self.harness.is_armed(), False, "Must disarm after motor test finishes")
        done = self.harness.pop_can_tx("dev1")
        self.assert_true(done is not None and done["id"] == 171 and done["data"] == [1], "Motor test must still complete cleanly")

    def test_37_pid_params_get_set_and_validation(self):
        import struct

        # 1. GET ATC_RAT_RLL_P (param_id=2, default 0.135)
        self.harness.push_can_rx("dev1", 150, [2])
        self.harness.step_update()
        resp = self.harness.pop_can_tx("dev1")
        self.assert_true(resp is not None and resp["id"] == 151 and len(resp["data"]) == 6, "Expected 6-byte TX_PID_PARAM (151)")
        self.assert_equal(resp["data"][0], 2, "param_id must echo 2")
        self.assert_equal(resp["data"][1], 1, "GET status must be 1 (OK)")
        val = struct.unpack("<f", bytes(resp["data"][2:6]))[0]
        self.assert_true(abs(val - 0.135) < 1e-5, f"Expected ATC_RAT_RLL_P=0.135, got {val}")

        # 2. SET PSC_D_VEL_P (param_id=17) to 10.25 with save_flag=1
        payload = [17, 1] + list(struct.pack("<f", 10.25))
        self.harness.push_can_rx("dev1", 150, payload)
        self.harness.step_update()
        resp = self.harness.pop_can_tx("dev1")
        self.assert_equal(resp["data"][0], 17, "param_id must echo 17")
        self.assert_equal(resp["data"][1], 1, "SET status must be 1 (OK)")
        val = struct.unpack("<f", bytes(resp["data"][2:6]))[0]
        self.assert_true(abs(val - 10.25) < 1e-5, f"Expected PSC_D_VEL_P=10.25, got {val}")
        self.assert_true(abs(self.harness.eval_return_number("param:get('PSC_D_VEL_P')") - 10.25) < 1e-5)

        # 3. Live tuning while armed: SET ATC_RAT_PIT_D (param_id=9) to 0.0045
        self.harness.push_can_rx("dev1", 60, [0])
        self.harness.step_update()
        self.harness.clear_tx_queues()
        payload = [9, 1] + list(struct.pack("<f", 0.0045))
        self.harness.push_can_rx("dev1", 150, payload)
        self.harness.step_update()
        resp = self.harness.pop_can_tx("dev1")
        self.assert_equal(resp["data"][1], 1, "Live PID tuning while armed must succeed")
        val = struct.unpack("<f", bytes(resp["data"][2:6]))[0]
        self.assert_true(abs(val - 0.0045) < 1e-5, f"Expected ATC_RAT_PIT_D=0.0045, got {val}")

        # 4. Reject out-of-range value (ATC_RAT_RLL_P = 99.0 > max 2.0) -> status=0, unchanged
        payload = [2, 1] + list(struct.pack("<f", 99.0))
        self.harness.push_can_rx("dev1", 150, payload)
        self.harness.step_update()
        resp = self.harness.pop_can_tx("dev1")
        self.assert_equal(resp["data"][1], 0, "Out-of-range PID write must return status=0")
        val = struct.unpack("<f", bytes(resp["data"][2:6]))[0]
        self.assert_true(abs(val - 0.135) < 1e-5, "Rejected PID must retain previous value 0.135")

        # 5. Reject NaN float -> status=0, unchanged
        payload = [2, 1] + list(struct.pack("<f", float("nan")))
        self.harness.push_can_rx("dev1", 150, payload)
        self.harness.step_update()
        resp = self.harness.pop_can_tx("dev1")
        self.assert_equal(resp["data"][1], 0, "NaN PID write must return status=0")

        # 6. Reject unknown param_id (99) -> status=0
        self.harness.push_can_rx("dev1", 150, [99])
        self.harness.step_update()
        resp = self.harness.pop_can_tx("dev1")
        self.assert_equal(resp["data"][0], 99)
        self.assert_equal(resp["data"][1], 0, "Unknown param_id must return status=0")

    def run_all(self):
        print("=" * 70)
        print("  UStall ROV 2026 - Pixhawk 4 / ArduSub CAN Bridge Test Bench")
        print("=" * 70)
        tests = [
            ("Test 01: Initial Load & GCS Greeting", self.test_01_init_and_gcs_greeting),
            ("Test 02: Health Ping (ID 40 -> 41)", self.test_02_health_ping),
            ("Test 03: Init Connection (ID 50 -> 51)", self.test_03_init_conn),
            ("Test 04: Init Thrusters Arming (ID 60 -> 61, Mode 0)", self.test_04_init_thrusters_arm),
            ("Test 05: Pilot Drive Static Throttle (ID 100 -> RC overrides)", self.test_05_driving_static_throttle_stabilize),
            ("Test 06: Pilot Drive Dynamic Throttle (ID 110 -> ALT_HOLD)", self.test_06_driving_dynamic_throttle_alt_hold),
            ("Test 07: Watchdog Timeout & Failsafe Neutral", self.test_07_watchdog_pilot_timeout),
            ("Test 08: Watchdog Error Reset & Resume (ID 80 -> 81)", self.test_08_watchdog_recovery),
            ("Test 09: Deinit Thrusters Disarming (ID 70 -> 71, Release RC)", self.test_09_deinit_thrusters_disarm),
            ("Test 10: Direct Motor Bench Test (ID 170 -> 171, Manual Mode)", self.test_10_direct_motor_bench_test),
            ("Test 13: Loop Cycle Time Adjustment (ID 160 -> 161)", self.test_13_update_cycle_time),
            ("Test 14: Dual CAN Port Redundancy (CAN1 <-> CAN2)", self.test_14_dual_can_port_switching),
            ("Test 15: DroneCAN Extended Frame & RTR Filtering", self.test_15_filtering_dronecan_extended_and_rtr_frames),
            ("Test 16: Long-Run Streaming Stability & Heap Growth", self.test_16_long_run_streaming_stability),
            ("Test 17: Mode Switch Retry on Sensor Delay", self.test_17_mode_switch_retry_when_initially_rejected),
            ("Test 18: Isolation of Active Bench Motor Test", self.test_18_driving_frame_during_active_motor_test_ignored),
            ("Test 20: Dynamic RCMAP Auto-Detection", self.test_20_dynamic_rc_parameter_auto_detection),
            ("Test 22: Concurrent Motor Test Overlap Rejection", self.test_22_concurrent_motor_test_rejection),
            ("Test 23: Pilot Drive Manual Mode (ID 90 -> MANUAL)", self.test_23_driving_manual_mode),
            ("Test 24: Auto-Surface on 2 Missed Heartbeat Pings & Recovery", self.test_24_auto_surface_on_missed_heartbeats_and_recovery),
            ("Test 25: Heartbeat ACK Idle Keep-Alive & Max-Surface Disarm", self.test_25_heartbeat_ack_prevents_surface_and_max_duration_disarm),
            ("Test 26: Arm in Requested Mode & Pixhawk Speed Limit", self.test_26_arm_with_requested_mode_and_speed_limit),
            ("Test 27: Emergency Disarm Cancels Active Motor Test", self.test_27_emergency_disarm_cancels_active_motor_test),
            ("Test 28: Motor Test Failure Restores Mode & Disarms", self.test_28_motor_test_failure_restores_mode_and_disarms),
            ("Test 29: Empty Cycle Time Frame Ignores Stale Buffer", self.test_29_empty_update_cycle_ignores_stale_buffer),
            ("Test 30: External Arm Resets Heartbeat/Watchdog Timers", self.test_30_external_arm_resets_timers_cleanly),
            ("Test 31: External Disarm Cancels Active Motor Test", self.test_31_external_disarm_cancels_active_motor_test),
            ("Test 32: Motor Config Directions & ESC Port Mapping (ID 130 -> 131)", self.test_32_motor_config_directions_and_port_mapping),
            ("Test 33: Per-Axis Gains Get/Set & Combined Scaling (ID 140 -> 141)", self.test_33_per_axis_gains_get_set_and_scaling),
            ("Test 34: Non-Bridge Traffic Does Not Hijack Active CAN Port", self.test_34_non_bridge_traffic_does_not_hijack_active_port),
            ("Test 35: Rapid Flight Mode Switching Without Throttle", self.test_35_rapid_flight_mode_switching_without_throttle),
            ("Test 36: Arm Rejected During Active Bench Motor Test", self.test_36_arm_rejected_during_active_motor_test),
            ("Test 37: Attitude & Depth Hold PID Get/Set & Validation (ID 150 -> 151)", self.test_37_pid_params_get_set_and_validation),
        ]

        for name, fn in tests:
            self.run_test(name, fn)

        print("=" * 70)
        print(f"Results: {self.passed} PASSED, {self.failed} FAILED out of {len(tests)} tests")
        print("=" * 70)
        return self.failed == 0

if __name__ == "__main__":
    tb = TestBench()
    success = tb.run_all()
    sys.exit(0 if success else 1)
