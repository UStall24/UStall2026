using System;
using System.Collections.Generic;
using System.IO.Ports;
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
        private const int CANUSB_TTY_BAUD_RATE_DEFAULT = 2000000;

        private SerialPort sp = null;

        public delegate void OnCanMessageArrival(ushort rxId, byte[] data);

        public static Dictionary<ushort, OnCanMessageArrival> canMessageArrivalEvents = new();

        private string comPort;

        public bool DebugMode { get; set; } = false;

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

        public bool IsOpen
        {
            get => sp == null || sp.IsOpen;
        }

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
            if (!FindCanAdapter())
                return false;
            if (sp == null || force)
            {
                CanusbSpeed speed = CanusbSpeed.CANUSB_SPEED_500000;
                int baudrate = CANUSB_TTY_BAUD_RATE_DEFAULT;

                sp = UsbCAN.AdapterInit(comPort, baudrate);
                sp.DataReceived += SerialPortDataRecievedEventHandler;
                if (sp == null)
                {
                    Log("Failed to open serial port.");
                    return false;
                }

                UsbCAN.CommandSettings(
                    sp,
                    speed,
                    CanusbMode.CANUSB_MODE_NORMAL,
                    CanusbFrameType.CANUSB_FRAME_STANDARD
                );
            }
            return true;
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
                Log($"[{DateTime.Now:HH:mm:ss}] RX ID 0x{rxId:X3} Data: {BitConverter.ToString(data)}");
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
            if (data == null)
                data = Array.Empty<byte>();
            byte idLsb = (byte)(txId & 0xFF);
            byte idMsb = (byte)(txId >> 8 & 0xFF);
            Console.WriteLine($"Sending TX ID 0x{txId:X3} Data: {BitConverter.ToString(data)}");
            return UsbCAN.SendDataFrame(sp, CanusbFrameType.CANUSB_FRAME_STANDARD, idLsb, idMsb, data, data.Length) == 0;
        }

        public bool Close()
        {
            return UsbCAN.AdapterClose(sp);
        }
    }
}