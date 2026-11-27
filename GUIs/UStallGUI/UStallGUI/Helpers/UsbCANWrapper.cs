using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Ports;
using System.Threading.Tasks;

namespace UStallGUI.Helpers
{
    public readonly record struct ImuAttitudeSample(double RollDeg, double PitchDeg, double YawDeg, bool AhrsHealthy);
    public readonly record struct ImuVectorSample(double X, double Y, double Z);

    public class UsbCANWrapper
    {
        private const CanusbSpeed Speed = CanusbSpeed.CANUSB_SPEED_125000;
        private static readonly byte[] HeartbeatAckPayload = new byte[] { 2 };

        private readonly object _txLock = new();
        private readonly object _rxLock = new();
        private readonly object _cbLock = new();
        private readonly List<byte> _rxBuffer = new();
        private readonly Dictionary<ushort, Action<ushort, byte[]>> _callbacks = new();

        private SerialPort sp;
        private string comPort;

        public string ComPort => comPort;
        public bool DebugMode { get; set; } = true;
        public bool IsOpen => sp != null && sp.IsOpen;

        /// <summary>
        /// Fired when Pixhawk sends a 3-byte health/telemetry frame on ID 41: (isArmed, arduSubMode).
        /// </summary>
        public event Action<bool, byte>? TelemetryReceived;

        /// <summary>
        /// Fired when Pixhawk sends AHRS Attitude (ID 182), Gyro (ID 183), or Accel (ID 184) frames.
        /// </summary>
        public event Action<ImuAttitudeSample>? ImuAttitudeReceived;
        public event Action<ImuVectorSample>? ImuGyroReceived;
        public event Action<ImuVectorSample>? ImuAccelReceived;

        public UsbCANWrapper()
        {
            RegisterCallback((ushort)RecieveAddressesControlUnit.Health, OnHealthFrameReceived);
            RegisterCallback((ushort)RecieveAddressesControlUnit.ImuAttitude, OnImuAttitudeFrameReceived);
            RegisterCallback((ushort)RecieveAddressesControlUnit.ImuGyro, OnImuGyroFrameReceived);
            RegisterCallback((ushort)RecieveAddressesControlUnit.ImuAccel, OnImuAccelFrameReceived);
            ConnectToCanAdapter();
        }

        private void Log(string msg)
        {
            if (DebugMode)
                Console.WriteLine(msg);
        }

        private void OnHealthFrameReceived(ushort rxId, byte[] data)
        {
            if (data == null || data.Length == 0)
                return;

            // Bytes [1]=armed (0/1), [2]=ArduSub mode when 3-byte telemetry payload is present
            if (data.Length >= 3)
            {
                TelemetryReceived?.Invoke(data[1] == 1, data[2]);
            }

            // Payload [2, ...] indicates a Pixhawk-initiated periodic health ping -> reply with [2] on ID 40
            if (data[0] == 2 && IsOpen)
            {
                SendFrame((ushort)SendAddressesControlUnit.Health, HeartbeatAckPayload);
            }
        }

        private void OnImuAttitudeFrameReceived(ushort rxId, byte[] data)
        {
            if (data == null || data.Length < 6)
                return;

            double rollDeg = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(0, 2)) / 100.0;
            double pitchDeg = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(2, 2)) / 100.0;
            double yawDeg = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(4, 2)) / 100.0;
            bool healthy = data.Length >= 7 && (data[6] & 1) != 0;
            ImuAttitudeReceived?.Invoke(new ImuAttitudeSample(rollDeg, pitchDeg, yawDeg, healthy));
        }

        private void OnImuGyroFrameReceived(ushort rxId, byte[] data)
        {
            if (data == null || data.Length < 6)
                return;

            double gx = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(0, 2)) / 100.0;
            double gy = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(2, 2)) / 100.0;
            double gz = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(4, 2)) / 100.0;
            ImuGyroReceived?.Invoke(new ImuVectorSample(gx, gy, gz));
        }

        private void OnImuAccelFrameReceived(ushort rxId, byte[] data)
        {
            if (data == null || data.Length < 6)
                return;

            double ax = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(0, 2)) / 100.0;
            double ay = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(2, 2)) / 100.0;
            double az = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(4, 2)) / 100.0;
            ImuAccelReceived?.Invoke(new ImuVectorSample(ax, ay, az));
        }

        public bool FindCanAdapter()
        {
            comPort = UsbCAN.FindComPortByName("USB-SERIAL CH340");
            if (string.IsNullOrEmpty(comPort))
            {
                Log("USB-SERIAL CH340 not found.");
                return false;
            }

            Log($"Using port: {comPort}");
            return true;
        }

        public bool ConnectToCanAdapter(bool force = false)
        {
            lock (_txLock)
            {
                if (sp != null && sp.IsOpen && !force)
                    return true;

                if (!FindCanAdapter())
                    return false;

                ClosePortLocked();

                sp = UsbCAN.AdapterInit(comPort);
                if (sp == null)
                {
                    Log("Failed to open serial port.");
                    return false;
                }

                lock (_rxLock)
                {
                    _rxBuffer.Clear();
                }

                sp.DataReceived += SerialPortDataRecievedEventHandler;

                UsbCAN.CommandSettings(
                    sp,
                    Speed,
                    CanusbMode.CANUSB_MODE_NORMAL,
                    CanusbFrameType.CANUSB_FRAME_STANDARD
                );
                PrintSettings();
                return true;
            }
        }

        private void ClosePortLocked()
        {
            if (sp == null)
                return;

            try { sp.DataReceived -= SerialPortDataRecievedEventHandler; } catch { }
            try { UsbCAN.AdapterClose(sp); } catch (Exception ex) { Log("Error closing serial port: " + ex.Message); }
            try { sp.Dispose(); } catch { }
            sp = null;
        }

        public async Task<byte[]> SendAndWaitForResponseAsync(
            ushort sendId,
            ushort recvId,
            byte[]? payload = null,
            int timeoutMs = 400,
            Func<byte[], bool>? predicate = null)
        {
            if (!IsOpen)
                return Array.Empty<byte>();

            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Handler(ushort id, byte[] data)
            {
                if (id != recvId)
                    return;

                var safeData = data ?? Array.Empty<byte>();
                if (predicate != null && !predicate(safeData))
                    return;

                tcs.TrySetResult(safeData);
            }

            RegisterCallback(recvId, Handler);
            try
            {
                if (!SendFrame(sendId, payload))
                    return Array.Empty<byte>();

                var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
                return completed == tcs.Task ? await tcs.Task : Array.Empty<byte>();
            }
            finally
            {
                UnregisterCallback(recvId, Handler);
            }
        }

        /// <summary>
        /// Sends a Health request and waits up to <paramref name="timeoutMs"/> for the reply.
        /// </summary>
        public async Task<bool> TestConnectionAsync(int timeoutMs = 500)
        {
            var resp = await SendAndWaitForResponseAsync(
                (ushort)SendAddressesControlUnit.Health,
                (ushort)RecieveAddressesControlUnit.Health,
                Array.Empty<byte>(),
                timeoutMs);

            bool ok = resp.Length > 0;
            Log(ok ? "Health response received within timeout." : "Health response not received within timeout.");
            return ok;
        }

        private void SerialPortDataRecievedEventHandler(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                var port = sp;
                if (port == null || !port.IsOpen)
                    return;

                int toRead = port.BytesToRead;
                if (toRead <= 0)
                    return;

                var tmp = new byte[toRead];
                int read = port.Read(tmp, 0, toRead);
                if (read <= 0)
                    return;

                List<(ushort rxId, byte[] data)>? frames = null;
                lock (_rxLock)
                {
                    for (int i = 0; i < read; i++)
                        _rxBuffer.Add(tmp[i]);

                    ProcessRxBuffer(ref frames);
                }

                if (frames != null)
                {
                    foreach (var (rxId, data) in frames)
                    {
                        if (rxId != (ushort)RecieveAddressesControlUnit.ImuAttitude &&
                            rxId != (ushort)RecieveAddressesControlUnit.ImuGyro &&
                            rxId != (ushort)RecieveAddressesControlUnit.ImuAccel)
                        {
                            Log($"[{DateTime.Now:HH:mm:ss}] RX ID {rxId} Data: {BitConverter.ToString(data)}");
                        }
                        TriggerCallback(rxId, data);
                    }
                }
            }
            catch (Exception ex)
            {
                Log("SerialPortDataRecievedEventHandler error: " + ex.Message);
            }
        }

        private void ProcessRxBuffer(ref List<(ushort rxId, byte[] data)>? frames)
        {
            while (true)
            {
                // Minimum Waveshare CAN data frame is 5 bytes (0-byte DLC: AA C0 ID_L ID_H 55)
                if (_rxBuffer.Count < 5)
                    return;

                // Sync to 0xAA start byte
                if (_rxBuffer[0] != 0xAA)
                {
                    _rxBuffer.RemoveAt(0);
                    continue;
                }

                byte header = _rxBuffer[1];
                int dlc = header & 0x0F;

                // Check for CAN data frame: high nibble == 0xC and valid DLC (0..8)
                if ((header >> 4) != 0x0C || dlc > 8)
                {
                    _rxBuffer.RemoveAt(0);
                    continue;
                }

                int frameLen = dlc + 5; // AA + header + ID_L + ID_H + data(dlc) + 0x55
                if (_rxBuffer.Count < frameLen)
                    return;

                // Check end byte 0x55
                if (_rxBuffer[frameLen - 1] != 0x55)
                {
                    _rxBuffer.RemoveAt(0);
                    continue;
                }

                ushort rxId = (ushort)((_rxBuffer[3] << 8) | _rxBuffer[2]);
                var data = dlc > 0 ? new byte[dlc] : Array.Empty<byte>();
                if (dlc > 0)
                    _rxBuffer.CopyTo(4, data, 0, dlc);

                _rxBuffer.RemoveRange(0, frameLen);

                frames ??= new List<(ushort rxId, byte[] data)>();
                frames.Add((rxId, data));
            }
        }

        ~UsbCANWrapper()
        {
            Close();
        }

        public bool RegisterCallback(ushort rxId, Action<ushort, byte[]> action)
        {
            if (action == null)
                return false;

            lock (_cbLock)
            {
                if (_callbacks.TryGetValue(rxId, out var existing) && existing != null)
                {
                    foreach (var del in existing.GetInvocationList())
                    {
                        if (del.Equals(action))
                            return false;
                    }
                    _callbacks[rxId] = existing + action;
                }
                else
                {
                    _callbacks[rxId] = action;
                }
                return true;
            }
        }

        public bool UnregisterCallback(ushort rxId, Action<ushort, byte[]> action)
        {
            if (action == null)
                return false;

            lock (_cbLock)
            {
                if (!_callbacks.TryGetValue(rxId, out var existing) || existing == null)
                    return false;

                var updated = existing - action;
                if (updated == null)
                    _callbacks.Remove(rxId);
                else
                    _callbacks[rxId] = updated;
                return true;
            }
        }

        private void TriggerCallback(ushort rxId, byte[] data)
        {
            Action<ushort, byte[]> handler;
            lock (_cbLock)
            {
                _callbacks.TryGetValue(rxId, out handler);
            }
            handler?.Invoke(rxId, data);
        }

        public bool SendFrame(ushort txId, byte[] data = null)
        {
            data ??= Array.Empty<byte>();
            byte idLsb = (byte)(txId & 0xFF);
            byte idMsb = (byte)((txId >> 8) & 0xFF);

            lock (_txLock)
            {
                if (sp == null || !sp.IsOpen)
                    return false;

                return UsbCAN.SendDataFrame(sp, CanusbFrameType.CANUSB_FRAME_STANDARD, idLsb, idMsb, data, data.Length) == 0;
            }
        }

        public bool Close()
        {
            lock (_txLock)
            {
                ClosePortLocked();
            }
            return true;
        }

        public void PrintSettings()
        {
            Console.WriteLine("=== USB CAN Adapter Settings ===");
            Console.WriteLine($"COM Port      : {comPort}");
            Console.WriteLine($"Is Open       : {sp?.IsOpen}");
            Console.WriteLine($"Baudrate      : {sp?.BaudRate}");
            Console.WriteLine($"Data Bits     : {sp?.DataBits}");
            Console.WriteLine($"Parity        : {sp?.Parity}");
            Console.WriteLine($"Stop Bits     : {sp?.StopBits}");
            Console.WriteLine($"Handshake     : {sp?.Handshake}");
            Console.WriteLine($"Debug Mode    : {DebugMode}");
            Console.WriteLine("================================");
        }
    }
}
