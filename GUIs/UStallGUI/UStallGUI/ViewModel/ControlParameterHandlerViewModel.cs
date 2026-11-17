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

        public RelayCommand<object> TestMotor { get; }

        private readonly UsbCANWrapper can;

        // --- Constructor ---
        public ControlParameterHandlerViewModel(UsbCANWrapper can)
        {
            this.can = can;
            
            TestMotor = new RelayCommand<object>(TestMotorCommand, _ => TestMotor_Enabled);

            can.RegisterCallback((ushort)RecieveAddressesControlUnit.TestMotor, TestMotorCommandCallback);
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

            TestMotor_Enabled = false;

            try
            {
                ushort duration = 150;
                if (can != null)
                {
                    int[] values = new int[6];
                    for (int i = 0; i < values.Length; i++)
                        values[i] = 1500;

                    // Override the selected motor (scaled by global MaxThrusterSpeedPercent)
                    double factor = Math.Clamp(ControllerHandlerViewModel.MaxThrusterSpeedPercent, 0, 100) / 100.0;
                    values[motorIndex] = 1500 + (int)Math.Round((TestThrust - 1500) * factor);
                    byte[] motorBytes = PwmArrayToByteArray(values);
                    byte[] payload = new byte[motorBytes.Length + 1];
                    Array.Copy(motorBytes, payload, motorBytes.Length);
                    payload[^1] = (byte)duration;

                    can.SendFrame((ushort)SendAddressesControlUnit.TestMotor, payload);

                    // Brief delay to prevent overlapping motor test pulses
                    await Task.Delay(200);
                }
            }
            finally
            {
                TestMotor_Enabled = true;
            }
        }

        public void TestMotorCommandCallback(ushort id, byte[] data)
        {
            if (data != null && data.Length > 0 && data[0] == 1)
            {
                MainWindowViewModel.Instance.LogToControlBox("Testing Motor Successful");
            }
            else
            {
                MainWindowViewModel.Instance.LogToControlBox("Testing Motor Rejected or Failed (check Pixhawk status)");
            }
        }
    }
}
