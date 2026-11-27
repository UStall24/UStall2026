using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.Command;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using UStallGUI.Helpers;

namespace UStallGUI.ViewModel
{
    public class ThrusterConfigItem : ObservableObject
    {
        public int MotorIndex { get; }
        public string Label { get; }
        public string Description { get; }

        private int _selectedPortIndex;
        public int SelectedPortIndex
        {
            get => _selectedPortIndex;
            set => Set(ref _selectedPortIndex, Math.Clamp(value, 0, 5));
        }

        private bool _isReversed;
        public bool IsReversed
        {
            get => _isReversed;
            set
            {
                if (Set(ref _isReversed, value))
                    RaisePropertyChanged(nameof(DirectionLabel));
            }
        }

        public string DirectionLabel => IsReversed ? "REV (−)" : "FWD (+)";

        public ThrusterConfigItem(int motorIndex, string label, string description)
        {
            MotorIndex = motorIndex;
            Label = label;
            Description = description;
            _selectedPortIndex = motorIndex;
        }
    }

    public class ControlParameterHandlerViewModel : ObservableObject
    {
        private readonly UsbCANWrapper can;

        public string[] OutputPortOptions { get; } = { "OUT1", "OUT2", "OUT3", "OUT4", "OUT5", "OUT6" };

        public ObservableCollection<ThrusterConfigItem> Thrusters { get; } = new()
        {
            new ThrusterConfigItem(0, "M1", "Vertical - Front Left (Roll +0.866, Pitch +0.5, Heave +)"),
            new ThrusterConfigItem(1, "M2", "Horizontal - Rear Left (Fwd +, Yaw +, Lat -0.5)"),
            new ThrusterConfigItem(2, "M3", "Vertical - Rear Center (Pitch -1.0, Heave +)"),
            new ThrusterConfigItem(3, "M4", "Horizontal - Rear Right (Fwd +, Yaw -, Lat +0.5)"),
            new ThrusterConfigItem(4, "M5", "Vertical - Front Right (Roll -0.866, Pitch +0.5, Heave +)"),
            new ThrusterConfigItem(5, "M6", "Horizontal - Front Center (Lat +, Yaw +)"),
        };

        // --- Per-Axis Gains (0..100%) ---
        private int _gainForward = 100;
        public int GainForward { get => _gainForward; set => Set(ref _gainForward, Math.Clamp(value, 0, 100)); }

        private int _gainLateral = 100;
        public int GainLateral { get => _gainLateral; set => Set(ref _gainLateral, Math.Clamp(value, 0, 100)); }

        private int _gainHeave = 100;
        public int GainHeave { get => _gainHeave; set => Set(ref _gainHeave, Math.Clamp(value, 0, 100)); }

        private int _gainYaw = 100;
        public int GainYaw { get => _gainYaw; set => Set(ref _gainYaw, Math.Clamp(value, 0, 100)); }

        private int _gainPitch = 100;
        public int GainPitch { get => _gainPitch; set => Set(ref _gainPitch, Math.Clamp(value, 0, 100)); }

        private int _gainRoll = 100;
        public int GainRoll { get => _gainRoll; set => Set(ref _gainRoll, Math.Clamp(value, 0, 100)); }

        // --- Busy Flags ---
        private bool _testMotor_Enabled = true;
        public bool TestMotor_Enabled
        {
            get => _testMotor_Enabled;
            set
            {
                if (Set(ref _testMotor_Enabled, value))
                    TestMotor.RaiseCanExecuteChanged();
            }
        }

        private bool _configSync_Enabled = true;
        public bool ConfigSync_Enabled
        {
            get => _configSync_Enabled;
            set
            {
                if (Set(ref _configSync_Enabled, value))
                {
                    PullFromPixhawk_Command.RaiseCanExecuteChanged();
                    ApplyToPixhawk_Command.RaiseCanExecuteChanged();
                }
            }
        }

        // --- Test Motor PWM ---
        private int _testThrust = 1560;
        public int TestThrust
        {
            get => _testThrust;
            set => Set(ref _testThrust, Math.Clamp(value, 1100, 1900));
        }

        // --- Commands ---
        public RelayCommand<object> TestMotor { get; }
        public RelayCommand PullFromPixhawk_Command { get; }
        public RelayCommand ApplyToPixhawk_Command { get; }
        public RelayCommand ResetDefaults_Command { get; }

        public ControlParameterHandlerViewModel(UsbCANWrapper can)
        {
            this.can = can;

            TestMotor = new RelayCommand<object>(p => _ = TestMotorCommandAsync(p), _ => TestMotor_Enabled);
            PullFromPixhawk_Command = new RelayCommand(() => _ = PullFromPixhawkAsync(), () => ConfigSync_Enabled);
            ApplyToPixhawk_Command = new RelayCommand(() => _ = ApplyToPixhawkAsync(), () => ConfigSync_Enabled);
            ResetDefaults_Command = new RelayCommand(ResetDefaults);
        }

        public static byte[] PwmArrayToByteArray(int[] pwmValues)
        {
            byte[] result = new byte[pwmValues.Length];
            for (int i = 0; i < pwmValues.Length; i++)
            {
                int pwm = Math.Clamp(pwmValues[i], 1100, 1900);
                result[i] = (byte)Math.Round(((pwm - 1100) / 800.0) * 255);
            }
            return result;
        }

        public async Task TestMotorCommandAsync(object motorIndexStr)
        {
            if (motorIndexStr == null || !int.TryParse(motorIndexStr.ToString(), out int motorIndex))
                return;
            if (motorIndex < 0 || motorIndex >= 6)
                return;

            if (can == null || !can.IsOpen)
            {
                MainWindowViewModel.Instance.LogToControlBox("Cannot test motor: USB-CAN adapter not connected.");
                return;
            }

            int speedLimit = Math.Clamp(ControllerHandlerViewModel.MaxThrusterSpeedPercent, 0, 100);
            double factor = speedLimit / 100.0;
            int effectivePwm = 1500 + (int)Math.Round((TestThrust - 1500) * factor);

            // Pixhawk can_bridge.lua scales bench motor test PWM by global max_speed_percent;
            // send raw TestThrust here so it is not double-scaled, while checking effectivePwm for deadband.
            int[] values = { 1500, 1500, 1500, 1500, 1500, 1500 };
            values[motorIndex] = TestThrust;

            byte[] motorBytes = PwmArrayToByteArray(values);
            if (TestThrust != 1500 && (speedLimit == 0 || Math.Abs(effectivePwm - 1500) < 10))
            {
                MainWindowViewModel.Instance.LogToControlBox(
                    $"Motor M{motorIndex + 1} test skipped: scaled PWM ({effectivePwm} µs at {speedLimit}% speed limit) falls inside neutral deadband (1500 µs). Increase Speed Limit or Bench Test PWM.");
                return;
            }

            TestMotor_Enabled = false;
            try
            {
                byte duration = 150; // 1500 ms
                byte[] payload = new byte[motorBytes.Length + 1];
                Array.Copy(motorBytes, payload, motorBytes.Length);
                payload[^1] = duration;

                MainWindowViewModel.Instance.LogToControlBox($"Testing Motor M{motorIndex + 1} at {effectivePwm} µs (Speed Limit: {speedLimit}%)...");
                var resp = await can.SendAndWaitForResponseAsync(
                    (ushort)SendAddressesControlUnit.TestMotor,
                    (ushort)RecieveAddressesControlUnit.TestMotor,
                    payload,
                    duration * 10 + 500);

                if (resp.Length > 0 && resp[0] == 1)
                    MainWindowViewModel.Instance.LogToControlBox($"Motor M{motorIndex + 1} test completed.");
                else
                    MainWindowViewModel.Instance.LogToControlBox($"Motor M{motorIndex + 1} test rejected or timed out (check Pixhawk status).");
            }
            catch (Exception ex)
            {
                MainWindowViewModel.Instance?.LogToControlBox($"Motor M{motorIndex + 1} test error: {ex.Message}");
            }
            finally
            {
                TestMotor_Enabled = true;
            }
        }

        public async Task PullFromPixhawkAsync()
        {
            if (can == null || !can.IsOpen)
            {
                MainWindowViewModel.Instance.LogToControlBox("Cannot pull thruster config: USB-CAN adapter not connected.");
                return;
            }

            ConfigSync_Enabled = false;
            try
            {
                var motorResp = await can.SendAndWaitForResponseAsync(
                    (ushort)SendAddressesControlUnit.MotorConfig,
                    (ushort)RecieveAddressesControlUnit.MotorConfig,
                    Array.Empty<byte>(),
                    500);

                bool motorOk = motorResp.Length >= 7;
                if (motorOk)
                {
                    byte dirMask = motorResp[0];
                    for (int m = 0; m < 6; m++)
                    {
                        Thrusters[m].IsReversed = ((dirMask >> m) & 1) == 1;
                        int port = motorResp[m + 1];
                        if (port >= 1 && port <= 6)
                            Thrusters[m].SelectedPortIndex = port - 1;
                    }
                }

                var gainResp = await can.SendAndWaitForResponseAsync(
                    (ushort)SendAddressesControlUnit.AxisGains,
                    (ushort)RecieveAddressesControlUnit.AxisGains,
                    Array.Empty<byte>(),
                    500);

                bool gainOk = gainResp.Length >= 6;
                if (gainOk)
                {
                    GainForward = gainResp[0];
                    GainLateral = gainResp[1];
                    GainHeave = gainResp[2];
                    GainYaw = gainResp[3];
                    GainPitch = gainResp[4];
                    GainRoll = gainResp[5];
                }

                var speedResp = await can.SendAndWaitForResponseAsync(
                    (ushort)SendAddressesControlUnit.SetSpeedLimit,
                    (ushort)RecieveAddressesControlUnit.SetSpeedLimit,
                    Array.Empty<byte>(),
                    500);

                if (speedResp.Length > 0)
                {
                    SetupHandlerViewModel.Instance?.SyncSpeedLimitFromPixhawk(speedResp[0]);
                }

                if (motorOk && gainOk)
                    MainWindowViewModel.Instance.LogToControlBox("Pulled thruster directions, ESC ports, axis gains, and speed limit from Pixhawk.");
                else
                    MainWindowViewModel.Instance.LogToControlBox("Pull from Pixhawk incomplete or timed out.");
            }
            catch (Exception ex)
            {
                MainWindowViewModel.Instance?.LogToControlBox($"Pull thruster config error: {ex.Message}");
            }
            finally
            {
                ConfigSync_Enabled = true;
            }
        }

        public async Task ApplyToPixhawkAsync()
        {
            if (can == null || !can.IsOpen)
            {
                MainWindowViewModel.Instance.LogToControlBox("Cannot apply thruster config: USB-CAN adapter not connected.");
                return;
            }

            // Ensure all 6 ESC ports are unique before sending
            var usedPorts = new HashSet<int>();
            for (int m = 0; m < 6; m++)
            {
                int port = Thrusters[m].SelectedPortIndex + 1;
                if (!usedPorts.Add(port))
                {
                    MainWindowViewModel.Instance.LogToControlBox($"Duplicate ESC port OUT{port}! Each thruster (M1–M6) must use a unique port.");
                    return;
                }
            }

            ConfigSync_Enabled = false;
            try
            {
                byte dirMask = 0;
                byte[] motorPayload = new byte[7];
                for (int m = 0; m < 6; m++)
                {
                    if (Thrusters[m].IsReversed)
                        dirMask |= (byte)(1 << m);
                    motorPayload[m + 1] = (byte)(Thrusters[m].SelectedPortIndex + 1);
                }
                motorPayload[0] = dirMask;

                var motorResp = await can.SendAndWaitForResponseAsync(
                    (ushort)SendAddressesControlUnit.MotorConfig,
                    (ushort)RecieveAddressesControlUnit.MotorConfig,
                    motorPayload,
                    600);

                byte[] gainPayload =
                {
                    (byte)GainForward,
                    (byte)GainLateral,
                    (byte)GainHeave,
                    (byte)GainYaw,
                    (byte)GainPitch,
                    (byte)GainRoll
                };

                var gainResp = await can.SendAndWaitForResponseAsync(
                    (ushort)SendAddressesControlUnit.AxisGains,
                    (ushort)RecieveAddressesControlUnit.AxisGains,
                    gainPayload,
                    500);

                bool motorMatched = motorResp.Length >= 7;
                if (motorMatched)
                {
                    for (int i = 0; i < 7; i++)
                    {
                        if (motorResp[i] != motorPayload[i])
                        {
                            motorMatched = false;
                            break;
                        }
                    }

                    if (!motorMatched)
                    {
                        // Sync UI back to actual Pixhawk motor config (e.g. write rejected while armed)
                        byte actualMask = motorResp[0];
                        for (int m = 0; m < 6; m++)
                        {
                            Thrusters[m].IsReversed = ((actualMask >> m) & 1) == 1;
                            int port = motorResp[m + 1];
                            if (port >= 1 && port <= 6)
                                Thrusters[m].SelectedPortIndex = port - 1;
                        }
                    }
                }

                if (motorMatched && gainResp.Length >= 6)
                    MainWindowViewModel.Instance.LogToControlBox("Applied thruster directions, ESC ports, and axis gains to Pixhawk (saved to params).");
                else if (motorResp.Length >= 7 && !motorMatched && gainResp.Length >= 6)
                    MainWindowViewModel.Instance.LogToControlBox("Axis gains saved, but Pixhawk rejected motor direction/port changes (disarm thrusters first).");
                else
                    MainWindowViewModel.Instance.LogToControlBox("Apply to Pixhawk incomplete or timed out.");
            }
            catch (Exception ex)
            {
                MainWindowViewModel.Instance?.LogToControlBox($"Apply thruster config error: {ex.Message}");
            }
            finally
            {
                ConfigSync_Enabled = true;
            }
        }

        private void ResetDefaults()
        {
            for (int m = 0; m < 6; m++)
            {
                Thrusters[m].SelectedPortIndex = m;
                Thrusters[m].IsReversed = false;
            }
            GainForward = 100;
            GainLateral = 100;
            GainHeave = 100;
            GainYaw = 100;
            GainPitch = 100;
            GainRoll = 100;
            MainWindowViewModel.Instance.LogToControlBox("Thruster config reset to defaults (click 'Apply to Pixhawk' to save).");
        }
    }
}
