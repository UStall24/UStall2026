-- mock_ardupilot.lua
-- Comprehensive ArduPilot / ArduSub Lua Scripting Environment Mock for Testing
-- Project: UStall ROV 2026

local M = {}

M.sim_time_ms = 1000

function M.advance_time(ms)
    M.sim_time_ms = M.sim_time_ms + ms
end

function M.set_time(ms)
    M.sim_time_ms = ms
end

-- 32-bit unsigned integer simulation
function uint32_t(val)
    local v = 0
    if type(val) == "table" and val._val then
        v = val._val
    elseif type(val) == "number" then
        v = math.floor(val)
    end
    v = v % 0x100000000
    local obj = { _val = v }
    function obj:toint()
        return math.floor(self._val)
    end
    setmetatable(obj, {
        __tostring = function(self) return tostring(math.floor(self._val)) end,
        __eq = function(a, b)
            local va = (type(a) == "table" and a._val) or a
            local vb = (type(b) == "table" and b._val) or b
            return va == vb
        end
    })
    return obj
end

function millis()
    return uint32_t(M.sim_time_ms)
end

-- GCS Mock
gcs = {
    messages = {},
    send_text = function(self, severity, text)
        table.insert(self.messages, { severity = severity, text = tostring(text), time = M.sim_time_ms })
    end,
    clear = function(self)
        self.messages = {}
    end
}

-- CANFrame Mock
local CANFrame_mt = {}
CANFrame_mt.__index = CANFrame_mt

function CANFrame()
    local self = {
        _id = 0,
        _dlc = 0,
        _data = {0, 0, 0, 0, 0, 0, 0, 0},
        _is_ext = false,
        _is_err = false,
        _is_rtr = false,
    }
    return setmetatable(self, CANFrame_mt)
end

function CANFrame_mt:id(new_id)
    if new_id ~= nil then
        if type(new_id) == "table" and new_id._val then
            self._id = new_id._val
        else
            self._id = math.floor(new_id)
        end
    end
    return uint32_t(self._id)
end

function CANFrame_mt:id_signed()
    return math.floor(self._id)
end

function CANFrame_mt:dlc(new_dlc)
    if new_dlc ~= nil then
        self._dlc = new_dlc
    end
    return self._dlc
end

function CANFrame_mt:data(idx, new_val)
    if new_val ~= nil then
        self._data[idx + 1] = new_val
    end
    return self._data[idx + 1] or 0
end

function CANFrame_mt:isExtended()
    return self._is_ext
end

function CANFrame_mt:isErrorFrame()
    return self._is_err
end

function CANFrame_mt:isRemoteTransmissionRequest()
    return self._is_rtr
end

-- CAN Device Mock
local MockCANDevice_mt = {}
MockCANDevice_mt.__index = MockCANDevice_mt

function MockCANDevice(name)
    local self = {
        name = name,
        rx_queue = {},
        tx_queue = {},
    }
    return setmetatable(self, MockCANDevice_mt)
end

function MockCANDevice_mt:read_frame()
    if #self.rx_queue > 0 then
        return table.remove(self.rx_queue, 1)
    end
    return nil
end

function MockCANDevice_mt:write_frame(frame, timeout_us)
    local copy = CANFrame()
    copy:id(frame:id_signed())
    copy:dlc(frame:dlc())
    for i = 0, frame:dlc() - 1 do
        copy:data(i, frame:data(i))
    end
    table.insert(self.tx_queue, copy)
    return true
end

function MockCANDevice_mt:push_rx(id, data_bytes, is_ext, is_err, is_rtr)
    local f = CANFrame()
    f:id(id)
    f._is_ext = is_ext or false
    f._is_err = is_err or false
    f._is_rtr = is_rtr or false
    local len = data_bytes and #data_bytes or 0
    f:dlc(len)
    for i = 1, len do
        f:data(i - 1, data_bytes[i])
    end
    table.insert(self.rx_queue, f)
end

function MockCANDevice_mt:clear(self)
    self.rx_queue = {}
    self.tx_queue = {}
end

CAN = {
    dev1 = MockCANDevice("CAN1"),
    dev2 = MockCANDevice("CAN2"),
    get_device = function(self, buf_size)
        return self.dev1
    end,
    get_device2 = function(self, buf_size)
        return self.dev2
    end,
}

-- RC Channel Mock
local RCChannel_mt = {}
RCChannel_mt.__index = RCChannel_mt

function RCChannel(ch_num)
    return setmetatable({ ch_num = ch_num, override_pwm = 0 }, RCChannel_mt)
end

function RCChannel_mt:set_override(pwm)
    self.override_pwm = pwm
end

rc = {
    channels = {},
    get_channel = function(self, ch_num)
        if not self.channels[ch_num] then
            self.channels[ch_num] = RCChannel(ch_num)
        end
        return self.channels[ch_num]
    end
}

-- SRV_Channels Mock
SRV_Channels = {
    outputs = {}, -- chan (0-indexed) -> { pwm = ..., timeout_ms = ... }
    find_channel = function(self, func_num)
        -- k_motor1 (33) .. k_motor6 (38) mapped to 0..5
        if func_num >= 33 and func_num <= 38 then
            return func_num - 33
        end
        return false
    end,
    set_output_pwm_chan_timeout = function(self, chan, pwm, timeout_ms)
        self.outputs[chan] = { pwm = pwm, timeout_ms = timeout_ms, timestamp = M.sim_time_ms }
    end
}

-- Arming Mock
arming = {
    _armed = false,
    arm_allowed = true,
    is_armed = function(self)
        return self._armed
    end,
    arm = function(self)
        if not self.arm_allowed then return false end
        self._armed = true
        return true
    end,
    arm_force = function(self)
        self._armed = true
        return true
    end,
    disarm = function(self)
        self._armed = false
        return true
    end
}

-- Vehicle Mock
vehicle = {
    _mode = 0, -- 0 = STABILIZE
    mode_change_allowed = true,
    get_mode = function(self)
        return self._mode
    end,
    set_mode = function(self, mode)
        if not self.mode_change_allowed then return false end
        self._mode = mode
        return true
    end
}

-- Parameter Mock
param = {
    params = {
        RCMAP_FORWARD  = 5,
        RCMAP_LATERAL  = 6,
        RCMAP_THROTTLE = 3,
        RCMAP_YAW      = 4,
        RCMAP_PITCH    = 2,
        RCMAP_ROLL     = 1,
    },
    get = function(self, name)
        return self.params[name]
    end,
    set = function(self, name, val)
        self.params[name] = val
    end
}

function M.reset_state()
    M.sim_time_ms = 1000
    gcs.messages = {}
    CAN.dev1.rx_queue = {}
    CAN.dev1.tx_queue = {}
    CAN.dev2.rx_queue = {}
    CAN.dev2.tx_queue = {}
    SRV_Channels.outputs = {}
    arming._armed = false
    arming.arm_allowed = true
    vehicle._mode = 0
    vehicle.mode_change_allowed = true
    for _, ch in pairs(rc.channels) do
        ch.override_pwm = 0
    end
end

return M
