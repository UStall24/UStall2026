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

    /// <summary>
    /// IDs used when **sending** commands to the control unit.
    /// Each value is a 16‑bit unsigned integer (ushort).
    /// </summary>
    public enum SendAddressesControlUnit : ushort
    {
        Health = 40,
        // Connection / initialization
        InitConnection = 50,                     // Establish initial link
        InitThrusters = 60,                      // Power‑up thrusters
        DeinitThrusters = 70,                    // Power‑down thrusters
        ResetError = 80,                         // Clear error flags

        // Manual / controller input
        ApplyManualControl = 90,                 // Direct manual command
        ApplyControllerValues = 100,            // Standard controller setpoint
        ApplyControllerValues_DynamicThrottle = 110, // Controller with dynamic throttle

        // Direction data
        PullDirectionValues = 120,               // Request current direction data
        ApplyDirectionValues = 130,              // Send new direction setpoints

        // PID / gyro data
        PullPidValues = 140,                     // Request current PID parameters
        ApplyPidValues = 150,                    // Send updated PID parameters

        // Timing
        UpdateCylceTime = 160                    // Update the control loop cycle time
    }

    /// <summary>
    /// IDs used when **receiving** responses from the control unit.
    /// Each response ID is defined as the corresponding send ID + 1,
    /// ensuring a simple, predictable mapping.
    /// </summary>
    public enum RecieveAddressesControlUnit : ushort
    {
        Health_response = SendAddressesControlUnit.Health + 1,
        // Connection / init responses
        InitConnection_Response = SendAddressesControlUnit.InitConnection + 1,          // 51
        InitThrusters_Response = SendAddressesControlUnit.InitThrusters + 1,           // 61
        DeinitThrusters_Response = SendAddressesControlUnit.DeinitThrusters + 1,         // 71
        ResetError_Response = SendAddressesControlUnit.ResetError + 1,              // 81

        // Cycle‑time response
        UpdateCycleTime_Response = SendAddressesControlUnit.UpdateCylceTime + 1,        // 161

        // Control / manual responses
        ApplyManualControl_Response = SendAddressesControlUnit.ApplyManualControl + 1,      // 91
        ApplyControllerValues_Response = SendAddressesControlUnit.ApplyControllerValues + 1,   // 101
        ApplyControllerValues_DynamicThrottle_Response = SendAddressesControlUnit.ApplyControllerValues_DynamicThrottle + 1, // 111

        // Direction values responses
        PullDirectionValues_Response = SendAddressesControlUnit.PullDirectionValues + 1,    // 121
        ApplyDirectionValues_Response = SendAddressesControlUnit.ApplyDirectionValues + 1,   // 131

        // Gyro / PID responses
        PullPidValues_Response = SendAddressesControlUnit.PullPidValues + 1,          // 141
        ApplyPidValues_Response = SendAddressesControlUnit.ApplyPidValues + 1          // 151
    }

}