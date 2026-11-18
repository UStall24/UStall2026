using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Threading.Tasks;
using Xceed.Wpf.Toolkit.Core.Converters;

namespace UStallGUI.Helpers
{
    public struct CanMessage
    {
        public DateTime Timestamp;
        public byte[] Data;
    }

    public class UsbCANWrapper
    {
        private const CanusbSpeed speed = CanusbSpeed.CANUSB_SPEED_125000;
        private SerialPort sp = null;

        public delegate void OnCanMessageArrival(ushort rxId, byte[] data);

        public static Dictionary<ushort, OnCanMessageArrival> canMessageArrivalEvents = new();

        private string comPort;

        public string ComPort => comPort;

        public bool DebugMode { get; set; } = true;

        // Helper printing method
        private void Log(string msg)
        {
            if (DebugMode)
                Console.WriteLine(msg);
        }

        private static readonly byte[] HeartbeatAckPayload = new byte[] { 2 };

        public UsbCANWrapper()
        {
            RegisterCallback((ushort)RecieveAddressesControlUnit.Health, OnHealthFrameReceived);
            ConnectToCanAdapter();
        }

        private void OnHealthFrameReceived(ushort rxId, byte[] data)
        {
            // Payload [2] indicates a Pixhawk-initiated periodic health ping -> reply with [2] on ID 40
            if (data != null && data.Length > 0 && data[0] == 2 && IsOpen)
            {
                SendFrame((ushort)SendAddressesControlUnit.Health, HeartbeatAckPayload);
            }
        }

        public bool IsOpen => sp != null && sp.IsOpen;

        private readonly object _rxLock = new();
        private readonly List<byte> _rxBuffer = new();

        public bool FindCanAdapter()
        {
            comPort = UsbCAN.FindComPortByName("USB-SERIAL CH340");
            if (string.IsNullOrEmpty(comPort))
            {
                Log("USB-SERIAL CH340 not found.");
                return false;
            }

            Log($"Using port: {comPort}");

            Console.CancelKeyPress += (s, e) =>
            {
                Log("Stopping...");
                e.Cancel = true;
            };

            return true;
        }

        private void ResetSerialPort()
        {
            var portToClose = sp;
            sp = null;

            if (portToClose != null)
            {
                try { portToClose.DataReceived -= SerialPortDataRecievedEventHandler; } catch { }
                try { UsbCAN.AdapterClose(portToClose); } catch { }
            }

            lock (_rxLock)
            {
                _rxBuffer.Clear();
            }
        }

        public bool ConnectToCanAdapter(bool force = false)
        {
            // If already open and no force requested, nothing to do.
            if (sp != null && sp.IsOpen && !force)
                return true;

            // If force requested or port is closed/broken, clean up existing port before reconnecting.
            if (sp != null)
            {
                if (force)
                    Log("Force reconnect requested - closing existing serial port.");
                ResetSerialPort();
            }

            if (!FindCanAdapter())
                return false;

            sp = UsbCAN.AdapterInit(comPort);
            if (sp == null)
            {
                Log("Failed to open serial port.");
                return false;
            }

            // Attach handler after confirming adapter opened
            sp.DataReceived += SerialPortDataRecievedEventHandler;

            if (UsbCAN.CommandSettings(
                sp,
                speed,
                CanusbMode.CANUSB_MODE_NORMAL,
                CanusbFrameType.CANUSB_FRAME_STANDARD) < 0)
            {
                Log("Failed to apply USB-CAN adapter settings.");
                ResetSerialPort();
                return false;
            }

            PrintSettings();
            return true;
        }

        // ---------------------------------------------------------------------
        //  Connection‑test helper – clearer naming and safer async handling
        // ---------------------------------------------------------------------

        private bool _isTestingConnection = false;          // true while a test is in progress
        private bool _hasHealthResponse = false;           // set by the callback when the reply arrives
        private int _time_required = 0;

        /// <summary>
        /// Sends a Health request and waits up to <paramref name="timeoutMs"/> for the reply.
        /// </summary>
        public async Task<bool> TestConnectionAsync(int timeoutMs = 500)
        {
            // Reset state before starting a new test
            _isTestingConnection = true;
            _hasHealthResponse = false;

            // Register a one‑shot callback that will capture the response
            RegisterCallback(
                (ushort)RecieveAddressesControlUnit.Health,
                OnHealthResponse);

            try
            {
                // Transmit the request (empty payload)
                _time_required = Environment.TickCount;
                SendFrame((ushort)SendAddressesControlUnit.Health, Array.Empty<byte>());

                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < timeoutMs && !_hasHealthResponse)
                {
                    await Task.Delay(10);
                }
            }
            finally
            {
                UnregisterCallback(
                    (ushort)RecieveAddressesControlUnit.Health,
                    OnHealthResponse);
                _isTestingConnection = false;
            }

            if (_hasHealthResponse)
            {
                Log($"Health response received within timeout. Time required: {_time_required}ms");
                return true;
            }

            Log("Health response not received within timeout.");
            return false;
        }

        /// <summary>
        /// Callback invoked when the Health response frame arrives.
        /// </summary>
        private void OnHealthResponse(ushort rxId, byte[] data)
        {
            _time_required = Environment.TickCount - _time_required;
            _hasHealthResponse = true;
            Log($"Health response received. Data: {BitConverter.ToString(data)}");
        }

        private void SerialPortDataRecievedEventHandler(object sender, SerialDataReceivedEventArgs e)
        {
            var currentPort = sender as SerialPort ?? sp;
            if (currentPort == null || !currentPort.IsOpen)
                return;

            try
            {
                int toRead = currentPort.BytesToRead;
                if (toRead <= 0)
                    return;

                var tmp = new byte[toRead];
                int read = currentPort.Read(tmp, 0, toRead);
                if (read <= 0)
                    return;

                lock (_rxLock)
                {
                    // append new bytes to buffer
                    _rxBuffer.AddRange(tmp.AsSpan(0, read).ToArray());
                    ProcessRxBuffer();
                }
            }
            catch (Exception ex)
            {
                Log("SerialPortDataRecievedEventHandler error: " + ex.Message);
                ResetSerialPort();
            }
        }

        private void ProcessRxBuffer()
        {
            while (true)
            {
                // Need at least header
                if (_rxBuffer.Count < 6)
                    return;

                // Sync to 0xAA start byte
                if (_rxBuffer[0] != 0xAA)
                {
                    _rxBuffer.RemoveAt(0);
                    continue;
                }

                byte header = _rxBuffer[1];

                // Check for CAN data frame: high nibble == 0xC
                if (header >> 4 != 0x0C)
                {
                    // Unknown frame type: drop start byte and resync
                    _rxBuffer.RemoveAt(0);
                    continue;
                }

                int dlc = header & 0x0F;
                int frameLen = dlc + 5; // AA + header + ID_L + ID_H + data(dlc) + 0x55(end) => but end byte index = frameLen-1

                // Wait for full frame
                if (_rxBuffer.Count < frameLen)
                    return;

                // Check end byte 0x55
                if (_rxBuffer[frameLen - 1] != 0x55)
                {
                    // Corrupt frame, drop first byte and resync
                    _rxBuffer.RemoveAt(0);
                    continue;
                }

                // Extract ID and data
                ushort rxId = (ushort)(_rxBuffer[3] << 8 | _rxBuffer[2]);
                var data = new byte[dlc];
                if (dlc > 0)
                    _rxBuffer.CopyTo(4, data, 0, dlc);

                // Remove this frame from buffer
                _rxBuffer.RemoveRange(0, frameLen);

                // Log + callback
                Log($"[{DateTime.Now:HH:mm:ss}] RX ID {rxId} Data: {BitConverter.ToString(data)}");
                TriggerCallback(rxId, data);
            }
        }

        ~UsbCANWrapper()
        {
            UsbCAN.AdapterClose(sp);
            Log("Serial port closed.");
        }

        public bool RegisterCallback(ushort rxId, Action<ushort, byte[]> action)
        {
            if (!canMessageArrivalEvents.ContainsKey(rxId))
                canMessageArrivalEvents[rxId] = null; // initialize

            var delegateList = canMessageArrivalEvents[rxId]?.GetInvocationList();
            if (delegateList != null)
            {
                foreach (var del in delegateList)
                {
                    if (del.Target.Equals(action))
                    {
                        // Already registered
                        return false;
                    }
                }
            }
            canMessageArrivalEvents[rxId] += new OnCanMessageArrival(action);
            return true;
        }

        public bool UnregisterCallback(ushort rxId, Action<ushort, byte[]> action)
        {
            if (canMessageArrivalEvents.ContainsKey(rxId))
            {
                canMessageArrivalEvents[rxId] -= new OnCanMessageArrival(action);
                return true;
            }
            return false;
        }

        private void TriggerCallback(ushort rxId, byte[] data)
        {
            if (canMessageArrivalEvents.TryGetValue(rxId, out var handler) && handler != null)
            {
                handler(rxId, data);
            }
        }

        public bool SendFrame(ushort txId, byte[] data = null)
        {
            if (!ConnectToCanAdapter() || sp == null || !sp.IsOpen)
                return false;

            if (data == null)
                data = Array.Empty<byte>();
            byte idLsb = (byte)(txId & 0xFF);
            byte idMsb = (byte)(txId >> 8 & 0xFF);
            // Console.WriteLine($"Sending TX ID {txId} Data: {BitConverter.ToString(data)}");
            if (UsbCAN.SendDataFrame(sp, CanusbFrameType.CANUSB_FRAME_STANDARD, idLsb, idMsb, data, data.Length) != 0)
            {
                ResetSerialPort();
                return false;
            }

            return true;
        }

        public bool Close()
        {
            ResetSerialPort();
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
