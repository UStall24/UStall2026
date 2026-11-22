namespace UStallGUI.Helpers
{
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
        ApplyControllerValues_Manual = 90,
        ApplyControllerValues_Stabilize = 100,
        ApplyControllerValues_DepthHold = 110,
        SetSpeedLimit = 120,
        MotorConfig = 130,
        AxisGains = 140,
        PidParam = 150,
        UpdateCycleTime = 160,
        TestMotor = 170
    }

    /// <summary>
    /// IDs used when **receiving** responses from the Pixhawk CAN bridge.
    /// Each response ID is defined as the corresponding send ID + 1.
    /// The ControllerValues don't have a response.
    /// </summary>
    public enum RecieveAddressesControlUnit : ushort
    {
        Health = 41,
        InitConnection = 51,
        ArmThrusters = 61,
        DisarmThrusters = 71,
        ResetWatchdog = 81,
        SetSpeedLimit = 121,
        MotorConfig = 131,
        AxisGains = 141,
        PidParam = 151,
        UpdateCycleTime = 161,
        TestMotor = 171
    }
}
