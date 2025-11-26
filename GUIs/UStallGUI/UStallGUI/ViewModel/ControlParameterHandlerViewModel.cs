using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.Command;
using Microsoft.Win32;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using UStallGUI.Helpers;
using UStallGUI.Model;

namespace UStallGUI.ViewModel
{
    public class ControlParameterHandlerViewModel : ObservableObject
    {
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

        public RelayCommand PullDirectionValuesFromLCE_Command { get; }
        public RelayCommand ApplyDirectionValuesToLCE_Command { get; }
        public RelayCommand LoadDirectionValuesFromPC_Command { get; }
        public RelayCommand SaveDirectionValuesToPC_Command { get; }

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
                        case 0:
                            CurrentShownMotorValues = LoadedDirectionValue.Translation.Z_Axis;
                            break;

                        case 1:
                            CurrentShownMotorValues = LoadedDirectionValue.Translation.X_Axis;
                            break;

                        case 2:
                            CurrentShownMotorValues = LoadedDirectionValue.Translation.Y_Axis;
                            break;

                        case 3:
                            CurrentShownMotorValues = LoadedDirectionValue.Rotation.Z_Axis;
                            break;

                        case 4:
                            CurrentShownMotorValues = LoadedDirectionValue.Rotation.X_Axis;
                            break;

                        case 5:
                            CurrentShownMotorValues = LoadedDirectionValue.Rotation.Y_Axis;
                            break;
                    }
                    ;
                }
            }
        }

        private readonly UsbCANWrapper can;

        public ControlParameterHandlerViewModel(UsbCANWrapper can)
        {
            this.can = can;
            CurrentShownMotorValues = MotorValues.GetDefaultMotorValues();
            LoadDirectionValuesFromPC_Command = new RelayCommand(LoadDirectionValuesFromPC);
            SaveDirectionValuesToPC_Command = new RelayCommand(SaveDirectionValuesToPC);
            PullDirectionValuesFromLCE_Command = new RelayCommand(PullDirectionValuesFromLCE);
            ApplyDirectionValuesToLCE_Command = new RelayCommand(ApplyDirectionValuesToLCE);
        }

        private void LoadDirectionValuesFromPC()
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "JSON files (*.json)|*.json";
            openFileDialog.Title = "Select a JSON file";

            if (openFileDialog.ShowDialog() == true)
            {
                string filePath = openFileDialog.FileName;

                DirectionValues directionValues = ConfigLoader.LoadControllerParameters(filePath);
                if (directionValues != null) LoadedDirectionValue = directionValues;
                else MessageBox.Show("That is not a vaild DirectionValue file");
            }
        }

        private void SaveDirectionValuesToPC()
        {
            SaveFileDialog saveFileDialog = new SaveFileDialog
            {
                Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                Title = "Save DirectionValue File",
                FileName = "directionValues.json" // Set the default filename here
            };

            // Show the dialog and check if the user clicked "Save"
            if (saveFileDialog.ShowDialog() == true)
            {
                string filePath = saveFileDialog.FileName;
                bool successful = ConfigLoader.UpdateConfigurationFile(LoadedDirectionValue, filePath);
                if (successful) MessageBox.Show("File saved successfully");
                else MessageBox.Show("Saving Failed");
            }
        }

        private async void PullDirectionValuesFromLCE()
        {
            if (can != null)
            {
                byte[] addresses = { 0x11, 0x12, 0x13, 0x14, 0x15, 0x16 };
                List<float[]> directionValues = new();
                async void OnSuccessfulResponse(ushort id, byte[] data)
                {
                    if (data.Length != 0)
                    {
                        float[] floatValues = LCECommunicationHelper.ConvertBytesToMotorValues(data);
                        directionValues.Add(floatValues);
                    }
                    else
                    {
                        MainWindowViewModel.Instance.LogToControlBox("Requesting Direction Values from LCE failed - Invalid Data");
                        return;
                    }
                }

                for (int i = 0; i < 6; i++)
                {
                    can.RegisterCallback(addresses[i], OnSuccessfulResponse);
                }
                can.SendFrame(0x10, []);
                await Task.Delay(250);
                for (int i = 0; i < 6; i++)
                {
                    can.UnregisterCallback(addresses[i], OnSuccessfulResponse);
                }
                DirectionValues recievedDirectionValues = DirectionValues.GetDefaultDirectionValues();
                recievedDirectionValues.UpdateSingleAxis(Direction.Translation, Axis.Z_Axis, directionValues[0]);
                recievedDirectionValues.UpdateSingleAxis(Direction.Translation, Axis.X_Axis, directionValues[1]);
                recievedDirectionValues.UpdateSingleAxis(Direction.Translation, Axis.Y_Axis, directionValues[2]);
                recievedDirectionValues.UpdateSingleAxis(Direction.Rotation, Axis.Z_Axis, directionValues[3]);
                recievedDirectionValues.UpdateSingleAxis(Direction.Rotation, Axis.X_Axis, directionValues[4]);
                recievedDirectionValues.UpdateSingleAxis(Direction.Rotation, Axis.Y_Axis, directionValues[5]);

                LoadedDirectionValue = recievedDirectionValues;

                MainWindowViewModel.Instance.LogToControlBox("Requesting Direction Values from LCE successful");
            }
            else MainWindowViewModel.Instance.LogToControlBox("Requesting Direction Values from LCE failed - Serial Port is not open");
        }

        private async void ApplyDirectionValuesToLCE()
        {
            if (can != null)
            {
                byte[] addresses = { 0x21, 0x22, 0x23, 0x24, 0x25, 0x26 };
                byte[][] payload = new byte[6][];
                payload[0] = (LCECommunicationHelper.ConvertMotorValuesToBytes(LoadedDirectionValue.Translation.Z_Axis.GetAsArray()));
                payload[1] = (LCECommunicationHelper.ConvertMotorValuesToBytes(LoadedDirectionValue.Translation.X_Axis.GetAsArray()));
                payload[2] = (LCECommunicationHelper.ConvertMotorValuesToBytes(LoadedDirectionValue.Translation.Y_Axis.GetAsArray()));
                payload[3] = (LCECommunicationHelper.ConvertMotorValuesToBytes(LoadedDirectionValue.Rotation.Z_Axis.GetAsArray()));
                payload[4] = (LCECommunicationHelper.ConvertMotorValuesToBytes(LoadedDirectionValue.Rotation.X_Axis.GetAsArray()));
                payload[5] = (LCECommunicationHelper.ConvertMotorValuesToBytes(LoadedDirectionValue.Rotation.Y_Axis.GetAsArray()));

                async void OnSuccessfulResponse(ushort id, byte[] data)
                {
                    if (data.Length != 0)
                    {
                        MainWindowViewModel.Instance.LogToControlBox("Writing Direction Values to LCE successful");
                    }
                    else
                    {
                        MainWindowViewModel.Instance.LogToControlBox("Writing Direction Values to LCE failed - Invalid Data");
                        return;
                    }
                }
                can.RegisterCallback(0x20, OnSuccessfulResponse);
                for (int i = 0; i < 6; i++)
                {
                    can.SendFrame(addresses[i], payload[i]);
                }
                await Task.Delay(100);
                can.UnregisterCallback(0x20, OnSuccessfulResponse);
            }
            else MainWindowViewModel.Instance.LogToControlBox("Writing Direction Values to LCE failed - Serial Port is not open");
        }
    }
}