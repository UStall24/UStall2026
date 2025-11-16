using System.IO.Ports;
using System.Management;

namespace usb_can_test
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

    public class UsbCAN
    {
        private const float CANUSB_INJECT_SLEEP_GAP_DEFAULT = 200f; // ms
        private const int CANUSB_TTY_BAUD_RATE_DEFAULT = 2000000;

        private const int CANUSB_INJECT_PAYLOAD_MODE_RANDOM = 0;
        private const int CANUSB_INJECT_PAYLOAD_MODE_INCREMENTAL = 1;
        private const int CANUSB_INJECT_PAYLOAD_MODE_FIXED = 2;

        private static volatile bool programRunning = true;
        private static int terminateAfter = 0;
        private static readonly int injectPayloadMode = CANUSB_INJECT_PAYLOAD_MODE_FIXED;
        private static readonly float injectSleepGap = CANUSB_INJECT_SLEEP_GAP_DEFAULT;
        private static readonly int printTraffic = 0;
        private static readonly Random rng = new Random();

        // ---- Helpers translated from C ----

        public static CanusbSpeed CanusbIntToSpeed(int speed)
        {
            return speed switch
            {
                1000000 => CanusbSpeed.CANUSB_SPEED_1000000,
                800000 => CanusbSpeed.CANUSB_SPEED_800000,
                500000 => CanusbSpeed.CANUSB_SPEED_500000,
                400000 => CanusbSpeed.CANUSB_SPEED_400000,
                250000 => CanusbSpeed.CANUSB_SPEED_250000,
                200000 => CanusbSpeed.CANUSB_SPEED_200000,
                125000 => CanusbSpeed.CANUSB_SPEED_125000,
                100000 => CanusbSpeed.CANUSB_SPEED_100000,
                50000 => CanusbSpeed.CANUSB_SPEED_50000,
                20000 => CanusbSpeed.CANUSB_SPEED_20000,
                10000 => CanusbSpeed.CANUSB_SPEED_10000,
                5000 => CanusbSpeed.CANUSB_SPEED_5000,
                _ => 0,
            };
        }

        public static int GenerateChecksum(byte[] data, int offset, int length)
        {
            int checksum = 0;
            for (int i = 0; i < length; i++)
                checksum += data[offset + i];
            return checksum & 0xff;
        }

        public static bool FrameIsComplete(byte[] frame, int frameLen)
        {
            if (frameLen > 0)
            {
                if (frame[0] != 0xaa)
                {
                    // Need to sync on 0xaa at start
                    return true;
                }
            }

            if (frameLen < 2)
                return false;

            if (frame[1] == 0x55)
            {
                // Command frame, always 20 bytes
                return frameLen >= 20;
            }
            else if ((frame[1] >> 4) == 0x0c)
            {
                // Data frame, payload + 5 bytes
                int needed = (frame[1] & 0x0f) + 5;
                return frameLen >= needed;
            }

            // Unhandled frame type
            return true;
        }

        public static int FrameSend(SerialPort sp, byte[] frame, int frameLen)
        {
            if (printTraffic > 0)
            {
                Console.Write(">>> ");
                for (int i = 0; i < frameLen; i++)
                    Console.Write($"{frame[i]:X2} ");

                if (printTraffic > 1)
                {
                    Console.Write("    '");
                    for (int i = 4; i < frameLen - 1; i++)
                    {
                        char ch = (char)frame[i];
                        Console.Write(char.IsLetterOrDigit(ch) ? ch : '.');
                    }
                    Console.Write("'");
                }
                Console.WriteLine();
            }

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

        public static int FrameRecv(SerialPort sp, byte[] frame, int frameLenMax)
        {
            int frameLen = 0;
            int b = 0;
            while (programRunning)
            {
                try
                {
                    b = sp.ReadByte();
                }
                catch (TimeoutException)
                {
                    return -1;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("Read failed: " + ex.Message);
                    return -1;
                }
                if (b >= 0)
                {
                    byte by = (byte)b;

                    if (printTraffic > 0)
                        Console.Error.Write($"{by:X2} ");

                    if (frameLen == frameLenMax)
                    {
                        Console.Error.WriteLine("frame_recv() failed: Overflow");
                        return -1;
                    }

                    frame[frameLen++] = by;

                    if (FrameIsComplete(frame, frameLen))
                        break;
                }

                Thread.Sleep(1);
            }

            if (printTraffic > 0)
                Console.Error.WriteLine();

            // Command frames: verify checksum
            if (frameLen == 20 && frame[0] == 0xaa && frame[1] == 0x55)
            {
                int checksum = GenerateChecksum(frame, 2, 17);
                if (checksum != frame[frameLen - 1])
                {
                    Console.Error.WriteLine("frame_recv() failed: Checksum incorrect");
                    return -1;
                }
            }

            return frameLen;
        }

        public static int CommandSettings(SerialPort sp, CanusbSpeed speed, CanusbMode mode, CanusbFrameType frameType)
        {
            byte[] cmdFrame = new byte[20];
            int len = 0;

            cmdFrame[len++] = 0xaa;
            cmdFrame[len++] = 0x55;
            cmdFrame[len++] = 0x12;
            cmdFrame[len++] = (byte)speed;
            cmdFrame[len++] = (byte)frameType;
            cmdFrame[len++] = 0; // Filter ID not handled
            cmdFrame[len++] = 0;
            cmdFrame[len++] = 0;
            cmdFrame[len++] = 0;
            cmdFrame[len++] = 0; // Mask ID not handled
            cmdFrame[len++] = 0;
            cmdFrame[len++] = 0;
            cmdFrame[len++] = 0;
            cmdFrame[len++] = (byte)mode;
            cmdFrame[len++] = 0x01;
            cmdFrame[len++] = 0;
            cmdFrame[len++] = 0;
            cmdFrame[len++] = 0;
            cmdFrame[len++] = 0;
            cmdFrame[len++] = (byte)GenerateChecksum(cmdFrame, 2, 17);

            if (FrameSend(sp, cmdFrame, len) < 0)
                return -1;
            return 0;
        }

        public static int SendDataFrame(SerialPort sp, CanusbFrameType frameType, byte idLsb, byte idMsb, byte[] data, int dlc)
        {
            const int MAX_FRAME_SIZE = 13;
            if (dlc < 0 || dlc > 8)
            {
                Console.Error.WriteLine("Data length code (DLC) must be between 0 and 8!");
                return -1;
            }

            byte[] dataFrame = new byte[MAX_FRAME_SIZE];
            int len = 0;

            // Byte 0: packet start
            dataFrame[len++] = 0xaa;

            // Byte 1: info
            byte info = 0x00;
            info |= 0xC0; // bits 7,6 = 1
            if (frameType == CanusbFrameType.CANUSB_FRAME_STANDARD)
                info &= 0xDF; // clear extended bit
            else
                info |= 0x20; // set extended bit

            info &= 0xEF; // data frame, not remote
            info |= (byte)dlc; // DLC
            dataFrame[len++] = info;

            // Bytes 2–3: ID
            dataFrame[len++] = idLsb;
            dataFrame[len++] = idMsb;

            // Data bytes
            for (int i = 0; i < dlc; i++)
                dataFrame[len++] = data[i];

            // End byte
            dataFrame[len++] = 0x55;

            if (FrameSend(sp, dataFrame, len) < 0)
            {
                Console.Error.WriteLine("Unable to send frame!");
                return -1;
            }

            return 0;
        }

        public static int HexValue(int c)
        {
            if (c >= '0' && c <= '9')
                return c - '0';
            if (c >= 'A' && c <= 'F')
                return (c - 'A') + 10;
            if (c >= 'a' && c <= 'f')
                return (c - 'a') + 10;
            return -1;
        }

        public static int ConvertFromHex(string hex, byte[] bin, int maxLen)
        {
            int n1 = 0, n2 = 0;
            int high = -1;

            while (n1 < hex.Length)
            {
                int hv = HexValue(hex[n1]);
                if (hv >= 0)
                {
                    if (high == -1)
                    {
                        high = hex[n1];
                    }
                    else
                    {
                        bin[n2] = (byte)(HexValue(high) * 16 + hv);
                        n2++;
                        if (n2 >= maxLen)
                        {
                            Console.WriteLine("hex string truncated to {0} bytes", n2);
                            break;
                        }
                        high = -1;
                    }
                }
                n1++;
            }

            return n2;
        }

        public static int InjectDataFrame(SerialPort sp, string hexId, string hexData)
        {
            byte[] binaryData = new byte[8];
            byte idLsb = 0, idMsb = 0;

            int dataLen = ConvertFromHex(hexData, binaryData, binaryData.Length);
            if (dataLen == 0)
            {
                Console.Error.WriteLine("Unable to convert data from hex to binary!");
                return -1;
            }

            switch (hexId.Length)
            {
                case 1:
                    idLsb = (byte)HexValue(hexId[0]);
                    break;

                case 2:
                    idLsb = (byte)(HexValue(hexId[0]) * 16 + HexValue(hexId[1]));
                    break;

                case 3:
                    idMsb = (byte)HexValue(hexId[0]);
                    idLsb = (byte)(HexValue(hexId[1]) * 16 + HexValue(hexId[2]));
                    break;

                default:
                    Console.Error.WriteLine("Unable to convert ID from hex to binary!");
                    return -1;
            }

            int error = 0;

            while (programRunning && error == 0)
            {
                if (injectSleepGap > 0)
                    Thread.Sleep((int)injectSleepGap);

                if (terminateAfter != 0 && (--terminateAfter == 0))
                    programRunning = false;

                if (injectPayloadMode == CANUSB_INJECT_PAYLOAD_MODE_RANDOM)
                {
                    for (int i = 0; i < dataLen; i++)
                        binaryData[i] = (byte)rng.Next(0, 256);
                }
                else if (injectPayloadMode == CANUSB_INJECT_PAYLOAD_MODE_INCREMENTAL)
                {
                    for (int i = 0; i < dataLen; i++)
                        unchecked { binaryData[i]++; }
                }

                error = SendDataFrame(sp, CanusbFrameType.CANUSB_FRAME_STANDARD, idLsb, idMsb, binaryData, dataLen);
            }

            return error;
        }

        public static void DumpDataFrames(SerialPort sp)
        {
            byte[] frame = new byte[32];

            while (programRunning)
            {
                int frameLen = FrameRecv(sp, frame, frame.Length);

                if (!programRunning)
                    break;

                // timestamp: similar idea, but we'll just use now
                var ts = DateTime.UtcNow;
                long unixSec = new DateTimeOffset(ts).ToUnixTimeSeconds();
                long unixUsec = (ts.Ticks % TimeSpan.TicksPerSecond) / 10;
                Console.Write("{0}.{1:D6} ", unixSec, unixUsec);

                if (frameLen == -1)
                {
                    Console.WriteLine("Frame receive error!");
                }
                else
                {
                    if (frameLen >= 6 &&
                        frame[0] == 0xaa &&
                        ((frame[1] >> 4) == 0x0c))
                    {
                        Console.Write("Frame ID: {0:X2}{1:X2}, Data: ", frame[3], frame[2]);
                        for (int i = frameLen - 2; i > 3; i--)
                        {
                            Console.Write("{0:X2} ", frame[i]);
                        }
                        Console.WriteLine();
                    }
                    else
                    {
                        Console.Write("Unknown: ");
                        for (int i = 0; i < frameLen; i++)
                        {
                            Console.Write("{0:X2} ", frame[i]);
                        }
                        Console.WriteLine();
                    }
                }

                if (terminateAfter != 0 && (--terminateAfter == 0))
                    programRunning = false;
            }
        }

        public static SerialPort AdapterInit(string portName, int baudrate)
        {
            try
            {
                var sp = new SerialPort(portName, baudrate, Parity.None, 8, StopBits.One)
                {
                    Handshake = Handshake.None,
                    ReadTimeout = SerialPort.InfiniteTimeout,
                    WriteTimeout = SerialPort.InfiniteTimeout
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
            var searcher = new ManagementObjectSearcher(
                "SELECT * FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");

            foreach (var obj in searcher.Get())
            {
                string name = obj["Name"]?.ToString() ?? "";
                // Name looks like: "USB-SERIAL CH340 (COM3)"

                if (name.Contains(namePart, StringComparison.OrdinalIgnoreCase))
                {
                    int start = name.LastIndexOf("(COM");
                    if (start >= 0)
                    {
                        int end = name.IndexOf(")", start);
                        if (end > start)
                        {
                            string comPort = name.Substring(start + 1, end - start - 1);
                            return comPort; // returns "COM3"
                        }
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