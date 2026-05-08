using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.Command;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using UStallGUI.Helpers;
using UStallGUI.Model;

namespace UStallGUI.ViewModel
{
    public class ControlParameterHandlerViewModel : ObservableObject
    {
        // --- Properties & Fields ---
        public MotorValues _currentShownMotorValues;

        public MotorValues CurrentShownMotorValues
        {
            get => _currentShownMotorValues;
            set => Set(ref _currentShownMotorValues, value);
        }

        public DirectionValues _loadedDirectionValues;

        public DirectionValues LoadedDirectionValue
        {
            get => _loadedDirectionValues;
            set
            {
                Set(ref _loadedDirectionValues, value);
                DirectionSelectedIndex = _direction_selectedIndex;
            }
        }

        private bool _pullFromROV_Enabled = true;

        public bool PullFromROV_Enabled
        {
            get => _pullFromROV_Enabled;
            set
            {
                if (Set(ref _pullFromROV_Enabled, value))
                    PullFromROV_Command.RaiseCanExecuteChanged();
            }
        }

        private bool _applyToROV_Enabled = true;

        public bool ApplyToROV_Enabled
        {
            get => _applyToROV_Enabled;
            set
            {
                if (Set(ref _applyToROV_Enabled, value))
                    ApplyToROV_Command.RaiseCanExecuteChanged();
            }
        }

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

        private int _direction_selectedIndex = 0;

        public int DirectionSelectedIndex
        {
            get => _direction_selectedIndex;
            set
            {
                _direction_selectedIndex = value;
                if (LoadedDirectionValue != null)
                {
                    switch (value)
                    {
                        case 0: CurrentShownMotorValues = LoadedDirectionValue.Get(DOF.ForwardsBackwards); break;
                        case 1: CurrentShownMotorValues = LoadedDirectionValue.Get(DOF.LeftRight); break;
                        case 2: CurrentShownMotorValues = LoadedDirectionValue.Get(DOF.UpDown); break;
                        case 3: CurrentShownMotorValues = LoadedDirectionValue.Get(DOF.Yaw); break;
                        case 4: CurrentShownMotorValues = LoadedDirectionValue.Get(DOF.Roll); break;
                        case 5: CurrentShownMotorValues = LoadedDirectionValue.Get(DOF.Pitch); break;
                    }
                }
            }
        }

        public RelayCommand PullFromROV_Command { get; }
        public RelayCommand ApplyToROV_Command { get; }
        public RelayCommand LoadFromPC_Command { get; }
        public RelayCommand SaveToPC_Command { get; }
        public RelayCommand<object> TestMotor { get; }

        private readonly UsbCANWrapper can;
        private const int PullTimeoutMs = 500;
        private const int MaxRetries = 3;
        private const ushort AllDofSavedRx = (ushort)RecieveAddressesControlUnit.All_DoF_Saved;
        private readonly Dictionary<DOF, float[]> _tempDirectionBuffer = new();

        // --- Constructor ---
        public ControlParameterHandlerViewModel(UsbCANWrapper can)
        {
            this.can = can;
            LoadedDirectionValue = DirectionValues.GetDefaultDirectionValues();
            CurrentShownMotorValues = LoadedDirectionValue.Get(DOF.ForwardsBackwards);

            LoadFromPC_Command = new RelayCommand(LoadDirectionValuesFromPC);
            SaveToPC_Command = new RelayCommand(SaveDirectionValuesToPC);

            // Commands now respect the Enabled boolean properties
            PullFromROV_Command = new RelayCommand(PullDirectionValuesFromROV, () => PullFromROV_Enabled);
            ApplyToROV_Command = new RelayCommand(ApplyDirectionValuesToLCE, () => ApplyToROV_Enabled);
            TestMotor = new RelayCommand<object>(TestMotorCommand, _ => TestMotor_Enabled);

            can.RegisterCallback((ushort)RecieveAddressesControlUnit.TestMotor, TestMotorCommandCallback);
        }

        // --- PC File Logic ---
        private void LoadDirectionValuesFromPC()
        {
            OpenFileDialog openFileDialog = new OpenFileDialog { Filter = "JSON files (*.json)|*.json", Title = "Select a JSON file" };
            if (openFileDialog.ShowDialog() == true)
            {
                DirectionValues directionValues = ConfigLoader.LoadControllerParameters(openFileDialog.FileName);
                if (directionValues != null) LoadedDirectionValue = directionValues;
                else MessageBox.Show("That is not a valid DirectionValue file");
            }
        }

        private void SaveDirectionValuesToPC()
        {
            SaveFileDialog saveFileDialog = new SaveFileDialog { Filter = "JSON files (*.json)|*.json", Title = "Save DirectionValue File", FileName = "directionValues.json" };
            if (saveFileDialog.ShowDialog() == true)
            {
                bool successful = ConfigLoader.UpdateConfigurationFile(LoadedDirectionValue, saveFileDialog.FileName);
                MessageBox.Show(successful ? "File saved successfully" : "Saving Failed");
            }
        }

        // --- ROV Pull Logic ---
        public async void PullDirectionValuesFromROV()
        {
            if (can == null || !can.IsOpen) { MainWindowViewModel.Instance.LogToControlBox("CAN not open"); return; }
            bool originalSendControllerToggle = ControllerHandlerViewModel.Instance.SendControllerValues;
            ControllerHandlerViewModel.Instance.SendControllerValues = false; // Ensure controller values are not sent during pull
            PullFromROV_Enabled = false; // Disable UI
            _tempDirectionBuffer.Clear();

            try
            {
                var requests = new (DOF dof, ushort sendId, ushort recvId)[]
                {
                    (DOF.ForwardsBackwards, (ushort)SendAddressesControlUnit.Get_FB, (ushort)RecieveAddressesControlUnit.Get_FB),
                    (DOF.LeftRight,         (ushort)SendAddressesControlUnit.Get_LR, (ushort)RecieveAddressesControlUnit.Get_LR),
                    (DOF.UpDown,            (ushort)SendAddressesControlUnit.Get_UD, (ushort)RecieveAddressesControlUnit.Get_UD),
                    (DOF.Yaw,               (ushort)SendAddressesControlUnit.Get_YAW, (ushort)RecieveAddressesControlUnit.Get_YAW),
                    (DOF.Roll,              (ushort)SendAddressesControlUnit.Get_ROLL, (ushort)RecieveAddressesControlUnit.Get_ROLL),
                    (DOF.Pitch,             (ushort)SendAddressesControlUnit.Get_PITCH, (ushort)RecieveAddressesControlUnit.Get_PITCH),
                };

                foreach (var (dof, sendId, recvId) in requests)
                {
                    bool success = false;
                    for (int retry = 0; retry < MaxRetries; retry++)
                    {
                        var tcs = new TaskCompletionSource<float[]>();
                        void Handler(ushort id, byte[] data)
                        {
                            if (id != recvId) return;
                            tcs.TrySetResult(data != null && data.Length > 0 ? LCECommunicationHelper.ConvertBytesToMotorValues(data) : null);
                        }

                        can.RegisterCallback(recvId, Handler);
                        can.SendFrame(sendId);
                        var completed = await Task.WhenAny(tcs.Task, Task.Delay(PullTimeoutMs));
                        can.UnregisterCallback(recvId, Handler);

                        if (completed == tcs.Task && tcs.Task.Result != null)
                        {
                            _tempDirectionBuffer[dof] = tcs.Task.Result;
                            MainWindowViewModel.Instance.LogToControlBox($"[{dof}] Pull OK (try {retry + 1})");
                            success = true;
                            break;
                        }
                        MainWindowViewModel.Instance.LogToControlBox($"[{dof}] Pull retry {retry + 1}");
                    }

                    if (!success)
                    {
                        MainWindowViewModel.Instance.LogToControlBox($"[{dof}] PULL FAILED → aborting");
                        return;
                    }
                }
                LoadedDirectionValue = BuildDirectionValues(_tempDirectionBuffer);
                MainWindowViewModel.Instance.LogToControlBox("Direction pull SUCCESSFUL");
            }
            finally
            {
                PullFromROV_Enabled = true; // Re-enable UI
                ControllerHandlerViewModel.Instance.SendControllerValues = originalSendControllerToggle;
            }
        }

        // --- ROV Apply Logic ---
        private async void ApplyDirectionValuesToLCE()
        {
            if (LoadedDirectionValue == null || can == null || !can.IsOpen)
            {
                MainWindowViewModel.Instance.LogToControlBox("Cannot Apply: Data null or CAN closed");
                return;
            }
            bool originalSendControllerToggle = ControllerHandlerViewModel.Instance.SendControllerValues;
            ControllerHandlerViewModel.Instance.SendControllerValues = false; // Ensure controller values are not sent during pull
            ApplyToROV_Enabled = false; // Disable UI

            try
            {
                var requests = new (DOF dof, ushort sendId)[]
                {
                    (DOF.ForwardsBackwards, (ushort)SendAddressesControlUnit.Set_FB),
                    (DOF.LeftRight,         (ushort)SendAddressesControlUnit.Set_LR),
                    (DOF.UpDown,            (ushort)SendAddressesControlUnit.Set_UD),
                    (DOF.Yaw,               (ushort)SendAddressesControlUnit.Set_YAW),
                    (DOF.Roll,              (ushort)SendAddressesControlUnit.Set_ROLL),
                    (DOF.Pitch,             (ushort)SendAddressesControlUnit.Set_PITCH),
                };

                foreach (var (dof, sendId) in requests)
                {
                    bool success = false;
                    for (int retry = 0; retry < MaxRetries; retry++)
                    {
                        var tcs = new TaskCompletionSource<bool>();
                        void Handler(ushort id, byte[] data) { if (id == sendId) tcs.TrySetResult(true); }

                        can.RegisterCallback(sendId, Handler);
                        byte[] payload = LCECommunicationHelper.ConvertMotorValuesToBytes(LoadedDirectionValue.Get(dof).GetAsArray());
                        can.SendFrame(sendId, payload);

                        var completed = await Task.WhenAny(tcs.Task, Task.Delay(PullTimeoutMs));
                        can.UnregisterCallback(sendId, Handler);

                        if (completed == tcs.Task)
                        {
                            MainWindowViewModel.Instance.LogToControlBox($"[{dof}] Apply OK (try {retry + 1})");
                            success = true;
                            break;
                        }
                    }

                    if (!success)
                    {
                        MainWindowViewModel.Instance.LogToControlBox($"[{dof}] APPLY FAILED → aborting");
                        return;
                    }
                }

                // Wait for Save Confirmation
                var finalTcs = new TaskCompletionSource<bool>();
                void FinalHandler(ushort id, byte[] data) { if (id == AllDofSavedRx) finalTcs.TrySetResult(true); }
                can.RegisterCallback(AllDofSavedRx, FinalHandler);

                var finalCompleted = await Task.WhenAny(finalTcs.Task, Task.Delay(PullTimeoutMs * 2));
                can.UnregisterCallback(AllDofSavedRx, FinalHandler);

                MainWindowViewModel.Instance.LogToControlBox(finalCompleted == finalTcs.Task
                    ? "Direction APPLY SUCCESSFUL (saved on Pico)"
                    : "Direction APPLY FAILED → No final save confirmation");
            }
            finally
            {
                ApplyToROV_Enabled = true; // Re-enable UI
                ControllerHandlerViewModel.Instance.SendControllerValues = originalSendControllerToggle;
            }
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

        private DirectionValues BuildDirectionValues(Dictionary<DOF, float[]> data)
        {
            var result = DirectionValues.GetInvalidDirectionValues();
            foreach (var kv in data) result.UpdateSingleDOF(kv.Key, kv.Value);
            return result;
        }

        // --- Test Motor Logic ---
        private int _testThrust = 1560;

        public int TestThrust
        {
            get => _testThrust;
            set => Set(ref _testThrust, Math.Clamp(value, 1100, 1900));
        }

        public async void TestMotorCommand(object motorIndexStr)
        {
            if (motorIndexStr == null || !int.TryParse(motorIndexStr.ToString(), out int motorIndex)) return;

            // Toggle states during the brief send operation
            PullFromROV_Enabled = false;
            ApplyToROV_Enabled = false;
            TestMotor_Enabled = false;

            try
            {
                ushort duration = 150;
                if (can != null)
                {
                    int[] values = new int[6];
                    for (int i = 0; i < values.Length; i++)
                        values[i] = 1500;

                    // Override the selected motor
                    values[motorIndex] = TestThrust;
                    byte[] motorBytes = PwmArrayToByteArray(values);
                    byte[] payload = new byte[motorBytes.Length + 1];
                    Array.Copy(motorBytes, payload, motorBytes.Length);
                    payload[^1] = (byte)duration;

                    can.SendFrame((ushort)SendAddressesControlUnit.TestMotor, payload);

                    // Artificial delay to prevent spamming the Pico during the motor pulse
                    await Task.Delay(200);
                }
            }
            finally
            {
                PullFromROV_Enabled = true;
                ApplyToROV_Enabled = true;
                TestMotor_Enabled = true;
            }
        }

        public void TestMotorCommandCallback(ushort id, byte[] data)
        {
            MainWindowViewModel.Instance.LogToControlBox("Testing Motor Successful");
        }
    }
}