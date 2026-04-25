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
        private const CanusbSpeed speed = CanusbSpeed.CANUSB_SPEED_100000;
        private SerialPort sp = null;

        public delegate void OnCanMessageArrival(ushort rxId, byte[] data);

        public static Dictionary<ushort, OnCanMessageArrival> canMessageArrivalEvents = new();

        private string comPort;

        public bool DebugMode { get; set; } = true;

        // Helper printing method
        private void Log(string msg)
        {
            if (DebugMode)
                Console.WriteLine(msg);
        }

        public UsbCANWrapper()
        {
            ConnectToCanAdapter();
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

        public bool ConnectToCanAdapter(bool force = false)
        {
            // If already open and no force requested, nothing to do.
            if (sp != null && sp.IsOpen && !force)
                return true;

            if (!FindCanAdapter())
                return false;

            // If force requested and adapter is open, close it first.
            if (sp != null && sp.IsOpen && force)
            {
                try
                {
                    Log("Force reconnect requested - closing existing serial port.");
                    // Detach handler to avoid duplicates (safe even if not attached)
                    try { sp.DataReceived -= SerialPortDataRecievedEventHandler; } catch { }
                    UsbCAN.AdapterClose(sp);
                }
                catch (Exception ex)
                {
                    Log("Error closing serial port during reconnect: " + ex.Message);
                }
                finally
                {
                    sp = null;
                }
            }

            // If we have a SerialPort instance that is closed, ensure handler is detached and clear it so we create a fresh one.
            if (sp != null && !sp.IsOpen)
            {
                try { sp.DataReceived -= SerialPortDataRecievedEventHandler; } catch { }
                sp = null;
            }

            // Initialize a new adapter if needed
            if (sp == null)
            {
                sp = UsbCAN.AdapterInit(comPort);
                if (sp == null)
                {
                    Log("Failed to open serial port.");
                    return false;
                }

                // Attach handler after confirming adapter opened
                sp.DataReceived += SerialPortDataRecievedEventHandler;

                UsbCAN.CommandSettings(
                    sp,
                    speed,
                    CanusbMode.CANUSB_MODE_NORMAL,
                    CanusbFrameType.CANUSB_FRAME_STANDARD
                );
                PrintSettings();
            }
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
                (ushort)RecieveAddressesControlUnit.Health_response,
                OnHealthResponse);

            // Transmit the request (empty payload)
            Console.Clear();
            _time_required = Environment.TickCount;
            SendFrame((ushort)SendAddressesControlUnit.Health, Array.Empty<byte>());

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs && !_hasHealthResponse)
            {
                await Task.Delay(10);
            }

            _isTestingConnection = false;

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
            try
            {
                int toRead = sp.BytesToRead;
                if (toRead <= 0)
                    return;

                var tmp = new byte[toRead];
                int read = sp.Read(tmp, 0, toRead);
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
            ConnectToCanAdapter();
            if (data == null)
                data = Array.Empty<byte>();
            byte idLsb = (byte)(txId & 0xFF);
            byte idMsb = (byte)(txId >> 8 & 0xFF);
            // Console.WriteLine($"Sending TX ID {txId} Data: {BitConverter.ToString(data)}");
            return UsbCAN.SendDataFrame(sp, CanusbFrameType.CANUSB_FRAME_STANDARD, idLsb, idMsb, data, data.Length) == 0;
        }

        public bool Close()
        {
            return UsbCAN.AdapterClose(sp);
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