using System;
using System.IO.Ports;
using System.Management;

namespace UStallGUI.Helpers
{
    public enum CanusbSpeed : byte
    {
        CANUSB_SPEED_1000000 = 0x01,
        CANUSB_SPEED_800000 = 0x02,
        CANUSB_SPEED_500000 = 0x03,
        CANUSB_SPEED_400000 = 0x04,
        CANUSB_SPEED_250000 = 0x05,
        CANUSB_SPEED_200000 = 0x06,
        CANUSB_SPEED_125000 = 0x07,
        CANUSB_SPEED_100000 = 0x08,
        CANUSB_SPEED_50000 = 0x09,
        CANUSB_SPEED_20000 = 0x0a,
        CANUSB_SPEED_10000 = 0x0b,
        CANUSB_SPEED_5000 = 0x0c,
    }

    public enum CanusbMode : byte
    {
        CANUSB_MODE_NORMAL = 0x00,
        CANUSB_MODE_LOOPBACK = 0x01,
        CANUSB_MODE_SILENT = 0x02,
        CANUSB_MODE_LOOPBACK_SILENT = 0x03,
    }

    public enum CanusbFrameType : byte
    {
        CANUSB_FRAME_STANDARD = 0x01,
        CANUSB_FRAME_EXTENDED = 0x02,
    }

    public static class UsbCAN
    {
        private const int CANUSB_TTY_BAUD_RATE_DEFAULT = 2000000;

        public static int GenerateChecksum(byte[] data, int offset, int length)
        {
            int checksum = 0;
            for (int i = 0; i < length; i++)
                checksum += data[offset + i];
            return checksum & 0xff;
        }

        public static int FrameSend(SerialPort sp, byte[] frame, int frameLen)
        {
            if (sp == null || !sp.IsOpen)
                return -1;

            try
            {
                sp.Write(frame, 0, frameLen);
                return frameLen;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Write failed: " + ex.Message);
                return -1;
            }
        }

        public static int CommandSettings(SerialPort sp, CanusbSpeed speed, CanusbMode mode, CanusbFrameType frameType)
        {
            byte[] cmdFrame = new byte[20];
            cmdFrame[0] = 0xaa;
            cmdFrame[1] = 0x55;
            cmdFrame[2] = 0x12;
            cmdFrame[3] = (byte)speed;
            cmdFrame[4] = (byte)frameType;
            cmdFrame[13] = (byte)mode;
            cmdFrame[14] = 0x01;
            cmdFrame[19] = (byte)GenerateChecksum(cmdFrame, 2, 17);

            return FrameSend(sp, cmdFrame, cmdFrame.Length) < 0 ? -1 : 0;
        }

        public static int SendDataFrame(SerialPort sp, CanusbFrameType frameType, byte idLsb, byte idMsb, byte[] data, int dlc)
        {
            if (dlc < 0 || dlc > 8)
            {
                Console.Error.WriteLine("Data length code (DLC) must be between 0 and 8!");
                return -1;
            }

            byte[] dataFrame = new byte[5 + dlc];
            dataFrame[0] = 0xaa;

            byte info = (byte)(0xC0 | (frameType == CanusbFrameType.CANUSB_FRAME_EXTENDED ? 0x20 : 0x00) | dlc);
            dataFrame[1] = info;
            dataFrame[2] = idLsb;
            dataFrame[3] = idMsb;

            for (int i = 0; i < dlc; i++)
                dataFrame[4 + i] = data[i];

            dataFrame[4 + dlc] = 0x55;

            return FrameSend(sp, dataFrame, dataFrame.Length) < 0 ? -1 : 0;
        }

        public static SerialPort AdapterInit(string portName)
        {
            try
            {
                var sp = new SerialPort(portName, CANUSB_TTY_BAUD_RATE_DEFAULT, Parity.None, 8, StopBits.One)
                {
                    Handshake = Handshake.None,
                    ReadTimeout = SerialPort.InfiniteTimeout,
                    WriteTimeout = 500
                };
                sp.Open();
                return sp;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to open {portName}: {ex.Message}");
                return null;
            }
        }

        public static string FindComPortByName(string namePart)
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");

            foreach (var obj in searcher.Get())
            {
                string name = obj["Name"]?.ToString() ?? "";
                if (name.Contains(namePart, StringComparison.OrdinalIgnoreCase))
                {
                    int start = name.LastIndexOf("(COM", StringComparison.Ordinal);
                    if (start >= 0)
                    {
                        int end = name.IndexOf(')', start);
                        if (end > start)
                            return name.Substring(start + 1, end - start - 1);
                    }
                }
            }

            return null;
        }

        public static bool AdapterClose(SerialPort sp)
        {
            if (sp != null && sp.IsOpen)
            {
                sp.Close();
            }
            return true;
        }
    }
}
