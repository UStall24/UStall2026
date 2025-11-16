using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.Command;
using System;
using UStallGUI.Helpers;

namespace UStallGUI.ViewModel
{
    public class MainWindowViewModel : ObservableObject
    {
        public static MainWindowViewModel Instance;

        private string connectionStatusLCE;
        private string connectionStatusController = "Not Connected";

        public MainWindowViewModel()
        {
            Instance = this;
            UpdateConnectionStatusLCE(0);
            _InitKeyboardInputHandling();
        }

        private readonly string[] lce_connection_messages = { "No active connection", "Connecting...", "Connected", "Error Connecting", "Closing Successful", "Closing Failed" };

        private int lce_connection_index = 0;

        public void UpdateConnectionStatusLCE(int status)
        {
            if (status >= 0 && status < lce_connection_messages.Length && status != lce_connection_index)
            {
                lce_connection_index = status;
                ConnectionStatusLCE = lce_connection_messages[lce_connection_index];
                ControlBoxConsoleText = $"LCE Connection Status: {lce_connection_messages[lce_connection_index]}";
            }
        }

        private readonly ConsoleLog controlBoxLog = new();
        private readonly ConsoleLog accessoryBoxLog = new();

        private string controlBoxConsoleText;

        public string ControlBoxConsoleText
        {
            get => controlBoxConsoleText;
            set
            {
                controlBoxLog.Add(value);
                Set(ref controlBoxConsoleText, controlBoxLog.CurrentText);
            }
        }

        private string accessoryBoxConsoleText;

        public string AccessoryBoxConsoleText
        {
            //get => accessoryBoxConsoleText;
            set
            {
                //accessoryBoxLog.Add(value);
                //Set(ref accessoryBoxConsoleText, accessoryBoxLog.CurrentText);
                ControlBoxConsoleText = value; // To be changed cause i removed the 2nd Console
            }
        }

        private string _selectedGripper = "0";
        public string SelectedGripper { get => _selectedGripper; set => Set(ref _selectedGripper, value); }
        private string _rpiTemperature;
        public string RPiTemperature { get => _rpiTemperature; set => Set(ref _rpiTemperature, value); }

        // Textfield for the Statusbar Bindings
        public string ConnectionStatusLCE
        {
            get => connectionStatusLCE;
            set => Set(ref connectionStatusLCE, value);
        }

        public string ConnectionStatusController
        {
            get => connectionStatusController;
            set => Set(ref connectionStatusController, value);
        }

        #region Keyboard Input Handling

        public RelayCommand<string> KeyPressedCommand { get; private set; }

        private void _InitKeyboardInputHandling()
        {
            KeyPressedCommand = new RelayCommand<string>(OnKeyPressed);
        }

        public void OnKeyPressed(string key)
        {
            Console.WriteLine($"{key} pressed!");

            switch (key)
            {
                case "T":
                    AccessoryBoxViewModel.Instance.ExecuteGripperCommand(GripperAssignment.Gripper2_Servo1Plus);
                    break;

                case "G":
                    AccessoryBoxViewModel.Instance.ExecuteGripperCommand(GripperAssignment.Gripper2_Servo1Minus);
                    break;

                case "Z":
                    AccessoryBoxViewModel.Instance.ExecuteGripperCommand(GripperAssignment.Gripper2_Servo2Plus);
                    break;

                case "H":
                    AccessoryBoxViewModel.Instance.ExecuteGripperCommand(GripperAssignment.Gripper2_Servo2Minus);
                    break;

                case "B":
                    AccessoryBoxViewModel.Instance.ExecuteGripperCommand(GripperAssignment.Gripper2_PumpOn);
                    break;

                case "U":
                    AccessoryBoxViewModel.Instance.ExecuteGripperCommand(GripperAssignment.Gripper1_Servo1Plus);
                    break;

                case "J":
                    AccessoryBoxViewModel.Instance.ExecuteGripperCommand(GripperAssignment.Gripper1_Servo1Minus);
                    break;

                case "I":
                    AccessoryBoxViewModel.Instance.ExecuteGripperCommand(GripperAssignment.Gripper1_Servo2Plus);
                    break;

                case "K":
                    AccessoryBoxViewModel.Instance.ExecuteGripperCommand(GripperAssignment.Gripper1_Servo2Minus);
                    break;

                case "O":
                    AccessoryBoxViewModel.Instance.ExecuteGripperCommand(GripperAssignment.Gripper1_Servo3Plus);
                    break;

                case "L":
                    AccessoryBoxViewModel.Instance.ExecuteGripperCommand(GripperAssignment.Gripper1_Servo3Minus);
                    break;
            }
        }

        #endregion Keyboard Input Handling
    }
}