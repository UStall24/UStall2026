using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.Command;
using System;
using System.Collections.Generic;
using UStallGUI.Helpers;

namespace UStallGUI.ViewModel
{
    public enum LceConnectionStatus
    {
        None = 0,
        Connecting = 1,
        Connected = 2,
        ErrorConnecting = 3,
        ClosingSuccessful = 4,
        ClosingFailed = 5
    }

    public class MainWindowViewModel : ObservableObject
    {
        public static MainWindowViewModel Instance { get; private set; }

        private readonly ConsoleLog controlBoxLog = new();

        private string connectionStatusLCE;
        private string connectionStatusController = "Not Connected";
        private string controlBoxConsoleText;
        private string selectedGripper = "0";
        private string rpiTemperature;

        private LceConnectionStatus lceConnectionStatus = LceConnectionStatus.None;

        public RelayCommand OnButtonPress_Click { get; set; }

        private void OnButtonPress()
        {
            Console.WriteLine("Button Pressed!");
        }

        // Keyboard mapping: key -> gripper command
        private readonly IReadOnlyDictionary<string, GripperAssignment> keyToGripperMap =
            new Dictionary<string, GripperAssignment>(StringComparer.OrdinalIgnoreCase)
            {
                ["T"] = GripperAssignment.Gripper2_Servo1Plus,
                ["G"] = GripperAssignment.Gripper2_Servo1Minus,
                ["Z"] = GripperAssignment.Gripper2_Servo2Plus,
                ["H"] = GripperAssignment.Gripper2_Servo2Minus,
                ["B"] = GripperAssignment.Gripper2_PumpOn,

                ["U"] = GripperAssignment.Gripper1_Servo1Plus,
                ["J"] = GripperAssignment.Gripper1_Servo1Minus,
                ["I"] = GripperAssignment.Gripper1_Servo2Plus,
                ["K"] = GripperAssignment.Gripper1_Servo2Minus,
                ["O"] = GripperAssignment.Gripper1_Servo3Plus,
                ["L"] = GripperAssignment.Gripper1_Servo3Minus,
            };

        private static readonly IReadOnlyDictionary<LceConnectionStatus, string> LceStatusMessages =
            new Dictionary<LceConnectionStatus, string>
            {
                [LceConnectionStatus.None] = "No active connection",
                [LceConnectionStatus.Connecting] = "Connecting...",
                [LceConnectionStatus.Connected] = "Connected",
                [LceConnectionStatus.ErrorConnecting] = "Error Connecting",
                [LceConnectionStatus.ClosingSuccessful] = "Closing Successful",
                [LceConnectionStatus.ClosingFailed] = "Closing Failed"
            };

        public MainWindowViewModel()
        {
            Instance = this;

            // Initial state
            UpdateConnectionStatusLCE(LceConnectionStatus.None);
            InitKeyboardInputHandling();
            OnButtonPress_Click = new RelayCommand(OnButtonPress);
        }

        #region Public Properties

        public string ConnectionStatusLCE
        {
            get => connectionStatusLCE;
            private set => Set(ref connectionStatusLCE, value);
        }

        public string ConnectionStatusController
        {
            get => connectionStatusController;
            set => Set(ref connectionStatusController, value);
        }

        public string ControlBoxConsoleText
        {
            get => controlBoxConsoleText;
            private set => Set(ref controlBoxConsoleText, value);
        }

        public string SelectedGripper
        {
            get => selectedGripper;
            set => Set(ref selectedGripper, value);
        }

        public string RPiTemperature
        {
            get => rpiTemperature;
            set => Set(ref rpiTemperature, value);
        }

        #endregion Public Properties

        #region LCE Connection Status

        public void UpdateConnectionStatusLCE(LceConnectionStatus status)
        {
            if (status == lceConnectionStatus)
                return;

            lceConnectionStatus = status;

            if (LceStatusMessages.TryGetValue(status, out var message))
            {
                ConnectionStatusLCE = message;
                LogToControlBox($"LCE Connection Status: {message}");
            }
        }

        #endregion LCE Connection Status

        #region Logging

        public void LogToControlBox(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            controlBoxLog.Add(message.Trim());
            ControlBoxConsoleText = controlBoxLog.CurrentText;
        }

        #endregion Logging

        #region Keyboard Input Handling

        public RelayCommand<string> KeyPressedCommand { get; private set; }

        private void InitKeyboardInputHandling()
        {
            KeyPressedCommand = new RelayCommand<string>(OnKeyPressed);
        }

        public void OnKeyPressed(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return;

            Console.WriteLine($"{key} pressed!");

            if (!keyToGripperMap.TryGetValue(key, out var assignment))
            {
                // Unknown key – silently ignore or log if you want.
                return;
            }

            if (App.Instance.AccessoryBoxViewModelGetter == null)
            {
                LogToControlBox("AccessoryBoxViewModel.Instance is null. Cannot execute gripper command.");
                return;
            }

            App.Instance.AccessoryBoxViewModelGetter.ExecuteGripperCommand(assignment);
        }

        #endregion Keyboard Input Handling
    }
}