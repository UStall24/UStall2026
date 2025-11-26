using System;
using System.Collections.Generic;

namespace UStallGUI.Helpers
{
    public static class LCECommunicationHelper
    {
        public static byte[] ConvertMotorValuesToBytes(float[] motorValues)

        {
            if (motorValues == null)
            {
                throw new ArgumentNullException(nameof(motorValues));
            }

            byte[] byteArray = new byte[motorValues.Length];

            for (int i = 0; i < motorValues.Length; i++)
            {
                byte byteValue = 0x7F; // 127 dez -> 7F hex and 128 dez -> 80 dez the zero points for the ROV
                if (Math.Abs(motorValues[i]) > 1e-3)
                {
                    byteValue = (byte)((motorValues[i] + 1) * 127.5);
                }
                byteArray[i] = byteValue;
            }

            return byteArray;
        }

        public static float[] ConvertBytesToMotorValues(byte[] byteArray, bool roundUp = true)
        {
            if (byteArray == null)
            {
                throw new ArgumentNullException(nameof(byteArray));
            }

            float[] motorVals = new float[byteArray.Length];

            for (int i = 0; i < byteArray.Length; i++)
            {
                motorVals[i] = (float)(Math.Round((byteArray[i] / 127.5f) - 1, 2));
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

    public enum SendAddressesControlUnit : ushort
    {
        InitConnection = 0xF0,
        InitThrusters = 0xF2,
        DeinitThrusters = 0xF4,
        ResetError = 0xF6,

        ApplyManualControl = 0xE0,
        ApplyControllerValues = 0xD0,
        ApplyControllerValues_DynamicThrottle = 0xC0,

        PullDirectionValues = 0x10,
        ApplyDirectionValues = 0x21,

        PullPidValues = 0x30,
        ApplyPidValues = 0x32,

        UpdateCylceTime = 0xF8
    }

    public enum RecieveAddressesControlUnit : ushort
    {
        // Connection / init
        InitConnection_Response = 0xF1,

        InitThrusters_Response = 0xF3,
        DeinitThrusters_Response = 0xF5,
        ResetError_Response = 0xF7,

        // Cycle time
        UpdateCycleTime_Response = 0xF9,

        // Control / manual
        ApplyManualControl_Response = 0xE1,  // only if your firmware actually sends one

        ApplyControllerValues_Response = 0xD1,
        ApplyControllerValues_DynamicThrottle_Response = 0xC1,

        // Direction values
        PullDirectionValues_Response = 0x11,

        ApplyDirectionValues_Response = 0x22,

        // Gyro / PID
        PullPidValues_Response = 0x31,

        ApplyPidValues_Response = 0x33
    }
}