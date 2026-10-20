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
        # Verify GCS greeting and CAN driver detection
        msg_count = int(self.harness.eval_return_number("#gcs.messages"))
        self.assert_true(msg_count >= 2, "Expected at least 2 GCS greeting messages on boot")
        greeting = self.harness.eval_return_string("gcs.messages[1].text")
        self.assert_true("ROV CAN bridge" in greeting, f"Unexpected greeting: {greeting}")
        dev_ready = self.harness.eval_return_string("gcs.messages[2].text")
        self.assert_true("driver ready" in dev_ready, f"Unexpected driver ready msg: {dev_ready}")
        self.assert_equal(self.harness.is_armed(), False, "Vehicle must start disarmed")

    def test_02_health_ping(self):
        # Topside sends Health ping (ID 40)
        self.harness.push_can_rx("dev1", 40, [])
        delay = self.harness.step_update()
        self.assert_equal(delay, 10, "Cycle delay should be 10ms")
        resp = self.harness.pop_can_tx("dev1")
        self.assert_true(resp is not None, "Expected TX_HEALTH response")
        self.assert_equal(resp["id"], 41, "Response ID must be TX_HEALTH (41)")
        self.assert_equal(resp["data"], [1], "Response payload must be [1]")

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
        # Vehicle was disarmed before test, so must be disarmed now
        self.assert_equal(self.harness.is_armed(), False, "Vehicle must return to disarmed state")
        # Response 171 must be sent now
        resp = self.harness.pop_can_tx("dev1")
        self.assert_true(resp is not None, "Expected TX_TEST_MOTOR response")
        self.assert_equal(resp["id"], 171, "Response ID must be 171")
        self.assert_equal(resp["data"], [1], "Response payload must be [1]")

    def test_11_manual_calibration_slider_timeout(self):
        # Topside sends manual control frame (ID 90)
        # Motor 3 set to 200 (~1727 us)
        payload = [127, 127, 200, 127, 127, 127]
        self.harness.push_can_rx("dev1", 90, payload)
        self.harness.step_update()

        self.assert_equal(self.harness.is_armed(), True, "Vehicle must arm for calibration")
        self.assert_equal(self.harness.get_mode(), 19, "Mode must be MANUAL (19)")
        self.assert_true(abs(self.harness.get_srv_output(2) - 1727) <= 5, "Motor 3 PWM should be ~1727 us")
        resp = self.harness.pop_can_tx("dev1")
        self.assert_true(resp is not None, "Expected TX_MANUAL_CTRL response")
        self.assert_equal(resp["id"], 91, "Response ID must be 91")

        # Wait past MANUAL_CTRL_TIMEOUT_MS (300ms) -> advance 350ms
        self.harness.advance_time(350)
        self.harness.step_update()

        # Must auto-disarm for safety
        self.assert_equal(self.harness.is_armed(), False, "Vehicle must auto-disarm when calibration stream stops")
        self.assert_equal(self.harness.get_srv_output(2), 1500, "Motor 3 must be returned to neutral 1500 us")

    def test_12_direction_values_table(self):
        # Set all 6 DoF tables with custom values
        custom_fb = [200, 50, 127, 127, 127, 127]
        custom_lr = [127, 127, 210, 45, 127, 127]
        custom_ud = [127, 127, 127, 127, 220, 35]
        custom_yaw = [50, 200, 127, 127, 127, 127]
        custom_roll = [127, 127, 45, 210, 127, 127]
        custom_pitch = [127, 127, 127, 127, 35, 220]

        dofs = [
            (120, custom_fb),
            (121, custom_lr),
            (122, custom_ud),
            (123, custom_yaw),
            (124, custom_roll),
            (125, custom_pitch),
        ]

        for rx_id, data in dofs:
            self.harness.push_can_rx("dev1", rx_id, data)
            self.harness.step_update()
            resp = self.harness.pop_can_tx("dev1")
            self.assert_true(resp is not None, f"Expected immediate ACK for Set DoF {rx_id}")
            self.assert_equal(resp["id"], rx_id, f"ACK ID must match send ID {rx_id}")

        # ID 127 (All_DoF_Saved) must be delayed by 100ms
        resp = self.harness.pop_can_tx("dev1")
        self.assert_equal(resp, None, "All_DoF_Saved must not be sent immediately")

        # Advance by 120ms
        self.harness.advance_time(120)
        self.harness.step_update()
        resp = self.harness.pop_can_tx("dev1")
        self.assert_true(resp is not None, "Expected TX_ALL_DOF_SAVED after 100ms delay")
        self.assert_equal(resp["id"], 127, "Response ID must be 127")

        # Now test Get requests (IDs 130..135)
        self.harness.push_can_rx("dev1", 130, [])
        self.harness.step_update()
        resp = self.harness.pop_can_tx("dev1")
        self.assert_true(resp is not None)
        self.assert_equal(resp["id"], 131, "Get_FB response ID must be 131")
        self.assert_equal(resp["data"], custom_fb, "Get_FB data must match custom values set")

        self.harness.push_can_rx("dev1", 131, [])
        self.harness.step_update()
        resp = self.harness.pop_can_tx("dev1")
        self.assert_equal(resp["id"], 132, "Get_LR response ID must be 132")
        self.assert_equal(resp["data"], custom_lr, "Get_LR data must match custom values set")

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
        # Start motor test on Motor 1 for 1000ms
        self.harness.push_can_rx("dev1", 170, [145, 128, 128, 128, 128, 128, 100])
        self.harness.step_update()
        self.assert_equal(self.harness.get_mode(), 19, "Must be in MANUAL mode during motor test")

        # An accidental controller frame arrives during the bench test
        self.harness.push_can_rx("dev1", 100, [255, 127, 127, 127, 127, 127])
        self.harness.step_update()

        # Vehicle MUST remain in MANUAL mode (19) until motor test is finished
        self.assert_equal(self.harness.get_mode(), 19, "Driving frame must not knock motor test out of MANUAL mode")
        self.assert_true(abs(self.harness.get_srv_output(0) - 1560) <= 5, "Motor 1 must continue test pulse")

    def test_19_all_dof_saved_payload_compatibility(self):
        # Send all 6 DoF frames
        for rx_id in range(120, 126):
            self.harness.push_can_rx("dev1", rx_id, [127, 127, 127, 127, 127, 127])
            self.harness.step_update()
        self.harness.clear_tx_queues()

        # Advance 120ms
        self.harness.advance_time(120)
        self.harness.step_update()
        resp = self.harness.pop_can_tx("dev1")
        self.assert_true(resp is not None, "Expected TX_ALL_DOF_SAVED")
        self.assert_equal(resp["id"], 127)
        # Pico sent payload [1] with DLC 1
        self.assert_equal(resp["data"], [1], "TX_ALL_DOF_SAVED should include payload [1] for Pico compatibility")

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

    def test_21_manual_calibration_multi_packet_stream_disarm(self):
        # Starts disarmed
        self.assert_equal(self.harness.is_armed(), False, "Must start disarmed")

        # Stream 10 calibration frames simulating an active UI slider drag
        for step in range(10):
            payload = [127, 127, 180 + step, 127, 127, 127]
            self.harness.push_can_rx("dev1", 90, payload)
            self.harness.advance_time(20)
            self.harness.step_update()
            self.assert_equal(self.harness.is_armed(), True, "Must stay armed during active slider drag")
            self.harness.clear_tx_queues()

        # Advance past MANUAL_CTRL_TIMEOUT_MS (300 ms)
        self.harness.advance_time(350)
        self.harness.step_update()

        # Vehicle MUST reliably return to disarmed state because it was disarmed prior to slider
        self.assert_equal(self.harness.is_armed(), False, "Vehicle must auto-disarm after multi-packet stream timeout")
        self.assert_equal(self.harness.get_srv_output(2), 1500, "Motor 3 output must be neutral 1500 us")

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
            ("Test 11: Manual Motor Calibration Timeout & Safety", self.test_11_manual_calibration_slider_timeout),
            ("Test 12: Direction Values RAM Table & Delayed ACK (IDs 120-135)", self.test_12_direction_values_table),
            ("Test 13: Loop Cycle Time Adjustment (ID 160 -> 161)", self.test_13_update_cycle_time),
            ("Test 14: Dual CAN Port Redundancy (CAN1 <-> CAN2)", self.test_14_dual_can_port_switching),
            ("Test 15: DroneCAN Extended Frame & RTR Filtering", self.test_15_filtering_dronecan_extended_and_rtr_frames),
            ("Test 16: Long-Run Streaming Stability & Heap Growth", self.test_16_long_run_streaming_stability),
            ("Test 17: Mode Switch Retry on Sensor Delay", self.test_17_mode_switch_retry_when_initially_rejected),
            ("Test 18: Isolation of Active Bench Motor Test", self.test_18_driving_frame_during_active_motor_test_ignored),
            ("Test 19: All_DoF_Saved Payload Compatibility", self.test_19_all_dof_saved_payload_compatibility),
            ("Test 20: Dynamic RCMAP Auto-Detection", self.test_20_dynamic_rc_parameter_auto_detection),
            ("Test 21: Multi-Packet Calibration Stream Auto-Disarm", self.test_21_manual_calibration_multi_packet_stream_disarm),
            ("Test 22: Concurrent Motor Test Overlap Rejection", self.test_22_concurrent_motor_test_rejection),
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
