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
    /// IDs used when **sending** commands to the control unit.
    /// Each value is a 16‑bit unsigned integer (ushort).
    /// </summary>
    public enum SendAddressesControlUnit : ushort
    {
        Health = 40,
        InitConnection = 50,
        InitThrusters = 60,
        DeinitThrusters = 70,
        ResetError = 80,
        ApplyManualControl = 90,
        ApplyControllerValues = 100,
        ApplyControllerValues_Dyn = 110,

        // --- NEW: per DoF responses ---

        Set_FB = 120,
        Set_LR = 121,
        Set_UD = 122,
        Set_YAW = 123,
        Set_ROLL = 124,
        Set_PITCH = 125,

        Get_FB = 130,
        Get_LR = 131,
        Get_UD = 132,
        Get_YAW = 133,
        Get_ROLL = 134,
        Get_PITCH = 135,

        PullPidValues = 140,
        ApplyPidValues = 150,
        UpdateCycleTime = 160,

        TestMotor = 170
    }

    /// <summary>
    /// IDs used when **receiving** responses from the control unit.
    /// Each response ID is defined as the corresponding send ID + 1,
    /// ensuring a simple, predictable mapping.
    /// </summary>
    public enum RecieveAddressesControlUnit : ushort
    {
        Health = 41,
        InitConnection = 51,
        InitThrusters = 61,
        DeinitThrusters = 71,
        ResetError = 81,
        ApplyManualControl = 91,
        ApplyControllerValues = 101,
        ApplyControllerValues_Dyn = 111,

        // --- NEW: per DoF direction values ---

        Set_FB = 121,      // forwards_backwards
        Set_LR = 122,      // left_right
        Set_UD = 123,      // up_down
        Set_YAW = 124,
        Set_ROLL = 125,
        Set_PITCH = 126,
        All_DoF_Saved = 127,

        // Pull values (optional, same structure)

        Get_FB = 131,
        Get_LR = 132,
        Get_UD = 133,
        Get_YAW = 134,
        Get_ROLL = 135,
        Get_PITCH = 136,

        PullPidValues = 141,
        ApplyPidValues = 151,
        UpdateCycleTime = 161,

        TestMotor = 171
    }
}