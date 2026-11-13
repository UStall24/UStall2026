using System;
using System.Collections.Generic;

namespace UStallGUI.Helpers
{
    public static class LCECommunicationHelper
    {
        public static byte ConvertMotorValueToByte(float motorValue)
        {
            const byte ZERO_POINT = 0x7F;

            if (Math.Abs(motorValue) <= 1e-3)
            {
                return ZERO_POINT;
            }

            return (byte)((motorValue + 1) * 127.5);
        }

        public static float ConvertByteToMotorValue(byte byteValue)
        {
            return (float)Math.Round((byteValue / 127.5f) - 1, 2);
        }

        public static byte[] ConvertMotorValuesToBytes(float[] motorValues)
        {
            if (motorValues == null)
            {
                throw new ArgumentNullException(nameof(motorValues));
            }

            byte[] byteArray = new byte[motorValues.Length];

            for (int i = 0; i < motorValues.Length; i++)
            {
                byteArray[i] = ConvertMotorValueToByte(motorValues[i]);
            }

            return byteArray;
        }

        public static float[] ConvertBytesToMotorValues(byte[] byteArray)
        {
            if (byteArray == null)
            {
                throw new ArgumentNullException(nameof(byteArray));
            }

            float[] motorVals = new float[byteArray.Length];

            for (int i = 0; i < byteArray.Length; i++)
            {
                motorVals[i] = ConvertByteToMotorValue(byteArray[i]);
            }

            return motorVals;
        }

        public static float ConvertByteTo255Float(byte value) => value / 100f;

        public static byte Convert255FloatToByte(float value) => (byte)(value * 100);

        public static readonly Dictionary<byte, string> LCE_MessageAddresses = new Dictionary<byte, string>
        {
            { 0x00, "Error" },
            { 0x01, "Motor initialization successful" },
            { 0x02, "Receiving Control Data Successful" },
            { 0x03, "Warning: Cycle Time exceeded!" }
        };
    }

    /// <summary>
    /// IDs used when **sending** commands to the Pixhawk CAN bridge.
    /// Each value is a 16-bit unsigned integer (ushort).
    /// </summary>
    public enum SendAddressesControlUnit : ushort
    {
        Health = 40,
        InitConnection = 50,
        ArmThrusters = 60,
        DisarmThrusters = 70,
        ResetWatchdog = 80,
        ApplyManualControl = 90,
        ApplyControllerValues_Stabilize = 100,
        ApplyControllerValues_DepthHold = 110,
        UpdateCycleTime = 160,
        TestMotor = 170
    }

    /// <summary>
    /// IDs used when **receiving** responses from the Pixhawk CAN bridge.
    /// Each response ID is defined as the corresponding send ID + 1.
    /// </summary>
    public enum RecieveAddressesControlUnit : ushort
    {
        Health = 41,
        InitConnection = 51,
        ArmThrusters = 61,
        DisarmThrusters = 71,
        ResetWatchdog = 81,
        ApplyManualControl = 91,
        ApplyControllerValues_Stabilize = 101,
        ApplyControllerValues_DepthHold = 111,
        UpdateCycleTime = 161,
        TestMotor = 171
    }
}
